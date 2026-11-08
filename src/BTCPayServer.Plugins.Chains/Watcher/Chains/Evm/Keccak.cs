using System.Buffers.Binary;
using System.Numerics;

namespace NeoPaymentWatcher.Chains.Evm;

/// <summary>
/// Keccak-256 as Ethereum uses it: the original Keccak padding, not the NIST SHA-3 one, which is why
/// <c>SHA3_256</c> from the framework gives a different digest. Needed for EIP-55 address checksums;
/// fifty lines beat a dependency.
/// </summary>
public static class Keccak
{
    const int Rate = 136;   // (1600 - 2 * 256) bits, in bytes

    static readonly ulong[] RoundConstants =
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
        0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
        0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ];

    static readonly int[] Rotations = [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];
    static readonly int[] Lanes = [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

    public static byte[] Hash256(ReadOnlySpan<byte> data)
    {
        var state = new ulong[25];
        while (data.Length >= Rate)
        {
            Absorb(state, data[..Rate]);
            data = data[Rate..];
        }
        Span<byte> last = stackalloc byte[Rate];
        data.CopyTo(last);
        last[data.Length] = 0x01;
        last[Rate - 1] |= 0x80;
        Absorb(state, last);

        var digest = new byte[32];
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(i * 8), state[i]);
        return digest;
    }

    static void Absorb(ulong[] state, ReadOnlySpan<byte> block)
    {
        for (var i = 0; i < Rate / 8; i++)
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        Permute(state);
    }

    static void Permute(ulong[] a)
    {
        Span<ulong> c = stackalloc ulong[5];
        for (var round = 0; round < 24; round++)
        {
            for (var x = 0; x < 5; x++)
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (var x = 0; x < 5; x++)
            {
                var d = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5)
                    a[y + x] ^= d;
            }

            var carry = a[1];
            for (var i = 0; i < 24; i++)
            {
                var lane = Lanes[i];
                (carry, a[lane]) = (a[lane], BitOperations.RotateLeft(carry, Rotations[i]));
            }

            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++)
                    c[x] = a[y + x];
                for (var x = 0; x < 5; x++)
                    a[y + x] = c[x] ^ (~c[(x + 1) % 5] & c[(x + 2) % 5]);
            }

            a[0] ^= RoundConstants[round];
        }
    }
}
