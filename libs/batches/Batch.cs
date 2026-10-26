namespace Carbon.Batches;

public class Batch
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Standard { get; set; } = "";
    public string Country { get; set; } = "";
    public DateTimeOffset IssuedOn { get; set; }
    public long PriceCents { get; set; }
    public int TonnesIssued { get; set; }
    public int TonnesLeft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class TonneHold
{
    public Guid PurchaseId { get; set; }
    public Guid BatchId { get; set; }
    public int Tonnes { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
