using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.Chains.Near;

/// <summary>
/// NEAR account ids: 2 to 64 characters of lowercase letters, digits, <c>-</c> and <c>_</c>, in parts separated by
/// dots ("shop.near", "pay.shop.near"). An implicit account is the 64 hex digits of its public key, and an
/// Ethereum-style one is <c>0x</c> followed by 40 hex digits; both fit the same rule.
/// </summary>
public static partial class NearAccountId
{
    public const int MinLength = 2;
    public const int MaxLength = 64;

    [GeneratedRegex(@"^(([a-z\d]+[\-_])*[a-z\d]+\.)*([a-z\d]+[\-_])*[a-z\d]+$")]
    private static partial Regex Pattern();

    /// <summary>Trims and lowercases; refuses anything that is not an account id.</summary>
    public static bool TryNormalize(string? input, out string accountId)
    {
        accountId = "";
        var text = input?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Length is < MinLength or > MaxLength || !Pattern().IsMatch(text))
            return false;
        accountId = text;
        return true;
    }

    /// <summary>An implicit account exists as soon as it receives NEAR; a named account must be created first.</summary>
    public static bool IsImplicit(string accountId) => accountId.Length == 64 && accountId.All(Uri.IsHexDigit);
}
