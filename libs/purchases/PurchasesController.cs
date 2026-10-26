using System.Text.Json;
using Carbon.Shared.Auth;
using Carbon.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Carbon.Purchases;

[ApiController]
[Route("api/purchases")]
[Authorize]
public class PurchasesController(PurchaseService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the purchase for the signed-in user. Batches and the certificate follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<PurchaseDto>> Create(CreatePurchaseRequest request, CancellationToken ct)
    {
        var purchase = await service.CreateAsync(request, ct);
        return Accepted($"/api/purchases/{purchase.Id}", PurchaseDto.From(purchase));
    }

    /// <summary>Your purchases. Staff see everyone's.</summary>
    [HttpGet]
    public async Task<IEnumerable<PurchaseDto>> List([FromQuery] string? batchCode, CancellationToken ct) =>
        (await service.ListAsync(batchCode, ct)).Select(PurchaseDto.From);

    [HttpGet("{id:guid}")]
    public async Task<PurchaseDto> Get(Guid id, CancellationToken ct) => PurchaseDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/reversal")]
    public async Task<ActionResult<PurchaseDto>> Reverse(Guid id, CancellationToken ct) =>
        Accepted($"/api/purchases/{id}", PurchaseDto.From(await service.ReverseAsync(id, ct)));

    /// <summary>The Kafka event log for one purchase, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
