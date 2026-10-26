using Carbon.Api.Data;
using Carbon.Api.Messaging;
using Carbon.Api.Modules.Purchases;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Api.Modules.Certificates;

public enum CertificateStatus { Issued, Rejected }

public class Certificate
{
    public Guid PurchaseId { get; set; }
    public string? Number { get; set; }
    public CertificateStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the certificate once the tonnes are held. A poison reference is committed as rejected.</summary>
public class CertificatesHandler(CarbonDbContext db) : IMessageHandler
{
    public const string Name = "certificates";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Certificates does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<TonnesHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, TonnesHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PurchaseRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Certificates.Add(new Certificate
            {
                PurchaseId = held.PurchaseId,
                Status = CertificateStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("certificates", Topology.ReleaseKey, held.PurchaseId, new ReleaseTonnes(held.PurchaseId, "poison"));
            db.Enqueue("certificates", Topology.RejectedKey, held.PurchaseId, new CertificateRejected(held.PurchaseId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"CC-{held.PurchaseId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Certificates.Add(new Certificate
            {
                PurchaseId = held.PurchaseId,
                Number = number,
                Status = CertificateStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("certificates", Topology.IssuedKey, held.PurchaseId, new CertificateIssued(held.PurchaseId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
