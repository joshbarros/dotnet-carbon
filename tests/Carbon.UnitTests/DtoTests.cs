using Carbon.Purchases;
using Carbon.Batches;
using Xunit;

namespace Carbon.UnitTests;

public class DtoTests
{
    [Fact]
    public void PurchaseDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            BatchCode = "S-1",
            Reference = "ORG-1",
            Tonnes = 3,
            TotalCents = 277_500,
            Status = PurchaseStatus.Allocated,
            CertificateNumber = "CC-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = PurchaseDto.From(purchase);

        Assert.Equal(purchase.Id, dto.Id);
        Assert.Equal(purchase.OwnerId, dto.OwnerId);
        Assert.Equal("Allocated", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("CC-1", dto.CertificateNumber);
    }

    [Fact]
    public void BatchDto_MapsTheScarceNumber()
    {
        var batch = new Batch { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Standard = "o", Country = "d", TonnesIssued = 10, TonnesLeft = 4 };
        var dto = BatchDto.From(batch);
        Assert.Equal(10, dto.TonnesIssued);
        Assert.Equal(4, dto.TonnesLeft);
    }
}
