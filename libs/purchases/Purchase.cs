namespace Carbon.Purchases;

public enum PurchaseStatus { Requested, Held, Allocated, Reversed, Rejected }

public class Purchase
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid BatchId { get; set; }
    public string BatchCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Tonnes { get; set; }
    public long TotalCents { get; set; }
    public PurchaseStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? CertificateNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? TonnesReleasedAt { get; set; }
}

public class PurchaseOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the reversal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreatePurchaseRequest(string? BatchCode, string? Reference, long Tonnes);

public record PurchaseDto(
    Guid Id, Guid OwnerId, string BatchCode, string Reference, int Tonnes, long TotalCents, string Status,
    string? Reason, string? CertificateNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? TonnesReleasedAt)
{
    public static PurchaseDto From(Purchase b) => new(
        b.Id, b.OwnerId, b.BatchCode, b.Reference, b.Tonnes, b.TotalCents, b.Status.ToString(),
        b.Reason, b.CertificateNumber, b.CreatedAt, b.UpdatedAt, b.TonnesReleasedAt);
}
