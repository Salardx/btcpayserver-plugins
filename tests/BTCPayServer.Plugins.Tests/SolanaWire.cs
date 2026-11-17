using System.Buffers.Binary;
using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Tests;

/// <summary>One instruction of a decoded transaction: the program and the accounts by their addresses, in the order the instruction names them.</summary>
internal sealed record SolanaWireInstruction(string Program, string[] Accounts, byte[] Data);

/// <summary>
/// A serialized legacy Solana transaction, read back for the tests: the signature slots, the header, the accounts, the
/// recent blockhash and the instructions. Written apart from the code that builds one, from the format's description:
/// lengths are "compact-u16" (seven bits a byte, lowest first), keys and hashes are 32 raw bytes, a signature is 64.
/// </summary>
internal sealed record SolanaWire(byte[][] Signatures, byte Signers, byte ReadOnlySigned, byte ReadOnlyUnsigned, string[] Accounts, string Blockhash, SolanaWireInstruction[] Instructions)
{
    /// <summary>Whether the message lets a transaction write to the account at this position.</summary>
    public bool IsWritable(int index) =>
        index < Signers ? index < Signers - ReadOnlySigned : index < Accounts.Length - ReadOnlyUnsigned;

    public bool IsSigner(int index) => index < Signers;

    public int IndexOf(string account) => Array.IndexOf(Accounts, account);

    public static SolanaWire Parse(byte[] transaction)
    {
        var at = 0;
        var signatures = new byte[Length(transaction, ref at)][];
        for (var i = 0; i < signatures.Length; i++)
            signatures[i] = Take(transaction, ref at, 64);
        // A versioned message starts with a byte whose top bit is set; a legacy one with the number of signers.
        Assert.True(transaction[at] < 0x80, "the message is not in the legacy format");
        var header = Take(transaction, ref at, 3);
        var accounts = new string[Length(transaction, ref at)];
        for (var i = 0; i < accounts.Length; i++)
            accounts[i] = Base58.Encode(Take(transaction, ref at, 32));
        var blockhash = Base58.Encode(Take(transaction, ref at, 32));
        var instructions = new SolanaWireInstruction[Length(transaction, ref at)];
        for (var i = 0; i < instructions.Length; i++)
        {
            var program = accounts[transaction[at++]];
            var named = Take(transaction, ref at, Length(transaction, ref at)).Select(index => accounts[index]).ToArray();
            var data = Take(transaction, ref at, Length(transaction, ref at));
            instructions[i] = new SolanaWireInstruction(program, named, data);
        }
        Assert.Equal(transaction.Length, at);
        return new SolanaWire(signatures, header[0], header[1], header[2], accounts, blockhash, instructions);
    }

    /// <summary>The amount a System Program transfer carries: instruction 2, then the lamports.</summary>
    public static ulong SystemTransferLamports(byte[] data)
    {
        Assert.Equal(12, data.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4));
    }

    /// <summary>What a token program's TransferChecked carries: instruction 12, the amount, the token's decimals.</summary>
    public static (ulong Amount, byte Decimals) TransferChecked(byte[] data)
    {
        Assert.Equal(10, data.Length);
        Assert.Equal(12, data[0]);
        return (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1)), data[9]);
    }

    static int Length(byte[] bytes, ref int at)
    {
        var value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var next = bytes[at++];
            value |= (next & 0x7f) << shift;
            if ((next & 0x80) == 0)
                return value;
        }
    }

    static byte[] Take(byte[] bytes, ref int at, int count)
    {
        var slice = bytes.AsSpan(at, count).ToArray();
        at += count;
        return slice;
    }
}
