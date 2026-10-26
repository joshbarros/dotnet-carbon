using Carbon.Api.Data;
using Carbon.Api.Modules.Purchases;
using Carbon.Api.Modules.Batches;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CARBON_TEST_DB")
        ?? "Host=localhost;Port=5478;Database=carbon_test;Username=carbon;Password=carbon";

    private static readonly DbContextOptions<CarbonDbContext> Options =
        new DbContextOptionsBuilder<CarbonDbContext>().UseNpgsql(ConnectionString).Options;

    public static CarbonDbContext Create() => new(Options);

    public static async Task<CarbonDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"Certificates\", \"TonneHolds\", \"Purchases\", \"Batches\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Batch> AddBatchAsync(CarbonDbContext db, int tonnesIssued, long priceCents = 1_500)
    {
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

    public static async Task<Purchase> AddPurchaseAsync(CarbonDbContext db, Batch batch, string reference, int tonnes, PurchaseStatus status)
    {
        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
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
