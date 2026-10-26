using System.Collections.Concurrent;
using Carbon.Batches;
using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Carbon.IntegrationTests;

public class BatchesConcurrencyTests
{
    [Fact]
    public async Task LastTonne_TwoConcurrentPurchases_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(tonnesIssued: 1);

        var outcomes = new ConcurrentBag<(Guid Purchase, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var purchaseId = Guid.CreateVersion7();
            await using var db = TestDatabase.Batches();
            var outcome = await new BatchesHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveTonnes(purchaseId, Guid.NewGuid(), batch.Id, 1, $"ORG-{n}"));
            outcomes.Add((purchaseId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Batches();
        var remaining = await check.Batches.Where(s => s.Id == batch.Id).Select(s => s.TonnesLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Purchase;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.PurchaseId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheTonnesBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(tonnesIssued: 4);
        var purchaseId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Batches())
            await new BatchesHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveTonnes(purchaseId, Guid.NewGuid(), batch.Id, 3, "ORG-1"));

        // Two different commands (poison compensation and a reversal) both ask for the same tonnes back.
        await using var first = TestDatabase.Batches();
        await using var second = TestDatabase.Batches();
        var one = await new BatchesHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseTonnes(purchaseId, "poison"));
        var two = await new BatchesHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseTonnes(purchaseId, "reversal"));

        await using var check = TestDatabase.Batches();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Batches.Where(s => s.Id == batch.Id).Select(s => s.TonnesLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(tonnesIssued: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveTonnes(Guid.CreateVersion7(), Guid.NewGuid(), batch.Id, 2, "ORG-1");

        await using var a = TestDatabase.Batches();
        await using var b = TestDatabase.Batches();
        var first = await new BatchesHandler(a).ReserveAsync(messageId, command);
        var second = await new BatchesHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Batches();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Batches.Where(s => s.Id == batch.Id).Select(s => s.TonnesLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(tonnesIssued: 2);
        await using var db = TestDatabase.Batches();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Batches.Where(s => s.Id == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TonnesLeft, x => x.TonnesLeft - 3)));
    }
}
