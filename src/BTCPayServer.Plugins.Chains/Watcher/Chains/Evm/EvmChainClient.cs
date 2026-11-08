using System.Text.Json;
using NeoPaymentWatcher.Services;

namespace NeoPaymentWatcher.Chains.Evm;

/// <summary>
/// Neo X, X Layer, Base or any EVM chain over standard JSON-RPC: one <c>eth_getBlockByNumber</c> per
/// block for native-coin payments (plus a receipt per candidate), and one <c>eth_getLogs</c> per block
/// for the whitelisted ERC-20 tokens.
/// </summary>
public sealed class EvmChainClient(ChainProfile chain, ILogger<EvmChainClient> logger) : IChainClient
{
    const string SymbolSelector = "0x95d89b41";
    const string DecimalsSelector = "0x313ce567";

    readonly JsonRpcClient rpc = new(chain.RpcUrls, logger);

    public ChainProfile Chain { get; } = chain;

    public async Task VerifyNetworkAsync(CancellationToken ct)
    {
        var chainId = EvmHex.ToInt64((await rpc.CallAsync("eth_chainId", [], ct)).GetString());
        if (chainId != Chain.ChainId)
            throw new InvalidOperationException($"The RPC node is on chain {chainId}, but Watcher:Network is '{Chain.Network}' (chain {Chain.ChainId}).");
    }

    public async Task<uint> GetTipAsync(CancellationToken ct) =>
        (uint)EvmHex.ToInt64((await rpc.CallAsync("eth_blockNumber", [], ct)).GetString());

    public async Task<ScannedBlock> ScanBlockAsync(uint index, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        var number = EvmHex.Quantity(index);
        var block = await rpc.CallAsync("eth_getBlockByNumber", [number, true], ct);
        if (block.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Block {index} is not available from the node yet.");
        var hash = block.GetProperty("hash").GetString()!;
        var time = EvmHex.ToInt64(block.GetProperty("timestamp").GetString()) * 1000;

        var transfers = new List<TokenTransfer>();
        if (tokenHashes.Contains(TokenRegistry.Native) && block.TryGetProperty("transactions", out var transactions))
        {
            foreach (var candidate in EvmTransfers.NativeCandidates(transactions, watchedKeys))
            {
                var receipt = await rpc.CallAsync("eth_getTransactionReceipt", [candidate.TxId], ct);
                if (EvmTransfers.Succeeded(receipt, candidate.TxId, hash))
                    transfers.Add(EvmTransfers.Native(candidate, index, time));
            }
        }

        var tokens = tokenHashes.Where(t => t != TokenRegistry.Native).ToArray();
        if (tokens.Length > 0 && watchedKeys.Count > 0)
        {
            // By hash, not by number: a node that does not have the block yet then answers with an error,
            // and the block is scanned again, instead of the empty list that would hide a payment.
            var filter = new
            {
                blockHash = hash,
                address = tokens,
                topics = new object?[] { EvmTransfers.TransferTopic, null, watchedKeys.Select(EvmHex.AddressTopic).ToArray() },
            };
            var logs = await rpc.CallAsync("eth_getLogs", [filter], ct);
            transfers.AddRange(EvmTransfers.ParseLogs(logs, index, time, watchedKeys, tokenHashes));
        }
        return new ScannedBlock(index, time, transfers);
    }

    public async Task<TokenInfo> GetTokenInfoAsync(string tokenHash, CancellationToken ct)
    {
        if (tokenHash == TokenRegistry.Native)
            return new TokenInfo(TokenRegistry.Native, Chain.NativeSymbol, Chain.NativeDecimals);
        var symbol = EvmHex.DecodeString(await CallContractAsync(tokenHash, SymbolSelector, ct));
        var decimals = EvmHex.DecodeUInt8(await CallContractAsync(tokenHash, DecimalsSelector, ct));
        if (symbol.Length == 0)
            throw new InvalidOperationException($"{tokenHash} returned no symbol; is it an ERC-20 token?");
        return new TokenInfo(tokenHash, symbol, decimals);
    }

    public bool TryCanonicalizeAddress(string? input, out string canonical, out string key)
    {
        var ok = EvmHex.TryNormalizeAddress(input, out canonical);
        key = canonical;
        return ok;
    }

    async Task<string?> CallContractAsync(string contract, string selector, CancellationToken ct) =>
        (await rpc.CallAsync("eth_call", [new { to = contract, data = selector }, "latest"], ct)).GetString();
}
