using System.Collections.Concurrent;
using Carbon.Api.Messaging;
using Carbon.Api.Modules.Batches;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Carbon.Api.Tests;

public class BatchesConcurrencyTests
{
    [Fact]
    public async Task LastTonne_TwoConcurrentPurchases_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, tonnesIssued: 1);

        var outcomes = new ConcurrentBag<(Guid Purchase, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var purchaseId = Guid.CreateVersion7();
            var handler = new BatchesHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveTonnes(purchaseId, batch.Id, 1, $"ORG-{n}"));
            outcomes.Add((purchaseId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, tonnesIssued: 4);
        var purchaseId = Guid.CreateVersion7();
        var handler = new BatchesHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveTonnes(purchaseId, batch.Id, 3, "ORG-1"));

        // Two different commands (poison compensation and a reversal) both ask for the same tonnes back.
        var first = await new BatchesHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseTonnes(purchaseId, "poison"));
        var second = await new BatchesHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseTonnes(purchaseId, "reversal"));

        var remaining = await TestDatabase.Create().Batches.Where(s => s.Id == batch.Id).Select(s => s.TonnesLeft).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, tonnesIssued: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveTonnes(Guid.CreateVersion7(), batch.Id, 2, "ORG-1");

        var first = await new BatchesHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new BatchesHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Batches.Where(s => s.Id == batch.Id).Select(s => s.TonnesLeft).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
