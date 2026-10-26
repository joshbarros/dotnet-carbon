using Carbon.Api.Messaging;
using Carbon.Api.Modules.Purchases;
using Carbon.Api.Modules.Batches;
using Carbon.Api.Modules.Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Carbon.Api.Tests;

public class SagaTests
{
    private static PurchasesHandler Purchases() =>
        new(TestDatabase.Create(), Options.Create(new PurchaseOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, 5);
        var purchase = await TestDatabase.AddPurchaseAsync(db, batch, "ORG-POISON-1", 2, PurchaseStatus.Held);
        var held = new TonnesHeld(purchase.Id, batch.Id, 2, purchase.Reference);

        var handler = new CertificatesHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Certificates.SingleAsync(d => d.PurchaseId == purchase.Id);
        Assert.Equal(CertificateStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Reversal_ThenFinalMark_LeavesPurchaseReversedAndCertificateIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, 5);
        var purchase = await TestDatabase.AddPurchaseAsync(db, batch, "ORG-1", 2, PurchaseStatus.Held);

        // The certificate is issued; the final mark has not landed yet.
        var number = "CC-TEST";
        await new CertificatesHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new TonnesHeld(purchase.Id, batch.Id, 2, purchase.Reference));

        // The reversal gets there first.
        await new PurchaseService(TestDatabase.Create()).ReverseAsync(purchase.Id, default);

        var mark = await Purchases().MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(purchase.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(PurchaseStatus.Reversed, (await check.Purchases.SingleAsync()).Status);
        Assert.Equal(CertificateStatus.Issued, (await check.Certificates.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, 5);
        var held = await TestDatabase.AddPurchaseAsync(db, batch, "ORG-A", 1, PurchaseStatus.Held);
        var rejected = await TestDatabase.AddPurchaseAsync(db, batch, "ORG-B", 1, PurchaseStatus.Rejected);
        var requested = await TestDatabase.AddPurchaseAsync(db, batch, "ORG-C", 1, PurchaseStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Purchases().MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(held.Id, "CC-1")));
        Assert.Equal(MarkOutcome.Skipped, await Purchases().MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(rejected.Id, "CC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Purchases().MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(requested.Id, "CC-3")));

        var statuses = (await TestDatabase.Create().Purchases.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(PurchaseStatus.Allocated, statuses["ORG-A"]);
        Assert.Equal(PurchaseStatus.Rejected, statuses["ORG-B"]);
        Assert.Equal(PurchaseStatus.Requested, statuses["ORG-C"]);
    }

    [Fact]
    public async Task Reversal_OfARejectedPurchase_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(db, 5);
        var purchase = await TestDatabase.AddPurchaseAsync(db, batch, "ORG-1", 1, PurchaseStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new PurchaseService(TestDatabase.Create()).ReverseAsync(purchase.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
