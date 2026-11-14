using System.Numerics;
using System.Text;

namespace NeoPaymentWatcher.Chains;

/// <summary>Base58 with Bitcoin's alphabet, as Neo N3 addresses and Solana keys are written.</summary>
public static class Base58
{
    const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var text = new StringBuilder();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            text.Insert(0, Alphabet[(int)remainder]);
        }
        for (var i = 0; i < data.Length && data[i] == 0; i++)
            text.Insert(0, '1');
        return text.ToString();
    }

    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(text))
            return false;
        var value = BigInteger.Zero;
        foreach (var c in text)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0)
                return false;
            value = value * 58 + digit;
        }
        byte[] bytes = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = text.TakeWhile(c => c == '1').Count();
        data = new byte[leadingZeros + bytes.Length];
        bytes.CopyTo(data, leadingZeros);
        return true;
    }
}
