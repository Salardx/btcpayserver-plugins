using System.Numerics;
using System.Text.Json;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Chains.Near;

/// <summary>A transfer into a watched account as a chunk lists it: a transaction (hash) or a receipt (id) that moves NEAR or a NEP-141 token.</summary>
public sealed record NearCandidate(string Id, bool IsTransaction, string From, string To, string Token, BigInteger Amount, string? Memo);

/// <summary>One step of an account's balance inside a block, from the <c>account_changes</c> view.</summary>
public sealed record NearBalanceStep(string CauseType, string? CauseHash, BigInteger Balance);

/// <summary>
/// Pure parsing of what a NEAR node returns. Chunks list the transactions of a block and the receipts that left the
/// previous block; a payment from a wallet is a <c>Transfer</c> action to the account, or an <c>ft_transfer</c>
/// call on the token contract naming the account, possibly wrapped in a <c>Delegate</c> action when a relayer
/// paid the gas (NEP-366 meta transactions).
/// </summary>
public static class NearTransfers
{
    const string Transfer = "Transfer";
    const string FunctionCall = "FunctionCall";
    const string Delegate = "Delegate";

    public static List<NearCandidate> FromChunk(JsonElement chunk, IReadOnlySet<string> watched, IReadOnlySet<string> tokens)
    {
        var candidates = new List<NearCandidate>();
        if (chunk.TryGetProperty("transactions", out var transactions) && transactions.ValueKind == JsonValueKind.Array)
        {
            foreach (var tx in transactions.EnumerateArray())
            {
                var hash = Text(tx, "hash");
                var signer = Text(tx, "signer_id");
                var receiver = Text(tx, "receiver_id");
                if (hash is null || signer is null || receiver is null || !tx.TryGetProperty("actions", out var actions))
                    continue;
                foreach (var action in actions.EnumerateArray())
                    Add(candidates, action, hash, true, signer, receiver, watched, tokens, allowDelegate: true);
            }
        }
        if (chunk.TryGetProperty("receipts", out var receipts) && receipts.ValueKind == JsonValueKind.Array)
        {
            foreach (var receipt in receipts.EnumerateArray())
            {
                var id = Text(receipt, "receipt_id");
                var predecessor = Text(receipt, "predecessor_id");
                var receiver = Text(receipt, "receiver_id");
                if (id is null || predecessor is null || receiver is null)
                    continue;
                if (!receipt.TryGetProperty("receipt", out var body) || !body.TryGetProperty("Action", out var actionReceipt)
                    || !actionReceipt.TryGetProperty("actions", out var actions))
                    continue;
                foreach (var action in actions.EnumerateArray())
                    Add(candidates, action, id, false, predecessor, receiver, watched, tokens, allowDelegate: false);
            }
        }
        return candidates;
    }

    static void Add(List<NearCandidate> candidates, JsonElement action, string id, bool isTransaction, string signer, string receiver,
        IReadOnlySet<string> watched, IReadOnlySet<string> tokens, bool allowDelegate)
    {
        if (action.ValueKind != JsonValueKind.Object)
            return;
        if (allowDelegate && action.TryGetProperty(Delegate, out var delegated) && delegated.TryGetProperty("delegate_action", out var inner))
        {
            // The relayer signed the transaction; the sender inside the delegate action is who pays.
            var sender = Text(inner, "sender_id");
            var target = Text(inner, "receiver_id");
            if (sender is null || target is null || !inner.TryGetProperty("actions", out var actions))
                return;
            foreach (var nested in actions.EnumerateArray())
                Add(candidates, nested, id, isTransaction, sender, target, watched, tokens, allowDelegate: false);
            return;
        }
        if (action.TryGetProperty(Transfer, out var transfer))
        {
            if (!tokens.Contains(TokenRegistry.Native) || !watched.Contains(receiver) || signer == receiver || signer == "system")
                return;
            var deposit = NearRpc.ParseAmount(Text(transfer, "deposit"));
            if (deposit.Sign > 0)
                candidates.Add(new NearCandidate(id, isTransaction, signer, receiver, TokenRegistry.Native, deposit, null));
            return;
        }
        if (action.TryGetProperty(FunctionCall, out var call) && tokens.Contains(receiver))
        {
            var method = Text(call, "method_name");
            if (method is not ("ft_transfer" or "ft_transfer_call"))
                return;
            if (!TryReadFungibleArguments(Text(call, "args"), out var to, out var amount, out var memo))
                return;
            if (!watched.Contains(to) || signer == to || amount.Sign <= 0)
                return;
            candidates.Add(new NearCandidate(id, isTransaction, signer, to, receiver, amount, memo));
        }
    }

    /// <summary>The <c>ft_transfer</c> arguments: base64 of JSON with <c>receiver_id</c>, <c>amount</c> and an optional <c>memo</c>.</summary>
    internal static bool TryReadFungibleArguments(string? argsBase64, out string receiver, out BigInteger amount, out string? memo)
    {
        receiver = "";
        amount = BigInteger.Zero;
        memo = null;
        if (string.IsNullOrEmpty(argsBase64))
            return false;
        try
        {
            using var args = JsonDocument.Parse(Convert.FromBase64String(argsBase64));
            var root = args.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var to = Text(root, "receiver_id");
            var value = Text(root, "amount");
            if (to is null || value is null || !value.All(char.IsAsciiDigit) || value.Length == 0)
                return false;
            receiver = to;
            amount = NearRpc.ParseAmount(value);
            memo = Text(root, "memo");
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The balance after each cause in the block, in order; the caller knows the balance before the block.</summary>
    public static List<NearBalanceStep> BalanceSteps(JsonElement changes, string accountId)
    {
        var steps = new List<NearBalanceStep>();
        if (!changes.TryGetProperty("changes", out var list) || list.ValueKind != JsonValueKind.Array)
            return steps;
        foreach (var change in list.EnumerateArray())
        {
            if (Text(change, "type") != "account_update" || !change.TryGetProperty("change", out var state) || Text(state, "account_id") != accountId)
                continue;
            var cause = change.GetProperty("cause");
            var type = Text(cause, "type") ?? "";
            var hash = type == "receipt_processing" ? Text(cause, "receipt_hash") : Text(cause, "tx_hash");
            steps.Add(new NearBalanceStep(type, hash, NearRpc.ParseAmount(Text(state, "amount"))));
        }
        return steps;
    }

    /// <summary>Who a receipt came from. Refunds come from "system" and are the account's own money coming back.</summary>
    public static string? Predecessor(JsonElement receipt) => Text(receipt, "predecessor_id");

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
