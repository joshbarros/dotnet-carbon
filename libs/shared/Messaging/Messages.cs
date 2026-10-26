using System.Text.Json;

namespace Carbon.Shared.Messaging;

public record ReserveTonnes(Guid PurchaseId, Guid OwnerId, Guid BatchId, int Tonnes, string Reference);
public record TonnesHeld(Guid PurchaseId, Guid OwnerId, Guid BatchId, int Tonnes, string Reference);
public record TonnesShort(Guid PurchaseId, string Reference, int RequestedTonnes, int TonnesLeft);
public record ReleaseTonnes(Guid PurchaseId, string Reason);
public record TonnesReleased(Guid PurchaseId, int Tonnes, string Reason);
public record CertificateIssued(Guid PurchaseId, string Number);
public record CertificateRejected(Guid PurchaseId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
