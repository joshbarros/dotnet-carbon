using Carbon.Api.Modules.Purchases;
using Xunit;

namespace Carbon.Api.Tests;

public class PurchaseRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("ORG-1", 0)]
    [InlineData("ORG-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long tonnes) =>
        Assert.NotNull(PurchaseRules.Validate(reference, tonnes));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(PurchaseRules.Validate("ORG-1", PurchaseRules.MaxTonnesPerPurchase));
        Assert.NotNull(PurchaseRules.Validate("ORG-1", PurchaseRules.MaxTonnesPerPurchase + 1));
        Assert.NotNull(PurchaseRules.Validate(new string('x', PurchaseRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one tonne
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long tonnes)
    {
        Assert.NotNull(PurchaseRules.Validate("ORG-1", tonnes));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyTotal_IsTonnesTimesPricePlusTheRegistryLevy()
    {
        Assert.Null(PurchaseRules.Validate("ORG-1", 10));
        Assert.Equal(10 * (1_500L + 20L), PurchaseRules.TotalCents(1_500, 10)); // 10 t at $15.00 + $0.20 levy = $152.00
        Assert.Equal(21L, PurchaseRules.TotalCents(1, 1));
        Assert.Equal(500 * (1_500L + 20L), PurchaseRules.TotalCents(1_500, PurchaseRules.MaxTonnesPerPurchase));
    }

    [Fact]
    public void TotalArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => PurchaseRules.TotalCents(long.MaxValue, 1)); // price + levy overflows
        Assert.Throws<OverflowException>(() => PurchaseRules.TotalCents(long.MaxValue / 2, 3)); // the multiplication overflows
    }

    [Theory]
    [InlineData("POISON")]
    [InlineData("ORG-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void PoisonSubstring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(PurchaseRules.IsPoison(reference));

    [Theory]
    [InlineData("ORG-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(PurchaseRules.IsPoison(reference));
}
