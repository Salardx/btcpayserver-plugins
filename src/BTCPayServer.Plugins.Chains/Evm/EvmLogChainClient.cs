using System.Collections.Concurrent;
using System.Text.Json;
using BTCPayServer.Plugins.Chains.Services;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Chains.Evm;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Chains.Evm;

/// <summary>
/// An EVM chain on which every payment method shows up as a Transfer log, the chain's own coin included: Arc, where
/// USDC is the coin and each movement of it, a plain transfer, a call of its ERC-20 interface or a transfer made by
/// a contract, is announced by one log from a fixed address. Nothing has to be read from the blocks themselves, so
/// the listener asks for the logs of a whole span since its last turn: two calls per turn instead of two per block.
/// </summary>
public sealed class EvmLogChainClient(ChainProfile chain, ILogger<EvmLogChainClient> logger) : EvmChainClient(chain, logger), IRangeScanner
{
    /// <summary>
    /// Each turn reads this many blocks of the previous turns again. A public endpoint is served by several nodes,
    /// and one that is a block behind can answer a span with the newest block's logs missing and no error; what one
    /// turn missed that way the next ones find. Recording a payment twice is impossible: its id is the same.
    /// </summary>
    internal const uint Overlap = 16;

    readonly ConcurrentDictionary<uint, long> blockTimes = new();

    /// <summary>Public nodes cap the span of one <c>eth_getLogs</c>; 500 blocks and the overlap are inside every cap seen so far.</summary>
    public int MaxBlocksPerRange => 500;

    public TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1.5);

    /// <summary>One block, read the same way as a span.</summary>
    public override async Task<ScannedBlock> ScanBlockAsync(uint index, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        var range = await ScanAsync(index, index, watchedKeys, tokenHashes, ct);
        return new ScannedBlock(index, range.ToTime, range.Transfers);
    }

    public Task<ScannedRange> ScanRangeAsync(uint from, uint to, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct) =>
        ScanAsync(from > Overlap ? from - Overlap : 0, to, watchedKeys, tokenHashes, ct);

    async Task<ScannedRange> ScanAsync(uint from, uint to, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        // The contracts whose Transfer logs are read: the tokens, and the coin's own log source.
        var sources = tokenHashes.Where(t => t != TokenRegistry.Native).ToList();
        if (Chain.NativeTransferLog is { } coin && tokenHashes.Contains(TokenRegistry.Native))
            sources.Add(coin);
        var last = EvmHex.Quantity(to);
        if (sources.Count == 0 || watchedKeys.Count == 0)
            return new ScannedRange(to, Time(await Rpc.CallAsync("eth_getBlockByNumber", [last, false], ct), to), []);

        // Both calls travel in one request, so that the node that returns the logs is, where the endpoint keeps a
        // request together, the node that has block `to`: one that is behind answers "no such block" and the span
        // is asked again.
        var filter = new { fromBlock = EvmHex.Quantity(from), toBlock = last, address = sources, topics = TransferTopics(watchedKeys) };
        var answers = await Rpc.CallBatchAsync([("eth_getBlockByNumber", [last, false]), ("eth_getLogs", [filter])], ct);
        var lastTime = Time(answers[0], to);
        Remember(to, lastTime);

        var transfers = new List<TokenTransfer>();
        foreach (var log in EvmTransfers.ParseTransferLogs(answers[1], watchedKeys, tokenHashes, Chain.NativeTransferLog))
        {
            if (log.BlockIndex < from || log.BlockIndex > to)
                continue;
            if (!blockTimes.TryGetValue(log.BlockIndex, out var time))
            {
                time = Time(await Rpc.CallAsync("eth_getBlockByNumber", [EvmHex.Quantity(log.BlockIndex), false], ct), log.BlockIndex);
                Remember(log.BlockIndex, time);
            }
            transfers.Add(new TokenTransfer(log.TxId, log.LogIndex, log.BlockIndex, time, log.TokenHash, log.From, log.To, log.Amount));
        }
        return new ScannedRange(to, lastTime, transfers);
    }

    /// <summary>A block's time does not change once the block is final, so a payment that is read again costs no second call.</summary>
    void Remember(uint index, long time)
    {
        if (blockTimes.Count > 1024)
            blockTimes.Clear();
        blockTimes[index] = time;
    }

    static long Time(JsonElement block, uint index)
    {
        if (block.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Block {index} is not available from the node yet.");
        return EvmHex.ToInt64(block.GetProperty("timestamp").GetString()) * 1000;
    }
}
