using Carbon.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Api.Modules.Certificates;

public record CertificateDto(Guid PurchaseId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/certificates")]
public class CertificatesController(CarbonDbContext db) : ControllerBase
{
    [HttpGet("{purchaseId:guid}")]
    public async Task<ActionResult<CertificateDto>> Get(Guid purchaseId, CancellationToken ct)
    {
        var doc = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(d => d.PurchaseId == purchaseId, ct);
        return doc is null
            ? NotFound()
            : new CertificateDto(doc.PurchaseId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
