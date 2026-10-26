using Carbon.Api.Messaging;
using Carbon.Api.Modules.Purchases;
using Carbon.Api.Modules.Batches;
using Carbon.Api.Modules.Certificates;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Api.Data;

public class CarbonDbContext(DbContextOptions<CarbonDbContext> options) : DbContext(options)
{
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<TonneHold> Holds => Set<TonneHold>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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

        b.Entity<Purchase>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(PurchaseRules.MaxReferenceLength);
            e.HasIndex(x => new { x.BatchId, x.Reference }).IsUnique();
            e.HasOne<Batch>().WithMany().HasForeignKey(x => x.BatchId);
        });

        b.Entity<Certificate>(e =>
        {
            e.ToTable("Certificates");
            e.HasKey(x => x.PurchaseId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
