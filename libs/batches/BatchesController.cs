using Carbon.Shared;
using Carbon.Shared.Auth;
using Carbon.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Carbon.Batches;

public record CreateBatchRequest(string? Code, string? Name, string? Standard, string? Country, DateTimeOffset? IssuedOn, long TonnesIssued, long PriceCents);

public record BatchDto(Guid Id, string Code, string Name, string Standard, string Country, DateTimeOffset IssuedOn, long PriceCents, int TonnesIssued, int TonnesLeft)
{
    public static BatchDto From(Batch s) => new(s.Id, s.Code, s.Name, s.Standard, s.Country, s.IssuedOn, s.PriceCents, s.TonnesIssued, s.TonnesLeft);
}

/// <summary>What other modules may ask Batches, over Batches's own tables.</summary>
public sealed class BatchCatalog(BatchesDbContext db) : IBatchCatalog
{
    public async Task<BatchInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Batches.AsNoTracking().Where(s => s.Code == code).Select(s => new BatchInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/batches")]
[Authorize]
public class BatchesController(BatchesDbContext db) : ControllerBase
{
    private const int MaxTonnesIssued = 1_000_000;
    private const long MaxPriceCents = 100_000;

    [HttpGet]
    public async Task<IEnumerable<BatchDto>> List(CancellationToken ct) =>
        (await db.Batches.AsNoTracking().OrderBy(s => s.IssuedOn).ThenBy(s => s.Code).ToListAsync(ct)).Select(BatchDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<BatchDto>> Get(string code, CancellationToken ct)
    {
        var batch = await db.Batches.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return batch is null ? NotFound() : BatchDto.From(batch);
    }

    /// <summary>Opens a batch. Staff only. This is the only place TonnesLeft is initialised; after that only the Batches handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<BatchDto>> Create(CreateBatchRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Standard) || string.IsNullOrWhiteSpace(request.Country))
            throw new DomainException(400, "Name, standard and country are required.");
        if (request.TonnesIssued < 1 || request.TonnesIssued > MaxTonnesIssued)
            throw new DomainException(400, $"Tonnes issued must be between 1 and {MaxTonnesIssued} tonnes.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per tonne.");
        if (await db.Batches.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Batch '{code}' already exists.");

        var batch = new Batch
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Standard = request.Standard.Trim(),
            Country = request.Country.Trim(),
            IssuedOn = request.IssuedOn ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            TonnesIssued = (int)request.TonnesIssued,
            TonnesLeft = (int)request.TonnesIssued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync(ct);
        return Created($"/api/batches/{batch.Code}", BatchDto.From(batch));
    }
}
