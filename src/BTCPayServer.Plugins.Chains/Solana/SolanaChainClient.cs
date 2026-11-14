using System.Collections.Concurrent;
using System.Text.Json;
using BTCPayServer.Plugins.Chains.Services;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Chains.Solana;

/// <summary>
/// Solana over its public JSON-RPC. Solana makes about five blocks a second, so nothing is read block by block:
/// each turn asks for the transactions that named a receiving address since the last turn
/// (<c>getSignaturesForAddress</c>, for the wallet and for its token account per token) and reads each one's balance
/// changes. That costs a few calls per receiving wallet, however many invoices are open on it.
///
/// A payment is matched to its invoice by the Solana Pay reference the paying wallet put into the transaction, so
/// open invoices can share one address; a store that reserves an address per invoice instead is matched by the
/// address alone. Everything is read at the <c>finalized</c> commitment: what is recorded cannot be rolled back.
/// </summary>
public sealed class SolanaChainClient(ChainProfile chain, ILogger<SolanaChainClient> logger) : IChainClient, IRangeScanner, IDestinationChecker
{
    /// <summary>The first block of each cluster names it, as a chain id does on an EVM chain.</summary>
    internal const string MainnetGenesis = "5eykt4UsFv8P8NJdTREpY1vzqKqZKvdpKuc147dw2N9d";
    internal const string DevnetGenesis = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";

    internal const string Finalized = "finalized";
    internal const int NativeDecimals = 9;

    /// <summary>
    /// Each turn reads this many slots, about half a minute, of the previous turns again. A node may list a
    /// transaction under an address a moment after it reports the slot as finalized (providers that index addresses
    /// on the side do), and what one turn missed for that reason the next ones find. Recording a payment twice is
    /// impossible: its id is the same. A transaction that was read once is kept until its slot leaves this window,
    /// so reading again costs no further calls.
    /// </summary>
    internal const uint Overlap = 150;

    /// <summary>A turn usually finds nothing new or one transaction, so the first page is small; a busy history is paged from there.</summary>
    const int FirstPage = 25;

    /// <summary>The most signatures a node returns per call.</summary>
    const int PageSize = 1000;

    /// <summary>What a node answers when asked for a transaction of a newer version than the request names.</summary>
    const int UnsupportedTransactionVersion = -32015;

    readonly JsonRpcClient rpc = new(chain.RpcUrls, logger);
    readonly ConcurrentDictionary<string, string> tokenPrograms = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<(string Wallet, string Mint), string> tokenAccounts = new();
    readonly ConcurrentDictionary<string, (uint Slot, JsonElement? Transaction)> read = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> missing = new(StringComparer.Ordinal);

    public ChainProfile Chain { get; } = chain;

    /// <summary>
    /// How many turns a transaction that the node lists but does not return is asked for again before it is given
    /// up: at the listener's pace after an error, two minutes. Without a limit one such transaction would stop the
    /// chain for every store.
    /// </summary>
    internal int FetchAttempts { get; init; } = 24;

    /// <summary>The scan goes by address, not by block, so a long span costs no more than a short one: a listener that was down for a day catches up in one turn.</summary>
    public int MaxBlocksPerRange => 500_000;

    public TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(2);

    public async Task VerifyNetworkAsync(CancellationToken ct)
    {
        var expected = Chain.Network == "mainnet" ? MainnetGenesis : DevnetGenesis;
        var genesis = (await rpc.CallAsync("getGenesisHash", [], ct)).GetString();
        if (genesis != expected)
            throw new InvalidOperationException($"The RPC node is on another Solana cluster (genesis {genesis}) than the configured one, {Chain.Network} (genesis {expected}).");
    }

    /// <summary>The newest finalized slot.</summary>
    public async Task<uint> GetTipAsync(CancellationToken ct) =>
        (uint)(await rpc.CallAsync("getSlot", [new { commitment = Finalized }], ct)).GetUInt64();

    public async Task<ScannedBlock> ScanBlockAsync(uint index, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        var range = await ScanAsync(index, index, watchedKeys, tokenHashes, ct);
        return new ScannedBlock(index, range.ToTime, range.Transfers);
    }

    public Task<ScannedRange> ScanRangeAsync(uint from, uint to, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct) =>
        ScanAsync(from > Overlap ? from - Overlap : 0, to, watchedKeys, tokenHashes, ct);

    async Task<ScannedRange> ScanAsync(uint from, uint to, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct)
    {
        foreach (var old in read.Where(r => r.Value.Slot < from).Select(r => r.Key).ToArray())
            read.TryRemove(old, out _);

        var transfers = new List<TokenTransfer>();
        foreach (var (wallet, watch) in Watches(watchedKeys))
        {
            // The wallet's own history shows SOL arriving; a token arrives at the wallet's token account, which has a history of its own.
            var histories = new List<string>();
            if (tokenHashes.Contains(TokenRegistry.Native))
                histories.Add(wallet);
            foreach (var mint in tokenHashes.Where(t => t != TokenRegistry.Native))
                histories.Add(await TokenAccountAsync(wallet, mint, ct));

            var signatures = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var address in histories)
                foreach (var (signature, slot) in await SignaturesAsync(address, from, to, ct))
                    signatures[signature] = slot;

            foreach (var (signature, slot) in signatures.OrderBy(s => s.Value))
            {
                var first = !read.TryGetValue(signature, out var known);
                var transaction = first ? await TransactionAsync(signature, slot, ct) : known.Transaction;
                if (transaction is not { } found)
                    continue;
                var payment = SolanaTransfers.Parse(found, wallet, tokenHashes);
                if (payment.Incoming.Count == 0)
                    continue;
                var named = payment.Accounts.Where(watch.References.Contains).Distinct(StringComparer.Ordinal).ToList();
                var receiver = named.Count > 0 ? SolanaAddress.SharedKey(wallet, named[0]) : watch.Reserved ? wallet : null;
                if (first && named.Count > 1)
                    logger.LogWarning("{Chain}: transaction {Tx} names {Count} open invoices at {Address}; all of it is recorded on the first",
                        Chain.DisplayName, signature, named.Count, wallet);
                if (receiver is null)
                {
                    if (first)
                        logger.LogInformation("{Chain}: transaction {Tx} paid {Address} without the reference of an open invoice; it is not matched to one",
                            Chain.DisplayName, signature, wallet);
                    continue;
                }
                var time = (payment.BlockTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) * 1000;
                foreach (var incoming in payment.Incoming)
                    transfers.Add(new TokenTransfer(signature, incoming.AccountIndex, slot, time, incoming.TokenHash, incoming.From, receiver, incoming.Amount));
            }
        }
        return new ScannedRange(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), transfers);
    }

    public async Task<TokenInfo> GetTokenInfoAsync(string tokenHash, CancellationToken ct)
    {
        if (tokenHash == TokenRegistry.Native)
            return new TokenInfo(TokenRegistry.Native, Chain.NativeSymbol, NativeDecimals);
        var mint = await AccountAsync(tokenHash, ct);
        if (mint is not { } account || !account.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("parsed", out var parsed) || !parsed.TryGetProperty("info", out var info)
            || !info.TryGetProperty("decimals", out var decimals))
            throw new InvalidOperationException($"{tokenHash} is not a token on Solana {Chain.Network}.");
        // A mint carries no symbol; the payment method names the currency.
        return new TokenInfo(tokenHash, "", decimals.GetInt32());
    }

    public bool TryCanonicalizeAddress(string? input, out string canonical, out string key)
    {
        var ok = SolanaAddress.TryNormalize(input, out canonical);
        key = canonical;
        return ok;
    }

    /// <summary>
    /// A receiving address must be a wallet, not one of its token accounts, and for a token its token account must
    /// exist already: a wallet that pays a Solana Pay request does not create it.
    /// </summary>
    public async Task<DestinationCheck> CheckAsync(string address, PaymentMethodDefinition method, CancellationToken ct)
    {
        var account = await AccountAsync(address, ct);
        if (account is { } existing)
        {
            var owner = Text(existing, "owner");
            if (owner is SolanaAddress.TokenProgram or SolanaAddress.Token2022Program)
                return new DestinationCheck(false, $"{address} is a token account, not a wallet. Enter the address of the wallet that owns it; its token accounts are found from there.");
            if (owner != SolanaAddress.SystemProgram || (existing.TryGetProperty("executable", out var executable) && executable.ValueKind == JsonValueKind.True))
                return new DestinationCheck(false, $"{address} belongs to a program; it is not a wallet that can be paid.");
        }
        if (method.IsNative)
        {
            if (account is not null)
                return DestinationCheck.Fine;
            var minimum = (await rpc.CallAsync("getMinimumBalanceForRentExemption", [0], ct)).GetUInt64();
            return new DestinationCheck(true, $"This address holds no SOL yet. Its first payment must be at least {TokenAmount.Format(minimum, NativeDecimals)} SOL, the least an account may hold, or the transfer fails.");
        }

        var tokenAccount = await AccountAsync(await TokenAccountAsync(address, method.TokenHash, ct), ct);
        if (tokenAccount is not { } holding)
            return new DestinationCheck(false, $"This wallet has no {method.Currency} account yet, and a wallet that pays an invoice does not create one. Receive any amount of {method.Currency} at this address once, then add it here.");
        var frozen = holding.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                     && data.TryGetProperty("parsed", out var parsed) && parsed.TryGetProperty("info", out var info)
                     && Text(info, "state") == "frozen";
        return frozen
            ? new DestinationCheck(false, $"The {method.Currency} account of this wallet is frozen by the token's issuer; it cannot receive.")
            : DestinationCheck.Fine;
    }

    /// <summary>What to look for at one wallet: whether an invoice reserved it, and the references of the invoices that share it.</summary>
    sealed record Watch(bool Reserved, HashSet<string> References);

    static Dictionary<string, Watch> Watches(IReadOnlySet<string> watchedKeys)
    {
        var watches = new Dictionary<string, Watch>(StringComparer.Ordinal);
        foreach (var key in watchedKeys)
        {
            var shared = SolanaAddress.TrySplitSharedKey(key, out var wallet, out var reference);
            var address = shared ? wallet! : key;
            var watch = watches.GetValueOrDefault(address) ?? new Watch(false, new HashSet<string>(StringComparer.Ordinal));
            if (shared)
                watch.References.Add(reference!);
            else
                watch = watch with { Reserved = true };
            watches[address] = watch;
        }
        return watches;
    }

    /// <summary>
    /// Signatures of successful transactions that named the address in slots <paramref name="from"/> to
    /// <paramref name="to"/>, newest first as the node lists them. The node must have finalized slot
    /// <paramref name="to"/> to answer: one that is behind refuses, and the span is asked again.
    /// </summary>
    async Task<List<(string Signature, uint Slot)>> SignaturesAsync(string address, uint from, uint to, CancellationToken ct)
    {
        var found = new List<(string, uint)>();
        string? before = null;
        while (true)
        {
            var limit = before is null ? FirstPage : PageSize;
            var options = new Dictionary<string, object> { ["limit"] = limit, ["commitment"] = Finalized, ["minContextSlot"] = to };
            if (before is not null)
                options["before"] = before;
            var page = await rpc.CallAsync("getSignaturesForAddress", [address, options], ct);
            if (page.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"The node did not list the transactions of {address}.");
            var listed = 0;
            var older = false;
            foreach (var entry in page.EnumerateArray())
            {
                listed++;
                before = Text(entry, "signature");
                var slot = entry.GetProperty("slot").GetUInt64();
                if (slot < from)
                {
                    older = true;
                    break;
                }
                if (slot > to || before is null)
                    continue;
                if (entry.TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null)
                    continue;
                found.Add((before, (uint)slot));
            }
            if (older || listed < limit)
                return found;
        }
    }

    /// <summary>
    /// The transaction behind a listed signature, or null when it is left out. A node that has not caught up with
    /// the one that listed the signature answers null: the span is then asked again, <see cref="FetchAttempts"/>
    /// times, after which the transaction is given up with an error in the log, because the listener must go on
    /// for the other stores. One the plugin cannot read at all (a version newer than it knows) is left out at once.
    /// </summary>
    async Task<JsonElement?> TransactionAsync(string signature, uint slot, CancellationToken ct)
    {
        JsonElement transaction;
        try
        {
            transaction = await rpc.CallAsync("getTransaction",
                [signature, new { commitment = Finalized, maxSupportedTransactionVersion = 0, encoding = "json" }], ct);
        }
        catch (Exception ex) when (JsonRpcClient.RpcError(ex) is { Code: UnsupportedTransactionVersion })
        {
            logger.LogError("{Chain}: transaction {Tx} has a version this plugin does not read; it is left out. If it paid an invoice, settle that invoice by hand.",
                Chain.DisplayName, signature);
            read[signature] = (slot, null);
            return null;
        }
        if (transaction.ValueKind == JsonValueKind.Object)
        {
            missing.TryRemove(signature, out _);
            read[signature] = (slot, transaction);
            return transaction;
        }
        if (missing.AddOrUpdate(signature, 1, (_, attempts) => attempts + 1) < FetchAttempts)
            throw new InvalidOperationException($"Transaction {signature} is not available from the node yet.");
        missing.TryRemove(signature, out _);
        logger.LogError("{Chain}: the node lists transaction {Tx} but did not return it in {Attempts} attempts; it is left out. If it paid an invoice, settle that invoice by hand.",
            Chain.DisplayName, signature, FetchAttempts);
        read[signature] = (slot, null);
        return null;
    }

    /// <summary>The wallet's token account for a token; which of the two token programs issued the token decides the address.</summary>
    async Task<string> TokenAccountAsync(string wallet, string mint, CancellationToken ct)
    {
        if (tokenAccounts.TryGetValue((wallet, mint), out var known))
            return known;
        if (!tokenPrograms.TryGetValue(mint, out var program))
        {
            program = await AccountAsync(mint, ct) is { } account ? Text(account, "owner") : null;
            if (program is not (SolanaAddress.TokenProgram or SolanaAddress.Token2022Program))
                throw new InvalidOperationException($"{mint} is not a token on Solana {Chain.Network}.");
            tokenPrograms[mint] = program;
        }
        return tokenAccounts[(wallet, mint)] = SolanaAddress.AssociatedTokenAddress(wallet, mint, program);
    }

    /// <summary>The account at an address as the node describes it, or null when nothing exists there.</summary>
    async Task<JsonElement?> AccountAsync(string address, CancellationToken ct)
    {
        var answer = await rpc.CallAsync("getAccountInfo", [address, new { commitment = Finalized, encoding = "jsonParsed" }], ct);
        return answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
