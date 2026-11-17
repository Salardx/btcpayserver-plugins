using System.Buffers.Binary;
using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Chains.Solana;

/// <summary>
/// The transfer a Solana Pay wallet builds from a transfer request, built here instead, for a wallet in the browser
/// that cannot scan the checkout's code: it is handed the finished transaction and only signs and sends it. One
/// instruction, as the Solana Pay specification's reference implementation builds it: a System Program transfer for
/// SOL, or the token program's <c>TransferChecked</c> between the two wallets' token accounts, with the invoice's
/// reference appended to the instruction as a read-only account that signs nothing. The paying wallet is the only
/// signer and pays the fee. Nothing here holds a key: these are the unsigned bytes, with the signature left empty.
///
/// The legacy transaction format is used: every wallet signs it, and one instruction needs no lookup table.
/// </summary>
public static class SolanaPayTransaction
{
    const int KeyLength = 32;
    const int SignatureLength = 64;

    /// <summary>The System Program's transfer instruction, and the token programs' TransferChecked.</summary>
    const uint SystemTransfer = 2;
    const byte TokenTransferChecked = 12;

    /// <summary>
    /// A transfer of SOL from <paramref name="payer"/> to <paramref name="recipient"/>. Accounts in the order the
    /// format demands (who signs and may be written first, then who may be written, then the rest): the payer, the
    /// recipient, the reference, the System Program.
    /// </summary>
    public static byte[] Transfer(string payer, string recipient, ulong lamports, string reference, string recentBlockhash)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, SystemTransfer);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), lamports);
        return Serialize(
            accounts: [payer, recipient, reference, SolanaAddress.SystemProgram],
            readOnlyUnsigned: 2,
            recentBlockhash,
            program: 3,
            instructionAccounts: [0, 1, 2],
            data);
    }

    /// <summary>
    /// A transfer of a token from the payer's token account to the recipient's, checked against the token's mint and
    /// decimals. Accounts: the payer (who owns the source and signs), the source and the destination token accounts,
    /// the mint, the reference, the token program. The instruction names them as the program wants them: source,
    /// mint, destination, owner, and then the reference.
    /// </summary>
    public static byte[] TokenTransfer(string payer, string source, string mint, string destination, string tokenProgram,
        ulong amount, byte decimals, string reference, string recentBlockhash)
    {
        var data = new byte[10];
        data[0] = TokenTransferChecked;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1), amount);
        data[9] = decimals;
        return Serialize(
            accounts: [payer, source, destination, mint, reference, tokenProgram],
            readOnlyUnsigned: 3,
            recentBlockhash,
            program: 5,
            instructionAccounts: [1, 3, 2, 0, 4],
            data);
    }

    /// <summary>
    /// One signature slot, left empty, then the message: the header (one signer, who may be written; how many of the
    /// last accounts are read-only), the accounts, the recent blockhash, and the one instruction.
    /// </summary>
    static byte[] Serialize(string[] accounts, byte readOnlyUnsigned, string recentBlockhash, byte program, byte[] instructionAccounts, byte[] data)
    {
        if (accounts.Distinct(StringComparer.Ordinal).Count() != accounts.Length)
            throw new ArgumentException("The accounts of a transfer must all differ: the payer, the receiver and the reference cannot be the same account.", nameof(accounts));
        var bytes = new List<byte>(1 + SignatureLength + 3 + 1 + accounts.Length * KeyLength + KeyLength + 4 + instructionAccounts.Length + data.Length);
        AddLength(bytes, 1);
        bytes.AddRange(new byte[SignatureLength]);
        bytes.AddRange([1, 0, readOnlyUnsigned]);
        AddLength(bytes, accounts.Length);
        foreach (var account in accounts)
            bytes.AddRange(Key(account));
        bytes.AddRange(Key(recentBlockhash));
        AddLength(bytes, 1);
        bytes.Add(program);
        AddLength(bytes, instructionAccounts.Length);
        bytes.AddRange(instructionAccounts);
        AddLength(bytes, data.Length);
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    /// <summary>A length as the format writes it: seven bits a byte, lowest first, the top bit saying that more follow.</summary>
    static void AddLength(List<byte> bytes, int length)
    {
        var rest = (uint)length;
        do
        {
            var low = (byte)(rest & 0x7f);
            rest >>= 7;
            bytes.Add(rest == 0 ? low : (byte)(low | 0x80));
        } while (rest != 0);
    }

    static byte[] Key(string text) =>
        Base58.TryDecode(text, out var key) && key.Length == KeyLength ? key : throw new FormatException($"{text} is not 32 bytes of Base58.");
}

/// <summary>
/// What the pay page hands to a wallet in the browser: the unsigned transaction, or the reason there is none, in
/// words for the customer. <c>LastValidBlockHeight</c> is the block height after which the network no longer takes
/// the transaction, so the page can tell a payment that is on its way from one that will never arrive.
/// </summary>
public sealed record SolanaPreparedPayment(byte[]? Transaction, string? Refusal, ulong LastValidBlockHeight = 0)
{
    public static SolanaPreparedPayment Refused(string reason) => new(null, reason);
}

/// <summary>Where a transaction the pay page handed out stands, as the chain answers for its signature.</summary>
public enum SolanaSignatureState
{
    /// <summary>Not on the chain yet, and the block height it names has not passed: it may still arrive.</summary>
    OnItsWay,
    /// <summary>On the chain and successful; the listener records it when it is final.</summary>
    Paid,
    /// <summary>On the chain and failed: nothing moved.</summary>
    Failed,
    /// <summary>Not on the chain, and the network no longer takes it: it never will arrive.</summary>
    Gone,
}
