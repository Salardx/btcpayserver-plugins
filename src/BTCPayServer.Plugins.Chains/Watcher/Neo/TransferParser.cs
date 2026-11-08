using System.Globalization;
using System.Numerics;
using System.Text.Json;
using NeoPaymentWatcher.Chains;

namespace NeoPaymentWatcher.Neo;

public static class TransferParser
{
    /// <summary>
    /// Extracts incoming NEP-17 transfers from a transaction's application log. Only successful (HALT)
    /// Application executions count. Mints and burns, self-transfers, zero amounts, NEP-11 transfers and
    /// tokens outside the whitelist are skipped: anyone can deploy a contract that emits "Transfer".
    /// </summary>
    public static List<TokenTransfer> ParseIncoming(
        JsonElement applicationLog,
        uint blockIndex,
        long blockTime,
        IReadOnlySet<string> watchedScriptHashes,
        IReadOnlySet<string> tokenHashes)
    {
        var transfers = new List<TokenTransfer>();
        var txId = applicationLog.GetProperty("txid").GetString()!;
        var notifyIndex = -1;
        foreach (var execution in applicationLog.GetProperty("executions").EnumerateArray())
        {
            var succeeded = execution.GetProperty("trigger").GetString() == "Application"
                && execution.GetProperty("vmstate").GetString() == "HALT";
            if (!execution.TryGetProperty("notifications", out var notifications) || notifications.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var notification in notifications.EnumerateArray())
            {
                notifyIndex++;
                if (!succeeded || notification.GetProperty("eventname").GetString() != "Transfer")
                    continue;
                var contract = notification.GetProperty("contract").GetString()!.ToLowerInvariant();
                if (!tokenHashes.Contains(contract))
                    continue;

                // NEP-17 Transfer is (from, to, amount); NEP-11 adds a fourth tokenId argument.
                var args = notification.GetProperty("state").GetProperty("value");
                if (args.GetArrayLength() != 3)
                    continue;
                if (!TryReadScriptHash(args[0], out var from) || !TryReadScriptHash(args[1], out var to))
                    continue;
                if (from == to || !watchedScriptHashes.Contains(to))
                    continue;
                if (args[2].GetProperty("type").GetString() != "Integer")
                    continue;
                var amount = BigInteger.Parse(args[2].GetProperty("value").GetString()!, CultureInfo.InvariantCulture);
                if (amount.Sign <= 0)
                    continue;

                transfers.Add(new TokenTransfer(txId, notifyIndex, blockIndex, blockTime, contract,
                    NeoAddress.FromScriptHash(Convert.FromHexString(from)),
                    NeoAddress.FromScriptHash(Convert.FromHexString(to)),
                    amount));
            }
        }
        return transfers;
    }

    /// <summary>Reads a 20-byte account as lowercase hex. A null account (type Any) means a mint or burn.</summary>
    static bool TryReadScriptHash(JsonElement item, out string scriptHash)
    {
        scriptHash = "";
        var type = item.GetProperty("type").GetString();
        if (type is not ("ByteString" or "Buffer"))
            return false;
        var bytes = Convert.FromBase64String(item.GetProperty("value").GetString()!);
        if (bytes.Length != 20)
            return false;
        scriptHash = Convert.ToHexStringLower(bytes);
        return true;
    }
}
