using Carbon.Purchases;
using Carbon.Batches;
using Carbon.Certificates;
using Carbon.Shared.Auth;
using Carbon.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Carbon.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CARBON_TEST_DB")
        ?? "Host=localhost;Port=5478;Database=carbon_test;Username=carbon;Password=carbon";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static PurchasesDbContext Purchases() => new(Options<PurchasesDbContext>(PurchasesDbContext.SchemaName));
    public static BatchesDbContext Batches() => new(Options<BatchesDbContext>(BatchesDbContext.SchemaName));
    public static CertificatesDbContext Certificates() => new(Options<CertificatesDbContext>(CertificatesDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var purchases = Purchases();
        await using var tonnes = Batches();
        await using var certificates = Certificates();
        await purchases.Database.MigrateAsync();
        await tonnes.Database.MigrateAsync();
        await certificates.Database.MigrateAsync();
        await purchases.Database.ExecuteSqlRawAsync(
            "TRUNCATE purchases.\"InboxMessages\", purchases.\"OutboxMessages\", purchases.\"Purchases\", " +
            "tonnes.\"InboxMessages\", tonnes.\"OutboxMessages\", tonnes.\"TonneHolds\", tonnes.\"Batches\", " +
            "certificates.\"InboxMessages\", certificates.\"OutboxMessages\", certificates.\"Certificates\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Batch> AddBatchAsync(int tonnesIssued, long priceCents = 5_000)
    {
        await using var db = Batches();
        var batch = new Batch
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Project",
            Standard = "VCS",
            Country = "Ecuador",
            IssuedOn = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            TonnesIssued = tonnesIssued,
            TonnesLeft = tonnesIssued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        return batch;
    }

    public static async Task<Purchase> AddPurchaseAsync(Batch batch, string reference, int tonnes, PurchaseStatus status, Guid? ownerId = null)
    {
        await using var db = Purchases();
        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            BatchId = batch.Id,
            BatchCode = batch.Code,
            Reference = reference,
            Tonnes = tonnes,
            TotalCents = PurchaseRules.TotalCents(batch.PriceCents, tonnes),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();
        return purchase;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Batch[] batches) : IBatchCatalog
{
    public Task<BatchInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(batches.Where(s => s.Code == code).Select(s => new BatchInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
