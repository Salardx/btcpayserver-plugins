using System.Globalization;
using System.Text;
using System.Text.Json;
using NeoPaymentWatcher.Chains;

namespace NeoPaymentWatcher.Neo;

/// <summary>Neo N3 over JSON-RPC: <c>getblock</c> plus one <c>getapplicationlog</c> per transaction, read by <see cref="TransferParser"/>.</summary>
public sealed class NeoChainClient(ChainProfile chain, ILogger<NeoChainClient> logger) : IChainClient
{
    readonly JsonRpcClient rpc = new(chain.RpcUrls, logger);

    public ChainProfile Chain { get; } = chain;

    public async Task VerifyNetworkAsync(CancellationToken ct)
    {
        var magic = (await rpc.CallAsync("getversion", [], ct)).GetProperty("protocol").GetProperty("network").GetUInt32();
        if (magic != Chain.NetworkMagic)
            throw new InvalidOperationException($"The RPC node is on network {magic}, but Watcher:Network is '{Chain.Network}' ({Chain.NetworkMagic}).");
    }

    public async Task<uint> GetTipAsync(CancellationToken ct) =>
        (await rpc.CallAsync("getblockcount", [], ct)).GetUInt32() - 1;

    public async Task<ScannedBlock> ScanBlockAsync(uint index, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        var block = await rpc.CallAsync("getblock", [index, true], ct);
        var time = block.GetProperty("time").GetInt64();
        var transfers = new List<TokenTransfer>();
        foreach (var tx in block.GetProperty("tx").EnumerateArray())
        {
            var log = await rpc.CallAsync("getapplicationlog", [tx.GetProperty("hash").GetString()!], ct);
            transfers.AddRange(TransferParser.ParseIncoming(log, index, time, watchedKeys, tokenHashes));
        }
        return new ScannedBlock(index, time, transfers);
    }

    public async Task<TokenInfo> GetTokenInfoAsync(string tokenHash, CancellationToken ct)
    {
        var decimals = await InvokeReadOnlyAsync(tokenHash, "decimals", ct);
        var symbol = await InvokeReadOnlyAsync(tokenHash, "symbol", ct);
        return new TokenInfo(
            tokenHash,
            Encoding.UTF8.GetString(Convert.FromBase64String(symbol.GetProperty("value").GetString()!)),
            int.Parse(decimals.GetProperty("value").GetString()!, CultureInfo.InvariantCulture));
    }

    public bool TryCanonicalizeAddress(string? input, out string canonical, out string key)
    {
        canonical = input?.Trim() ?? "";
        var ok = NeoAddress.TryGetScriptHash(canonical, out var hash);
        key = ok ? Convert.ToHexStringLower(hash) : "";
        return ok;
    }

    async Task<JsonElement> InvokeReadOnlyAsync(string contractHash, string operation, CancellationToken ct)
    {
        var result = await rpc.CallAsync("invokefunction", [contractHash, operation, Array.Empty<object>()], ct);
        if (result.GetProperty("state").GetString() != "HALT")
            throw new InvalidOperationException($"{contractHash}.{operation}() faulted; is it a NEP-17 token?");
        return result.GetProperty("stack")[0];
    }
}
