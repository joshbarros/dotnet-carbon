using Carbon.Api.Data;
using Carbon.Api.Messaging;
using Carbon.Api.Modules.Batches;
using Carbon.Api.Modules.Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Carbon.Api.Modules.Purchases;

public enum PurchaseStatus { Requested, Held, Allocated, Reversed, Rejected }

public class Purchase
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public string BatchCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Tonnes { get; set; }
    public long TotalCents { get; set; }
    public PurchaseStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? CertificateNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? TonnesReleasedAt { get; set; }
}

public class PurchaseOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the reversal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreatePurchaseRequest(string? BatchCode, string? Reference, long Tonnes);

public record PurchaseDto(
    Guid Id, string BatchCode, string Reference, int Tonnes, long TotalCents, string Status,
    string? Reason, string? CertificateNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? TonnesReleasedAt)
{
    public static PurchaseDto From(Purchase b) => new(
        b.Id, b.BatchCode, b.Reference, b.Tonnes, b.TotalCents, b.Status.ToString(),
        b.Reason, b.CertificateNumber, b.CreatedAt, b.UpdatedAt, b.TonnesReleasedAt);
}

/// <summary>HTTP side of the saga: writes the purchase and its first outbox row in one local transaction.</summary>
public class PurchaseService(CarbonDbContext db)
{
    public async Task<Purchase> CreateAsync(CreatePurchaseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BatchCode))
            throw new DomainException(400, "Batch code is required.");
        var error = PurchaseRules.Validate(request.Reference, request.Tonnes);
        if (error is not null)
            throw new DomainException(400, error);

        var batch = await db.Batches.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.BatchCode.Trim(), ct)
            ?? throw new DomainException(404, $"Batch '{request.BatchCode}' does not exist.");

        var tonnes = (int)request.Tonnes;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            BatchId = batch.Id,
            BatchCode = batch.Code,
            Reference = reference,
            Tonnes = tonnes,
            TotalCents = PurchaseRules.TotalCents(batch.PriceCents, tonnes),
            Status = PurchaseStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Purchases.Add(purchase);
        db.Enqueue("purchases", Topology.ReserveKey, purchase.Id, new ReserveTonnes(purchase.Id, batch.Id, tonnes, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {batch.Code}.");
        }
        await tx.CommitAsync(ct);
        return purchase;
    }

    /// <summary>Moves Held or Allocated to Reversed and asks Batches to give the tonnes back. The certificate is not touched.</summary>
    public async Task<Purchase> ReverseAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Purchases
            .Where(b => b.Id == id && (b.Status == PurchaseStatus.Held || b.Status == PurchaseStatus.Allocated))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PurchaseStatus.Reversed)
                .SetProperty(b => b.Reason, "Reversed by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Purchase not found.");
            throw new DomainException(409, $"A purchase in status {current.Status} cannot be reversed.");
        }

        db.Enqueue("purchases", Topology.ReleaseKey, id, new ReleaseTonnes(id, "reversal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Purchases.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a purchase forward. Each step runs only from the status it expects.</summary>
public class PurchasesHandler(CarbonDbContext db, IOptions<PurchaseOptions> options) : IMessageHandler
{
    public const string Name = "purchases";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<TonnesHeld>(d.Body).PurchaseId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<TonnesShort>(d.Body), ct),
        Topology.IssuedKey => MarkAllocatedAsync(d.MessageId, Json.Parse<CertificateIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<CertificateRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<TonnesReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Purchases does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid purchaseId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Purchases
            .Where(b => b.Id == purchaseId && b.Status == PurchaseStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PurchaseStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, TonnesShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Purchases
            .Where(b => b.Id == m.PurchaseId && b.Status == PurchaseStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PurchaseStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedTonnes} tonnes wanted, {m.TonnesLeft} left.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a reversal got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkAllocatedAsync(Guid messageId, CertificateIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Purchases
            .Where(b => b.Id == m.PurchaseId && b.Status == PurchaseStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PurchaseStatus.Allocated)
                .SetProperty(b => b.CertificateNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            purchaseIdForOrderCheck: m.PurchaseId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, CertificateRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Purchases
            .Where(b => b.Id == m.PurchaseId && (b.Status == PurchaseStatus.Held || b.Status == PurchaseStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PurchaseStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, TonnesReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Purchases
            .Where(b => b.Id == m.PurchaseId && b.TonnesReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.TonnesReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? purchaseIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && purchaseIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Purchases.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == PurchaseStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
