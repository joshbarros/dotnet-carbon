using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Carbon.Purchases;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a purchase forward. Each step runs only from the status it expects.</summary>
public class PurchasesHandler(PurchasesDbContext db, IOptions<PurchaseOptions> options) : IMessageHandler
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
