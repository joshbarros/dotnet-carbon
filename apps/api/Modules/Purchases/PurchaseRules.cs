namespace Carbon.Api.Modules.Purchases;

public static class PurchaseRules
{
    public const int MaxTonnesPerPurchase = 500;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";
    public const long LevyCentsPerTonne = 20;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long tonnes)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Beneficiary reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Beneficiary reference must be at most {MaxReferenceLength} characters.";
        if (tonnes < 1)
            return "A purchase needs at least 1 tonne.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (tonnes > MaxTonnesPerPurchase)
            return $"A purchase takes at most {MaxTonnesPerPurchase} tonnes.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Tonnes times (batch price plus the registry levy per tonne), in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long priceCents, int tonnes) => checked(tonnes * (priceCents + LevyCentsPerTonne));
}
