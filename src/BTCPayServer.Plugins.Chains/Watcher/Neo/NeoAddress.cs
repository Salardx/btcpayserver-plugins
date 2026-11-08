using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace NeoPaymentWatcher.Neo;

/// <summary>
/// Neo N3 address helpers. An address is Base58Check over version byte 0x35 followed by the
/// 20-byte script hash in the byte order used by notifications (UInt160 little-endian).
/// </summary>
public static class NeoAddress
{
    public const byte AddressVersion = 0x35;

    const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static bool TryGetScriptHash(string? address, out byte[] scriptHash)
    {
        scriptHash = [];
        if (!TryBase58Decode(address, out var data) || data.Length != 25 || data[0] != AddressVersion)
            return false;
        var checksum = SHA256.HashData(SHA256.HashData(data.AsSpan(0, 21)));
        if (!checksum.AsSpan(0, 4).SequenceEqual(data.AsSpan(21, 4)))
            return false;
        scriptHash = data[1..21];
        return true;
    }

    public static string FromScriptHash(ReadOnlySpan<byte> scriptHash) => Encode(AddressVersion, scriptHash);

    /// <summary>Contract hashes are shown big-endian: "0x" followed by the reversed bytes.</summary>
    public static string ToDisplayHash(ReadOnlySpan<byte> scriptHash)
    {
        var bytes = scriptHash.ToArray();
        Array.Reverse(bytes);
        return "0x" + Convert.ToHexStringLower(bytes);
    }

    /// <summary>Accepts "0x"-prefixed or bare 40-hex-digit contract hashes and returns the lowercase "0x" form.</summary>
    public static bool TryNormalizeContractHash(string? value, out string hash)
    {
        hash = "";
        if (value is null)
            return false;
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length != 40 || !hex.All(Uri.IsHexDigit))
            return false;
        hash = "0x" + hex.ToLowerInvariant();
        return true;
    }

    internal static string Encode(byte version, ReadOnlySpan<byte> scriptHash)
    {
        if (scriptHash.Length != 20)
            throw new ArgumentException("A script hash is 20 bytes.", nameof(scriptHash));
        Span<byte> data = stackalloc byte[25];
        data[0] = version;
        scriptHash.CopyTo(data[1..]);
        SHA256.HashData(SHA256.HashData(data[..21])).AsSpan(0, 4).CopyTo(data[21..]);
        return Base58Encode(data);
    }

    static string Base58Encode(ReadOnlySpan<byte> data)
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

    static bool TryBase58Decode(string? text, out byte[] data)
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
