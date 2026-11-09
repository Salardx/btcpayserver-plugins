using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Chains.Near;

public sealed record NearBlockHeader(uint Height, string Hash, string PrevHash, long TimeMs);

/// <summary>The NEAR JSON-RPC calls the plugin makes, with named parameters, on top of the shared failover client.</summary>
public sealed class NearRpc(JsonRpcClient rpc)
{
    /// <summary>Base58 of 32 zero bytes: the root of an empty transaction or receipt list in a chunk header.</summary>
    public const string EmptyRoot = "11111111111111111111111111111111";

    public const string UnknownBlock = "UNKNOWN_BLOCK";
    public const string UnknownAccount = "UNKNOWN_ACCOUNT";
    public const string GarbageCollectedBlock = "GARBAGE_COLLECTED_BLOCK";

    public Task<JsonElement> StatusAsync(CancellationToken ct) => rpc.CallAsync("status", [], ct);

    public async Task<NearBlockHeader> FinalBlockAsync(CancellationToken ct) =>
        Header(await rpc.CallAsync("block", new { finality = "final" }, ct));

    /// <summary>The full block, header and chunk headers, by height or by hash.</summary>
    public Task<JsonElement> BlockAsync(object blockId, CancellationToken ct) => rpc.CallAsync("block", new { block_id = blockId }, ct);

    public Task<JsonElement> ChunkAsync(string chunkHash, CancellationToken ct) => rpc.CallAsync("chunk", new { chunk_id = chunkHash }, ct);

    /// <summary>The account's balance and state at a block, or at the final block when no block is given.</summary>
    public Task<JsonElement> ViewAccountAsync(string accountId, object? blockId, CancellationToken ct)
    {
        var request = new Dictionary<string, object?> { ["request_type"] = "view_account", ["account_id"] = accountId };
        if (blockId is null)
            request["finality"] = "final";
        else
            request["block_id"] = blockId;
        return rpc.CallAsync("query", request, ct);
    }

    /// <summary>A view call; the result is the bytes the contract returned, decoded as UTF-8 (JSON for NEP-141 methods).</summary>
    public async Task<string> CallFunctionAsync(string contractId, string method, object args, object? blockId, CancellationToken ct)
    {
        var request = new Dictionary<string, object?>
        {
            ["request_type"] = "call_function",
            ["account_id"] = contractId,
            ["method_name"] = method,
            ["args_base64"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(args)),
        };
        if (blockId is null)
            request["finality"] = "final";
        else
            request["block_id"] = blockId;
        var result = await rpc.CallAsync("query", request, ct);
        var bytes = result.GetProperty("result").EnumerateArray().Select(b => b.GetByte()).ToArray();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Every change of the account's balance in one block, in execution order, each with the transaction or receipt that caused it.</summary>
    public Task<JsonElement> AccountChangesAsync(string accountId, string blockHash, CancellationToken ct) =>
        rpc.CallAsync("EXPERIMENTAL_changes", new { block_id = blockHash, changes_type = "account_changes", account_ids = new[] { accountId } }, ct);

    public Task<JsonElement> ReceiptAsync(string receiptId, CancellationToken ct) =>
        rpc.CallAsync("EXPERIMENTAL_receipt", new { receipt_id = receiptId }, ct);

    public static NearBlockHeader Header(JsonElement block)
    {
        var header = block.GetProperty("header");
        return new NearBlockHeader(
            header.GetProperty("height").GetUInt32(),
            header.GetProperty("hash").GetString()!,
            header.GetProperty("prev_hash").GetString()!,
            header.GetProperty("timestamp").GetInt64() / 1_000_000);
    }

    /// <summary>Whether the failure was a NEAR handler error with this cause, wherever it sits in the chain of causes.</summary>
    public static bool IsCause(Exception exception, string cause) => JsonRpcClient.RpcError(exception)?.Cause == cause;

    public static BigInteger ParseAmount(string? text) =>
        BigInteger.Parse(text ?? "0", NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>A NEP-141 view method returns a JSON string holding the integer ("\"1500000\"").</summary>
    public static BigInteger ParseQuotedAmount(string json) => ParseAmount(JsonSerializer.Deserialize<string>(json));
}
