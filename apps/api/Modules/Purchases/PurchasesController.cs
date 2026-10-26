using System.Text.Json;
using Carbon.Api.Data;
using Carbon.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Api.Modules.Purchases;

[ApiController]
[Route("api/purchases")]
public class PurchasesController(CarbonDbContext db, PurchaseService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the purchase. The tonne hold and the certificate follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<PurchaseDto>> Create(CreatePurchaseRequest request, CancellationToken ct)
    {
        var purchase = await service.CreateAsync(request, ct);
        return Accepted($"/api/purchases/{purchase.Id}", PurchaseDto.From(purchase));
    }

    [HttpGet]
    public async Task<IEnumerable<PurchaseDto>> List([FromQuery] string? batchCode, CancellationToken ct)
    {
        var query = db.Purchases.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(batchCode))
            query = query.Where(b => b.BatchCode == batchCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(PurchaseDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PurchaseDto>> Get(Guid id, CancellationToken ct)
    {
        var purchase = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return purchase is null ? NotFound() : PurchaseDto.From(purchase);
    }

    [HttpPost("{id:guid}/reversal")]
    public async Task<ActionResult<PurchaseDto>> Reverse(Guid id, CancellationToken ct) =>
        Accepted($"/api/purchases/{id}", PurchaseDto.From(await service.ReverseAsync(id, ct)));

    /// <summary>The Kafka event log for one purchase, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
