using Carbon.Shared;
using Carbon.Shared.Auth;
using Carbon.Shared.Contracts;
using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Carbon.Purchases;

/// <summary>HTTP side of the saga: writes the purchase and its first outbox row in one local transaction.</summary>
public class PurchaseService(PurchasesDbContext db, IBatchCatalog batches, ICurrentUser user)
{
    public async Task<Purchase> CreateAsync(CreatePurchaseRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.BatchCode))
            throw new DomainException(400, "Batch code is required.");
        var error = PurchaseRules.Validate(request.Reference, request.Tonnes);
        if (error is not null)
            throw new DomainException(400, error);

        var batch = await batches.FindAsync(request.BatchCode.Trim(), ct)
            ?? throw new DomainException(404, $"Batch '{request.BatchCode}' does not exist.");

        var tonnes = (int)request.Tonnes;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("purchases", Topology.ReserveKey, purchase.Id, new ReserveTonnes(purchase.Id, ownerId, batch.Id, tonnes, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Purchase> GetAsync(Guid id, CancellationToken ct)
    {
        var purchase = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (purchase is null || !OwnerAccess.CanAccess(user, purchase.OwnerId))
            throw new DomainException(404, "Purchase not found.");
        return purchase;
    }

    public async Task<List<Purchase>> ListAsync(string? batchCode, CancellationToken ct)
    {
        var query = db.Purchases.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see purchases.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(batchCode))
            query = query.Where(b => b.BatchCode == batchCode);
        return await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
    }

    /// <summary>Moves Held or Allocated to Reversed and asks Batches to give the tonnes back. The certificate is not touched.</summary>
    public async Task<Purchase> ReverseAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Purchases.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A purchase in status {current.Status} cannot be reversed.");
        }

        db.Enqueue("purchases", Topology.ReleaseKey, id, new ReleaseTonnes(id, "reversal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Purchases.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
