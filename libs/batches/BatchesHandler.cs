using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Batches;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Batch.TonnesLeft and of tonne holds.
/// The purchase is one conditional UPDATE: it matches only while the remaining Tonnes still covers the request.
/// </summary>
public class BatchesHandler(BatchesDbContext db) : IMessageHandler
{
    public const string Name = "batches";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveTonnes>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseTonnes>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Batches does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveTonnes command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Batches
            .Where(s => s.Id == command.BatchId && s.TonnesLeft >= command.Tonnes)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TonnesLeft, x => x.TonnesLeft - command.Tonnes), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new TonneHold
            {
                PurchaseId = command.PurchaseId,
                BatchId = command.BatchId,
                Tonnes = command.Tonnes,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("batches", Topology.HeldKey, command.PurchaseId,
                new TonnesHeld(command.PurchaseId, command.OwnerId, command.BatchId, command.Tonnes, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Batches.Where(s => s.Id == command.BatchId)
                .Select(s => s.TonnesLeft).FirstOrDefaultAsync(ct);
            db.Enqueue("batches", Topology.ShortKey, command.PurchaseId,
                new TonnesShort(command.PurchaseId, command.Reference, command.Tonnes, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held tonnes back exactly once. A second release for the same purchase changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseTonnes command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.PurchaseId == command.PurchaseId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.PurchaseId == command.PurchaseId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Batches
                    .Where(s => s.Id == hold.BatchId && s.TonnesLeft + hold.Tonnes <= s.TonnesIssued)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TonnesLeft, x => x.TonnesLeft + hold.Tonnes), ct);
                db.Enqueue("batches", Topology.ReleasedKey, command.PurchaseId,
                    new TonnesReleased(command.PurchaseId, hold.Tonnes, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
