using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Chains.Solana;

/// <summary>
/// Solana addresses and the one derivation the plugin needs. An address is 32 bytes in Base58. A wallet's tokens
/// do not sit at the wallet's address but in a token account per token, whose address is derived from the wallet,
/// the token program and the token ("associated token account"); a payment in USDC shows up there, so that is the
/// address whose history is read. A derived address is a hash that must not be a point of the Ed25519 curve, hence
/// the curve test.
/// </summary>
public static class SolanaAddress
{
    public const string SystemProgram = "11111111111111111111111111111111";
    public const string TokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    public const string Token2022Program = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
    public const string AssociatedTokenProgram = "ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL";

    /// <summary>Between the address and the reference in the key of an invoice that shares its address. Not a Base58 character.</summary>
    public const char ReferenceSeparator = '#';

    /// <summary>The field of Curve25519, and the constant d of the Edwards curve: -121665/121666.</summary>
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger D = BigInteger.Parse("37095705934669439343138083508754565189542113879843219016388785533085940283555", System.Globalization.CultureInfo.InvariantCulture);

    static readonly byte[] DerivationMarker = Encoding.ASCII.GetBytes("ProgramDerivedAddress");

    /// <summary>32 bytes of Base58 in their one canonical spelling.</summary>
    public static bool TryNormalize(string? input, out string address)
    {
        address = "";
        var text = input?.Trim();
        if (!Base58.TryDecode(text, out var key) || key.Length != 32 || Base58.Encode(key) != text)
            return false;
        address = text;
        return true;
    }

    /// <summary>
    /// Whether 32 bytes are a point of the Ed25519 curve: the bytes are a y coordinate (the top bit is the sign of x),
    /// and a point exists when (y^2 - 1) / (d y^2 + 1) is a square in the field.
    /// </summary>
    public static bool IsOnCurve(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            return false;
        Span<byte> bytes = stackalloc byte[32];
        key.CopyTo(bytes);
        bytes[31] &= 0x7f;
        var y = new BigInteger(bytes, isUnsigned: true, isBigEndian: false) % P;
        var yy = y * y % P;
        var u = (yy - 1 + P) % P;
        var v = (D * yy + 1) % P;
        if (v.IsZero)
            return false;
        var xx = u * BigInteger.ModPow(v, P - 2, P) % P;
        return xx.IsZero || BigInteger.ModPow(xx, (P - 1) / 2, P).IsOne;
    }

    /// <summary>
    /// The address a program derives from seeds: SHA-256 over the seeds, a bump byte, the program and a marker, with
    /// the bump counted down from 255 until the hash is not a curve point (so no private key can exist for it).
    /// </summary>
    public static string FindProgramAddress(IReadOnlyList<byte[]> seeds, byte[] program, out byte bump)
    {
        for (var candidate = 255; candidate >= 0; candidate--)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var seed in seeds)
                hash.AppendData(seed);
            hash.AppendData([(byte)candidate]);
            hash.AppendData(program);
            hash.AppendData(DerivationMarker);
            var digest = hash.GetHashAndReset();
            if (IsOnCurve(digest))
                continue;
            bump = (byte)candidate;
            return Base58.Encode(digest);
        }
        throw new InvalidOperationException("No program address exists for these seeds.");
    }

    /// <summary>The token account a wallet holds a token in by convention, and the only one a Solana Pay transfer pays to.</summary>
    public static string AssociatedTokenAddress(string owner, string mint, string tokenProgram) =>
        FindProgramAddress([Decode(owner), Decode(tokenProgram), Decode(mint)], Decode(AssociatedTokenProgram), out _);

    /// <summary>
    /// A fresh Solana Pay reference: 32 random bytes that a paying wallet adds to its transfer as an extra account.
    /// Nodes index transactions by every account they name, so the payment of one invoice is found by this value
    /// while many invoices share the address the money goes to.
    /// </summary>
    public static string NewReference() => Base58.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The key of an invoice that shares its address: the address and the invoice's reference.</summary>
    public static string SharedKey(string address, string reference) => $"{address}{ReferenceSeparator}{reference}";

    public static bool TrySplitSharedKey(string? key, [NotNullWhen(true)] out string? address, [NotNullWhen(true)] out string? reference)
    {
        address = null;
        reference = null;
        var cut = key?.IndexOf(ReferenceSeparator) ?? -1;
        if (cut <= 0 || !TryNormalize(key![..cut], out var wallet) || !TryNormalize(key[(cut + 1)..], out var tag))
            return false;
        address = wallet;
        reference = tag;
        return true;
    }

    static byte[] Decode(string address) =>
        Base58.TryDecode(address, out var key) && key.Length == 32 ? key : throw new FormatException($"{address} is not a Solana address.");
}
