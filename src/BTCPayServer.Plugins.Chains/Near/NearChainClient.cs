using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using BTCPayServer.Plugins.Chains.Services;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Chains.Near;

/// <summary>
/// NEAR over JSON-RPC, watch-only. The chain is too fast to read block by block over a public node (a block every
/// 0.6 seconds, ten or more shards, each its own call), so the client works from balances instead: for every
/// watched account and asset it compares the balance at the two ends of the span the listener asks for, bisects
/// the span when they differ until each change sits in one block, and then reads that block's account changes
/// and chunks to name the transaction behind the payment. Whatever path the money took, a contract, a relayer
/// paying the gas, an intents settlement, the balance moved and the payment is recorded; the transaction id is
/// the extra when a wallet sent it directly.
/// </summary>
public sealed class NearChainClient : IChainClient, IRangeScanner, IDestinationChecker
{
    /// <summary>About twenty minutes of blocks. A longer outage is caught up span by span; a span costs a few calls per account whatever its length.</summary>
    internal const int RangeLimit = 2_000;
    /// <summary>How many consecutive missing heights the client walks back over; NEAR skips the odd height, never a stretch.</summary>
    const int SkippedHeightsTolerated = 25;
    const string ReceiptProcessing = "receipt_processing";
    const string System = "system";
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(6);
    static readonly TimeSpan ChunkPause = TimeSpan.FromMilliseconds(120);

    readonly NearRpc rpc;
    readonly ILogger logger;
    /// <summary>The balance at the end of the last span per asset and account: the start of the next span, without a call.</summary>
    readonly ConcurrentDictionary<(string Token, string Account), (uint Height, BigInteger Balance)> lastBalances = new();
    NearBlockHeader? lastFinal;

    public NearChainClient(ChainProfile chain, ILogger<NearChainClient> logger)
    {
        Chain = chain;
        this.logger = logger;
        rpc = new NearRpc(new JsonRpcClient(chain.RpcUrls, logger));
    }

    public ChainProfile Chain { get; }

    public int MaxBlocksPerRange => RangeLimit;

    public TimeSpan PollInterval => Poll;

    public async Task VerifyNetworkAsync(CancellationToken ct)
    {
        var chainId = (await rpc.StatusAsync(ct)).GetProperty("chain_id").GetString();
        if (!string.Equals(chainId, Chain.Network, StringComparison.Ordinal))
            throw new InvalidOperationException($"The RPC node is on NEAR {chainId}, but this server expects {Chain.Network}.");
    }

    /// <summary>The final block: NEAR's consensus marks a block final two or three blocks after it, and only those are read.</summary>
    public async Task<uint> GetTipAsync(CancellationToken ct)
    {
        var header = await rpc.FinalBlockAsync(ct);
        lastFinal = header;
        return header.Height;
    }

    public async Task<ScannedBlock> ScanBlockAsync(uint index, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        var range = await ScanRangeAsync(index, index, watchedKeys, tokenHashes, ct);
        return new ScannedBlock(index, range.ToTime, range.Transfers);
    }

    public async Task<ScannedRange> ScanRangeAsync(uint from, uint to, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);
        ArgumentOutOfRangeException.ThrowIfZero(from);
        var blocks = new Dictionary<uint, JsonElement>();
        var end = await EndOfRangeAsync(to, blocks, ct);
        var transfers = new List<TokenTransfer>();
        foreach (var account in watchedKeys)
        {
            foreach (var token in tokenHashes)
            {
                var balances = new Dictionary<uint, BigInteger>();
                Task<BigInteger> At(uint height) => BalanceAtAsync(token, account, height, balances, ct);

                BigInteger before;
                if (lastBalances.TryGetValue((token, account), out var last) && last.Height == from - 1)
                {
                    before = last.Balance;
                }
                else
                {
                    try
                    {
                        before = await At(from - 1);
                    }
                    catch (Exception ex) when (NearRpc.IsCause(ex, NearRpc.GarbageCollectedBlock))
                    {
                        // The node no longer has the state at the start of the span: the listener was away for more than
                        // a day. Any invoice from before that is long past its monitoring window, so start from here.
                        logger.LogWarning("NEAR: block {Block} is no longer on the node; {Account} is watched from block {End} on", from - 1, account, end.Height);
                        lastBalances[(token, account)] = (to, await At(end.Height));
                        continue;
                    }
                }
                var after = await At(end.Height);
                lastBalances[(token, account)] = (to, after);
                if (after == before)
                    continue;

                foreach (var (height, delta) in await BalanceBisection.FindChangesAsync(from, end.Height, before, after, At, ct))
                {
                    if (delta.Sign <= 0)
                        continue;
                    var block = await BlockAtOrBeforeAsync(height, blocks, ct);
                    transfers.AddRange(await AttributeAsync(token, account, block, delta, await At(height - 1), blocks, ct));
                }
            }
        }
        return new ScannedRange(to, end.TimeMs, transfers);
    }

    public async Task<TokenInfo> GetTokenInfoAsync(string tokenHash, CancellationToken ct)
    {
        if (tokenHash == TokenRegistry.Native)
            return new TokenInfo(TokenRegistry.Native, Chain.NativeSymbol, Chain.NativeDecimals);
        using var metadata = JsonDocument.Parse(await rpc.CallFunctionAsync(tokenHash, "ft_metadata", new { }, null, ct));
        var root = metadata.RootElement;
        return new TokenInfo(tokenHash, root.GetProperty("symbol").GetString() ?? "", root.GetProperty("decimals").GetInt32());
    }

    public bool TryCanonicalizeAddress(string? input, out string canonical, out string key)
    {
        var ok = NearAccountId.TryNormalize(input, out canonical);
        key = canonical;
        return ok;
    }

    /// <summary>
    /// A named account must exist before it can receive anything (a transfer to a missing one bounces); an implicit
    /// account is created by its first transfer. A token contract may want the account registered (NEP-145) before
    /// it holds a balance; that is a note, since the plugin's pay page registers it with the first payment.
    /// </summary>
    public async Task<DestinationCheck> CheckAsync(string address, PaymentMethodDefinition method, CancellationToken ct)
    {
        if (!NearAccountId.IsImplicit(address))
        {
            try
            {
                await rpc.ViewAccountAsync(address, null, ct);
            }
            catch (Exception ex) when (NearRpc.IsCause(ex, NearRpc.UnknownAccount))
            {
                return new DestinationCheck(false, $"{address} does not exist on NEAR {Chain.Network}. Create it in a wallet first, or use the implicit account of a key you control.");
            }
        }
        if (method.IsNative)
            return DestinationCheck.Fine;
        var storage = await rpc.CallFunctionAsync(method.TokenHash, "storage_balance_of", new { account_id = address }, null, ct);
        return storage.Trim() == "null"
            ? new DestinationCheck(true, $"{address} is not registered with the {method.Currency} contract yet. The pay page registers it with the first payment; a transfer from a wallet that does not register receivers fails until then. To be safe, register it once yourself (storage_deposit, about 0.00125 NEAR).")
            : DestinationCheck.Fine;
    }

    /// <summary>The newest block at or before the span's end; the final header from the last tip check when it is that block.</summary>
    async Task<NearBlockHeader> EndOfRangeAsync(uint to, Dictionary<uint, JsonElement> blocks, CancellationToken ct)
    {
        if (lastFinal is { } header && header.Height == to)
            return header;
        return NearRpc.Header(await BlockAtOrBeforeAsync(to, blocks, ct));
    }

    /// <summary>The block at this height, or the newest one before it when the height was skipped.</summary>
    async Task<JsonElement> BlockAtOrBeforeAsync(uint height, Dictionary<uint, JsonElement> cache, CancellationToken ct)
    {
        for (var h = height; ; h--)
        {
            if (cache.TryGetValue(h, out var cached))
            {
                cache[height] = cached;
                return cached;
            }
            try
            {
                var block = await rpc.BlockAsync(h, ct);
                cache[h] = block;
                cache[height] = block;
                return block;
            }
            catch (Exception ex) when (NearRpc.IsCause(ex, NearRpc.UnknownBlock) && h > 0 && height - h < SkippedHeightsTolerated)
            {
            }
        }
    }

    /// <summary>The balance as of a height: at that block, or at the newest block before it when the height was skipped. A missing account holds nothing.</summary>
    async Task<BigInteger> BalanceAtAsync(string token, string account, uint height, Dictionary<uint, BigInteger> memo, CancellationToken ct)
    {
        if (memo.TryGetValue(height, out var known))
            return known;
        for (var h = height; ; h--)
        {
            try
            {
                var balance = token == TokenRegistry.Native ? await NativeBalanceAsync(account, h, ct) : await TokenBalanceAsync(token, account, h, ct);
                memo[height] = balance;
                return balance;
            }
            catch (Exception ex) when (NearRpc.IsCause(ex, NearRpc.UnknownBlock) && h > 0 && height - h < SkippedHeightsTolerated)
            {
            }
        }
    }

    async Task<BigInteger> NativeBalanceAsync(string account, uint height, CancellationToken ct)
    {
        try
        {
            return NearRpc.ParseAmount((await rpc.ViewAccountAsync(account, height, ct)).GetProperty("amount").GetString());
        }
        catch (Exception ex) when (NearRpc.IsCause(ex, NearRpc.UnknownAccount))
        {
            return BigInteger.Zero;
        }
    }

    async Task<BigInteger> TokenBalanceAsync(string token, string account, uint height, CancellationToken ct) =>
        NearRpc.ParseQuotedAmount(await rpc.CallFunctionAsync(token, "ft_balance_of", new { account_id = account }, height, ct));

    /// <summary>
    /// Turns a balance rise in one block into transfers. For NEAR itself the account's change list says which receipts
    /// raised the balance and by how much, and the block's chunks supply the transaction behind each when a wallet
    /// sent it. For a token only the chunks speak: a transfer call naming the account for exactly the amount. When
    /// nothing in the chunks accounts for the rise, the payment is still recorded, under the block instead of a transaction.
    /// </summary>
    async Task<List<TokenTransfer>> AttributeAsync(string token, string account, JsonElement block, BigInteger delta, BigInteger before,
        Dictionary<uint, JsonElement> blocks, CancellationToken ct)
    {
        var header = NearRpc.Header(block);
        var candidates = await CandidatesAsync(block, account, token, blocks, ct);
        var transfers = new List<TokenTransfer>();

        if (token == TokenRegistry.Native)
        {
            var balance = before;
            var steps = NearTransfers.BalanceSteps(await rpc.AccountChangesAsync(account, header.Hash, ct), account);
            foreach (var step in steps)
            {
                var part = step.Balance - balance;
                balance = step.Balance;
                if (part.Sign <= 0 || step.CauseType != ReceiptProcessing || step.CauseHash is null)
                    continue;
                var from = NearTransfers.Predecessor(await rpc.ReceiptAsync(step.CauseHash, ct));
                if (from is null or System)
                    continue;   // a refund: the account's own money coming back
                var match = candidates.FirstOrDefault(c => c.IsTransaction && c.Amount == part && c.From == from);
                if (match is not null)
                    candidates.Remove(match);
                transfers.Add(new TokenTransfer(match?.Id ?? step.CauseHash, transfers.Count, header.Height, header.TimeMs, token, from, account, part));
            }
            if (steps.Count > 0)
                return transfers;   // empty when the rise was a refund: the account's own money, not a payment
        }

        var exact = candidates.Where(c => c.Amount == delta).OrderByDescending(c => c.IsTransaction).FirstOrDefault();
        if (exact is not null)
            return [Transfer(exact, header, 0)];
        if (candidates.Count > 1 && candidates.Aggregate(BigInteger.Zero, (sum, c) => sum + c.Amount) == delta)
            return candidates.Select((c, i) => Transfer(c, header, i)).ToList();

        logger.LogWarning("NEAR: {Account} received {Amount} units of {Token} in block {Block} by a path the chunks do not show; recorded under the block",
            account, delta, token, header.Height);
        return [new TokenTransfer($"{account}@{header.Hash}", -1, header.Height, header.TimeMs, token, "", account, delta)];
    }

    static TokenTransfer Transfer(NearCandidate c, NearBlockHeader header, int index) =>
        new(c.Id, index, header.Height, header.TimeMs, c.Token, c.From, c.To, c.Amount);

    /// <summary>
    /// Transfers to the account in this block's chunks and the previous block's: a payment lands in the block of
    /// its transaction when sender and receiver share a shard, and one block later, as a receipt, when they do not.
    /// </summary>
    async Task<List<NearCandidate>> CandidatesAsync(JsonElement block, string account, string token, Dictionary<uint, JsonElement> blocks, CancellationToken ct)
    {
        var watched = new HashSet<string>(StringComparer.Ordinal) { account };
        var tokens = new HashSet<string>(StringComparer.Ordinal) { token };
        var found = new List<NearCandidate>();
        var header = NearRpc.Header(block);
        await AddChunkCandidatesAsync(block, found, watched, tokens, ct);
        if (header.Height > 1)
        {
            var previous = await BlockAtOrBeforeAsync(header.Height - 1, blocks, ct);
            await AddChunkCandidatesAsync(previous, found, watched, tokens, ct);
        }
        // The same payment can show as a transaction in one block and as its receipt in the next; keep the transaction.
        return found
            .GroupBy(c => (c.From, c.To, c.Token, c.Amount, c.Memo))
            .Select(g => g.OrderByDescending(c => c.IsTransaction).First())
            .ToList();
    }

    async Task AddChunkCandidatesAsync(JsonElement block, List<NearCandidate> found, IReadOnlySet<string> watched, IReadOnlySet<string> tokens, CancellationToken ct)
    {
        if (!block.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array)
            return;
        foreach (var chunk in chunks.EnumerateArray())
        {
            if (chunk.GetProperty("tx_root").GetString() == NearRpc.EmptyRoot && chunk.GetProperty("outgoing_receipts_root").GetString() == NearRpc.EmptyRoot)
                continue;
            found.AddRange(NearTransfers.FromChunk(await rpc.ChunkAsync(chunk.GetProperty("chunk_hash").GetString()!, ct), watched, tokens));
            await Task.Delay(ChunkPause, ct);   // a payment is rare; a burst of chunk reads must not trip a public node's limit
        }
    }
}
