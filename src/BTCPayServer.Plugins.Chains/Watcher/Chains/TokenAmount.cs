using System.Globalization;
using System.Numerics;

namespace NeoPaymentWatcher.Chains;

/// <summary>Converts between decimal token amounts ("1.5") and integer base units without floating point.</summary>
public static class TokenAmount
{
    public static bool TryParse(string? text, int decimals, out BigInteger units)
    {
        units = BigInteger.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split('.');
        if (parts.Length > 2)
            return false;
        var whole = parts[0];
        var fraction = parts.Length == 2 ? parts[1] : "";
        if (whole.Length == 0 && fraction.Length == 0)
            return false;
        if (!whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit) || fraction.Length > decimals)
            return false;
        var digits = (whole.Length == 0 ? "0" : whole) + fraction.PadRight(decimals, '0');
        units = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    public static string Format(BigInteger units, int decimals)
    {
        var digits = BigInteger.Abs(units).ToString(CultureInfo.InvariantCulture);
        if (decimals > 0)
        {
            digits = digits.PadLeft(decimals + 1, '0');
            var fraction = digits[^decimals..].TrimEnd('0');
            digits = digits[..^decimals] + (fraction.Length > 0 ? "." + fraction : "");
        }
        return units.Sign < 0 ? "-" + digits : digits;
    }
}
