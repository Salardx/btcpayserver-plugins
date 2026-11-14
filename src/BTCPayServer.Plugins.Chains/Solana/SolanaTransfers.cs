using System.Globalization;
using System.Numerics;
using System.Text.Json;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Chains.Solana;

/// <summary>
/// What one transaction added to a wallet in one currency, and who it came from. <c>AccountIndex</c> is the position
/// of the receiving account (the wallet, or its token account) among the transaction's accounts: it tells apart two
/// payments that one transaction makes to two wallets.
/// </summary>
public sealed record SolanaIncoming(string TokenHash, BigInteger Amount, string From, int AccountIndex);

/// <summary>A transaction as the listener needs it: every account it names, and what it paid into the wallet.</summary>
public sealed record SolanaPayment(IReadOnlyList<string> Accounts, IReadOnlyList<SolanaIncoming> Incoming, long? BlockTime);

/// <summary>
/// Pure reading of a <c>getTransaction</c> answer. A payment is measured the way the Solana Pay reference
/// implementation validates one: by the balances before and after, which the node reports for every account of the
/// transaction. That counts what actually arrived, whatever instructions moved it: a plain transfer, a transfer made
/// inside another program, or a token with a transfer fee.
/// </summary>
public static class SolanaTransfers
{
    static readonly SolanaPayment Nothing = new([], [], null);

    public static SolanaPayment Parse(JsonElement transaction, string wallet, IReadOnlySet<string> tokenHashes)
    {
        if (transaction.ValueKind != JsonValueKind.Object
            || !transaction.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !transaction.TryGetProperty("transaction", out var body) || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return Nothing;
        // A failed transaction costs its fee and moves nothing else.
        if (meta.TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null)
            return Nothing;

        // Balances are listed in this order: the message's own accounts, then the ones loaded from lookup tables.
        var accounts = new List<string>();
        AddStrings(accounts, message, "accountKeys");
        if (meta.TryGetProperty("loadedAddresses", out var loaded) && loaded.ValueKind == JsonValueKind.Object)
        {
            AddStrings(accounts, loaded, "writable");
            AddStrings(accounts, loaded, "readonly");
        }
        long? blockTime = transaction.TryGetProperty("blockTime", out var time) && time.ValueKind == JsonValueKind.Number ? time.GetInt64() : null;
        if (accounts.Count == 0)
            return Nothing;

        // A transaction the wallet signed is its owner's own doing (a swap, a closed account's refund), not a customer's payment.
        var signers = message.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object
                      && header.TryGetProperty("numRequiredSignatures", out var required) && required.ValueKind == JsonValueKind.Number
            ? required.GetInt32()
            : 1;
        var index = accounts.IndexOf(wallet);
        if (index >= 0 && index < signers)
            return new SolanaPayment(accounts, [], blockTime);

        var payer = accounts[0];
        var incoming = new List<SolanaIncoming>();
        if (index >= 0 && tokenHashes.Contains(TokenRegistry.Native))
        {
            var gained = Number(meta, "postBalances", index) - Number(meta, "preBalances", index);
            if (gained.Sign > 0)
                incoming.Add(new SolanaIncoming(TokenRegistry.Native, gained, payer, index));
        }

        var before = TokenBalances(meta, "preTokenBalances");
        var after = TokenBalances(meta, "postTokenBalances");
        foreach (var mint in after.Where(b => b.Owner == wallet && tokenHashes.Contains(b.Mint)).Select(b => b.Mint).Distinct(StringComparer.Ordinal))
        {
            var held = after.Where(b => b.Owner == wallet && b.Mint == mint).ToList();
            var gained = BigInteger.Zero;
            foreach (var balance in held)
                gained += balance.Amount - before.Where(b => b.AccountIndex == balance.AccountIndex && b.Mint == mint).Select(b => b.Amount).FirstOrDefault();
            if (gained.Sign <= 0)
                continue;
            // The sender is the owner of a token account that holds less of the token than before.
            var from = after.Where(a => a.Mint == mint && a.Owner != wallet
                                        && before.Any(b => b.AccountIndex == a.AccountIndex && b.Amount > a.Amount))
                .Select(a => a.Owner)
                .FirstOrDefault() ?? payer;
            incoming.Add(new SolanaIncoming(mint, gained, from, held.Min(b => b.AccountIndex)));
        }
        return new SolanaPayment(accounts, incoming, blockTime);
    }

    sealed record TokenBalance(int AccountIndex, string Mint, string Owner, BigInteger Amount);

    static List<TokenBalance> TokenBalances(JsonElement meta, string name)
    {
        var balances = new List<TokenBalance>();
        if (!meta.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return balances;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("accountIndex", out var index) || index.ValueKind != JsonValueKind.Number
                || Text(entry, "mint") is not { } mint || Text(entry, "owner") is not { } owner
                || !entry.TryGetProperty("uiTokenAmount", out var amount) || amount.ValueKind != JsonValueKind.Object
                || Text(amount, "amount") is not { } units
                || !BigInteger.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                continue;
            balances.Add(new TokenBalance(index.GetInt32(), mint, owner, value));
        }
        return balances;
    }

    static void AddStrings(List<string> target, JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                target.Add(item.GetString()!);
    }

    /// <summary>Lamport balances are whole numbers up to 2^64, read from the number's own digits.</summary>
    static BigInteger Number(JsonElement meta, string name, int index) =>
        meta.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && index < list.GetArrayLength()
        && list[index].ValueKind == JsonValueKind.Number
        && BigInteger.TryParse(list[index].GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : BigInteger.Zero;

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
