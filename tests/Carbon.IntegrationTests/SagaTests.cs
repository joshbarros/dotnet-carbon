using Carbon.Purchases;
using Carbon.Certificates;
using Carbon.Shared;
using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Carbon.IntegrationTests;

public class SagaTests
{
    private static PurchasesHandler Handler(PurchasesDbContext db) => new(db, Options.Create(new PurchaseOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var purchase = await TestDatabase.AddPurchaseAsync(batch, "ORG-POISON-1", 2, PurchaseStatus.Held);
        var held = new TonnesHeld(purchase.Id, purchase.OwnerId, batch.Id, 2, purchase.Reference);

        await using var docs = TestDatabase.Certificates();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new CertificatesHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Certificates();
        var doc = await check.Certificates.SingleAsync(d => d.PurchaseId == purchase.Id);
        Assert.Equal(CertificateStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(purchase.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Reversal_ThenFinalMark_LeavesPurchaseReversedAndCertificateIssued()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var owner = Guid.CreateVersion7();
        var purchase = await TestDatabase.AddPurchaseAsync(batch, "ORG-1", 2, PurchaseStatus.Held, owner);

        // The certificate is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Certificates())
            await new CertificatesHandler(docs).IssueAsync(Guid.CreateVersion7(), new TonnesHeld(purchase.Id, owner, batch.Id, 2, purchase.Reference));

        // The owner reverses first.
        await using (var purchases = TestDatabase.Purchases())
            await new PurchaseService(purchases, new FixedCatalog(batch), new TestUser(owner)).ReverseAsync(purchase.Id, default);

        await using var late = TestDatabase.Purchases();
        var mark = await Handler(late).MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(purchase.Id, "CC-TEST"));

        await using var check = TestDatabase.Purchases();
        await using var checkDocs = TestDatabase.Certificates();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(PurchaseStatus.Reversed, (await check.Purchases.SingleAsync()).Status);
        Assert.Equal(CertificateStatus.Issued, (await checkDocs.Certificates.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var held = await TestDatabase.AddPurchaseAsync(batch, "ORG-A", 1, PurchaseStatus.Held);
        var rejected = await TestDatabase.AddPurchaseAsync(batch, "ORG-B", 1, PurchaseStatus.Rejected);
        var requested = await TestDatabase.AddPurchaseAsync(batch, "ORG-C", 1, PurchaseStatus.Requested);

        await using (var a = TestDatabase.Purchases())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(held.Id, "CC-1")));
        await using (var b = TestDatabase.Purchases())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(rejected.Id, "CC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Purchases())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkAllocatedAsync(Guid.CreateVersion7(), new CertificateIssued(requested.Id, "CC-3")));

        await using var check = TestDatabase.Purchases();
        var statuses = (await check.Purchases.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(PurchaseStatus.Allocated, statuses["ORG-A"]);
        Assert.Equal(PurchaseStatus.Rejected, statuses["ORG-B"]);
        Assert.Equal(PurchaseStatus.Requested, statuses["ORG-C"]);
    }

    [Fact]
    public async Task Reversal_OfARejectedPurchase_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var owner = Guid.CreateVersion7();
        var purchase = await TestDatabase.AddPurchaseAsync(batch, "ORG-1", 1, PurchaseStatus.Rejected, owner);

        await using var db = TestDatabase.Purchases();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PurchaseService(db, new FixedCatalog(batch), new TestUser(owner)).ReverseAsync(purchase.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Reversal_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var purchase = await TestDatabase.AddPurchaseAsync(batch, "ORG-1", 1, PurchaseStatus.Held);

        await using var db = TestDatabase.Purchases();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PurchaseService(db, new FixedCatalog(batch), new TestUser(Guid.NewGuid())).ReverseAsync(purchase.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Purchases();
        Assert.Equal(PurchaseStatus.Held, (await check.Purchases.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanReverseAnyonesPurchase()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var purchase = await TestDatabase.AddPurchaseAsync(batch, "ORG-1", 1, PurchaseStatus.Held);

        await using var db = TestDatabase.Purchases();
        var reversed = await new PurchaseService(db, new FixedCatalog(batch), new TestUser(Guid.NewGuid(), staff: true)).ReverseAsync(purchase.Id, default);
        Assert.Equal(PurchaseStatus.Reversed, reversed.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Purchases();
        var purchase = await new PurchaseService(db, new FixedCatalog(batch), new TestUser(owner))
            .CreateAsync(new CreatePurchaseRequest(batch.Code, "ORG-1", 2), default);

        await using var check = TestDatabase.Purchases();
        Assert.Equal(owner, (await check.Purchases.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveTonnes>(message.Payload).OwnerId);
        Assert.Equal(PurchaseStatus.Requested, purchase.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var batch = await TestDatabase.AddBatchAsync(5);
        await using var db = TestDatabase.Purchases();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PurchaseService(db, new FixedCatalog(batch), new TestUser(null)).CreateAsync(new CreatePurchaseRequest(batch.Code, "ORG-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownBatch_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Purchases();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PurchaseService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreatePurchaseRequest("NOPE", "ORG-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
