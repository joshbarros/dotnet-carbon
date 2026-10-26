namespace Carbon.Shared.Contracts;

public record BatchInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Purchases may ask Batches, without referencing it. Batches implements it over its own tables;
/// Purchases never reads another module's schema.
/// </summary>
public interface IBatchCatalog
{
    Task<BatchInfo?> FindAsync(string code, CancellationToken ct);
}
