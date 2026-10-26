using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Batches;

public class BatchesDbContext(DbContextOptions<BatchesDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "tonnes";

    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<TonneHold> Holds => Set<TonneHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Batch>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Batches_Remaining", "\"TonnesLeft\" >= 0 AND \"TonnesLeft\" <= \"TonnesIssued\""));
        });

        b.Entity<TonneHold>(e =>
        {
            e.ToTable("TonneHolds");
            e.HasKey(x => x.PurchaseId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Batch>().WithMany().HasForeignKey(x => x.BatchId);
        });
    }
}
