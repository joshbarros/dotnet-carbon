using Carbon.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Certificates;

public record CertificateDto(Guid PurchaseId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/certificates")]
[Authorize]
public class CertificatesController(CertificatesDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{purchaseId:guid}")]
    public async Task<ActionResult<CertificateDto>> Get(Guid purchaseId, CancellationToken ct)
    {
        var doc = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(d => d.PurchaseId == purchaseId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new CertificateDto(doc.PurchaseId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
