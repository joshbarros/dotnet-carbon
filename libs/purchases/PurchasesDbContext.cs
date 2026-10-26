using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Purchases;

public class PurchasesDbContext(DbContextOptions<PurchasesDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "purchases";

    public DbSet<Purchase> Purchases => Set<Purchase>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Purchase>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(PurchaseRules.MaxReferenceLength);
            e.HasIndex(x => new { x.BatchId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
