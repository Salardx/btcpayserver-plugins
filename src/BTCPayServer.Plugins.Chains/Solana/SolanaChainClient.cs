using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
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
    /// What a wallet holds right now is asked at the newest commitment a node vouches for; payments are still recorded
    /// from finalized data only.
    /// </summary>
    internal const string Confirmed = "confirmed";

    /// <summary>The network's fee for a transaction with one signature, in lamports. A wallet may add a priority fee of its own.</summary>
    internal const ulong SignatureFee = 5000;

    /// <summary>
    /// Below this much left over, the least an account may hold is asked for: a transaction that leaves its payer
    /// less than that, but not nothing, fails, whether the lamports went to a receiver or to the fee.
    /// </summary>
    const ulong SmallRemainder = 10_000_000;

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

    /// <summary>
    /// How many of a reference's newest transactions the pay page looks through, and how many of those it reads in
    /// full. Anyone who has seen a checkout knows its reference and can name it in a transaction of their own, so
    /// the list is bounded and only the newest few are read.
    /// </summary>
    const int PendingPage = 20;
    const int PendingReads = 5;

    /// <summary>
    /// How long a blockhash is shared between the payments the pay page prepares. A blockhash is good for about
    /// half a minute, so a couple of seconds of sharing costs a customer nothing and saves the node a call per click.
    /// </summary>
    static readonly TimeSpan BlockhashLifetime = TimeSpan.FromSeconds(2);

    /// <summary>The least an account may hold is a network parameter; it is asked for again now and then, not per payment.</summary>
    static readonly TimeSpan MinimumBalanceLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most signatures a node returns per call.</summary>
    const int PageSize = 1000;

    /// <summary>
    /// The newest transaction format this plugin reads. A node returns a transaction only to a client that names its
    /// version or a newer one. Since September 2026 wallets send version 1 (SIMD-0385) next to version 0 and the
    /// legacy format, and a node shows all three alike: the accounts, and every account's balance before and after.
    /// </summary>
    const int MaxTransactionVersion = 1;

    /// <summary>What a node answers when asked for a transaction of a newer version than the request names.</summary>
    const int UnsupportedTransactionVersion = -32015;

    readonly JsonRpcClient rpc = new(chain.RpcUrls, logger);
    readonly ConcurrentDictionary<string, string> tokenPrograms = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<(string Wallet, string Mint), string> tokenAccounts = new();
    readonly ConcurrentDictionary<string, (uint Slot, JsonElement? Transaction)> read = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> missing = new(StringComparer.Ordinal);
    Cached<RecentBlockhash>? blockhash;
    Cached<ulong>? minimumBalance;

    /// <summary>A value worth keeping for a moment, and when it was read.</summary>
    sealed record Cached<T>(T Value, DateTimeOffset At)
    {
        public bool IsFresh(TimeSpan lifetime) => DateTimeOffset.UtcNow - At < lifetime;
    }

    /// <summary>A blockhash and the block height after which the network no longer takes a transaction naming it.</summary>
    internal sealed record RecentBlockhash(string Blockhash, ulong LastValidBlockHeight);

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

    /// <summary>
    /// The invoice's transfer as an unsigned transaction, for a wallet in the browser to sign and send: what a wallet
    /// that scans the checkout's Solana Pay code would build by itself, with the reference in it. The paying wallet is
    /// looked at first, so that a customer reads why a payment cannot work instead of meeting a wallet's failed
    /// simulation: no SOL for the fee, too little of the currency, no token account, a remainder below the least an
    /// account may hold. Nothing is signed or sent here, and no key is involved.
    /// </summary>
    public async Task<SolanaPreparedPayment> PreparePaymentAsync(string? payer, string recipient, PaymentMethodDefinition method, BigInteger units, string reference, CancellationToken ct)
    {
        if (!SolanaAddress.TryNormalize(payer, out var from))
            return SolanaPreparedPayment.Refused("The wallet did not name a Solana account.");
        if (from == recipient)
            return SolanaPreparedPayment.Refused("This is the store's own wallet. Pay from another one.");
        if (units.Sign <= 0 || units > ulong.MaxValue)
            return SolanaPreparedPayment.Refused("Nothing is left to pay on this invoice.");
        var amount = (ulong)units;
        return method.IsNative
            ? await PrepareNativeAsync(from, recipient, amount, reference, ct)
            : await PrepareTokenAsync(from, recipient, method, amount, reference, ct);
    }

    async Task<SolanaPreparedPayment> PrepareNativeAsync(string from, string recipient, ulong amount, string reference, CancellationToken ct)
    {
        var accounts = await AccountsAsync([from, recipient], ct);
        if (accounts[0] is not { } wallet)
            return SolanaPreparedPayment.Refused($"This wallet holds no {Chain.NativeSymbol} on Solana {Chain.Network}.");
        if (Text(wallet, "owner") != SolanaAddress.SystemProgram)
            return SolanaPreparedPayment.Refused("This account belongs to a program; only a plain wallet can pay from here.");
        var held = Lamports(wallet);
        var needed = (BigInteger)amount + SignatureFee;
        if (held < needed)
            return SolanaPreparedPayment.Refused($"This wallet holds {Sol(held)} {Chain.NativeSymbol}. The invoice is for {Sol(amount)} {Chain.NativeSymbol}, plus a small network fee.");
        var left = held - needed;
        var firstPayment = accounts[1] is null;
        if (firstPayment || IsSmallRemainder(left))
        {
            var minimum = await MinimumBalanceAsync(ct);
            if (firstPayment && amount < minimum)
                return SolanaPreparedPayment.Refused($"The store's wallet has never held {Chain.NativeSymbol}, and the network takes no first payment below {Sol(minimum)} {Chain.NativeSymbol}. Tell the store.");
            if (left.Sign > 0 && left < minimum)
                return SolanaPreparedPayment.Refused(TooLittleLeft(left, minimum));
        }
        var recent = await RecentBlockhashAsync(ct);
        return new SolanaPreparedPayment(
            SolanaPayTransaction.Transfer(from, recipient, amount, reference, recent.Blockhash), null, recent.LastValidBlockHeight);
    }

    async Task<SolanaPreparedPayment> PrepareTokenAsync(string from, string recipient, PaymentMethodDefinition method, ulong amount, string reference, CancellationToken ct)
    {
        var program = await TokenProgramAsync(method.TokenHash, ct);
        var source = SolanaAddress.AssociatedTokenAddress(from, method.TokenHash, program);
        var destination = await TokenAccountAsync(recipient, method.TokenHash, ct);
        var accounts = await AccountsAsync([from, source, destination], ct);
        if (accounts[0] is not { } wallet || Lamports(wallet) < SignatureFee)
            return SolanaPreparedPayment.Refused($"This wallet has no {Chain.NativeSymbol} for the network fee, about {Sol(SignatureFee)} {Chain.NativeSymbol}.");
        if (Text(wallet, "owner") != SolanaAddress.SystemProgram)
            return SolanaPreparedPayment.Refused("This account belongs to a program; only a plain wallet can pay from here.");
        // The fee alone must not leave the wallet with less than an account may hold either.
        var left = Lamports(wallet) - SignatureFee;
        if (IsSmallRemainder(left) && await MinimumBalanceAsync(ct) is var minimum && left < minimum)
            return SolanaPreparedPayment.Refused(TooLittleLeft(left, minimum));
        if (TokenHolding.Read(accounts[1]) is not { } paying || paying.Owner != from || paying.Mint != method.TokenHash)
            return SolanaPreparedPayment.Refused($"This wallet holds no {method.Currency} on Solana {Chain.Network}.");
        if (paying.Frozen)
            return SolanaPreparedPayment.Refused($"The {method.Currency} of this wallet is frozen by the token's issuer.");
        if (paying.Decimals != method.Decimals)
            return SolanaPreparedPayment.Refused($"The token is set up with {method.Decimals} decimals but has {paying.Decimals} on Solana {Chain.Network}. Tell the store.");
        if (paying.Amount < amount)
            return SolanaPreparedPayment.Refused($"This wallet holds {TokenAmount.Format(paying.Amount, method.Decimals)} {method.Currency}. The invoice is for {TokenAmount.Format(amount, method.Decimals)} {method.Currency}.");
        if (TokenHolding.Read(accounts[2]) is not { Frozen: false } receiving || receiving.Owner != recipient || receiving.Mint != method.TokenHash)
            return SolanaPreparedPayment.Refused($"The store's wallet cannot receive {method.Currency} right now. Tell the store.");
        var recent = await RecentBlockhashAsync(ct);
        return new SolanaPreparedPayment(
            SolanaPayTransaction.TokenTransfer(from, source, method.TokenHash, destination, program, amount, (byte)method.Decimals, reference, recent.Blockhash),
            null, recent.LastValidBlockHeight);
    }

    /// <summary>
    /// The payment of this invoice that is on its way: a transaction that named the invoice's reference, really paid
    /// the store this currency, and sits in a slot the listener has not finished with. Null when there is none.
    ///
    /// Both halves matter. Anyone who has seen the checkout knows the reference and can name it in a transaction of
    /// their own, so what the transaction did is read, not just that it exists. And "not recorded yet" is measured
    /// against the listener's own cursor, not a clock: what it has already scanned past it will never record, so a
    /// customer is not left looking at a payment that is never going to arrive. A payment the listener has already
    /// recorded is not one on its way: a partly paid invoice still takes the rest.
    /// </summary>
    public async Task<string?> PendingPaymentAsync(string wallet, string reference, string tokenHash, uint scannedThrough, IReadOnlySet<string> recorded, CancellationToken ct)
    {
        var floor = scannedThrough > Overlap ? scannedThrough - Overlap : 0;
        var page = await rpc.CallAsync("getSignaturesForAddress", [reference, new { limit = PendingPage, commitment = Confirmed }], ct);
        if (page.ValueKind != JsonValueKind.Array)
            return null;
        var tokens = new HashSet<string>(StringComparer.Ordinal) { tokenHash };
        var read = 0;
        foreach (var entry in page.EnumerateArray())
        {
            if (Text(entry, "signature") is not { } signature
                || recorded.Contains(signature)
                || (entry.TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null)
                || !entry.TryGetProperty("slot", out var slot) || slot.ValueKind != JsonValueKind.Number
                || slot.GetUInt64() < floor)
                continue;
            if (++read > PendingReads)
                break;
            JsonElement transaction;
            try
            {
                transaction = await rpc.CallAsync("getTransaction",
                    [signature, new { commitment = Confirmed, maxSupportedTransactionVersion = MaxTransactionVersion, encoding = "json" }], ct);
            }
            catch (Exception ex) when (JsonRpcClient.RpcError(ex) is { Code: UnsupportedTransactionVersion })
            {
                // Neither this nor the listener can read it, so it will not settle the invoice either way.
                continue;
            }
            if (transaction.ValueKind != JsonValueKind.Object)
                continue;
            var payment = SolanaTransfers.Parse(transaction, wallet, tokens);
            if (payment.Incoming.Count > 0 && payment.Accounts.Contains(reference, StringComparer.Ordinal))
                return signature;
        }
        return null;
    }

    /// <summary>
    /// Where a transaction the pay page handed out stands. A node that does not know the signature has either not
    /// seen it yet or never will; the block height the transaction named tells the two apart. A caller that does not
    /// know that height passes zero, and then an unknown signature is only ever one that may still arrive.
    /// </summary>
    public async Task<SolanaSignatureState> SignatureStateAsync(string signature, ulong lastValidBlockHeight, CancellationToken ct)
    {
        var answer = await rpc.CallAsync("getSignatureStatuses", [new[] { signature }, new { searchTransactionHistory = true }], ct);
        if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.Object)
        {
            return value[0].TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null
                ? SolanaSignatureState.Failed
                : SolanaSignatureState.Paid;
        }
        if (lastValidBlockHeight == 0)
            return SolanaSignatureState.OnItsWay;
        var height = (await rpc.CallAsync("getBlockHeight", [new { commitment = Confirmed }], ct)).GetUInt64();
        return height > lastValidBlockHeight ? SolanaSignatureState.Gone : SolanaSignatureState.OnItsWay;
    }

    /// <summary>What a token account holds, as the node describes it: the amount, the token, and whose it is.</summary>
    sealed record TokenHolding(BigInteger Amount, int Decimals, bool Frozen, string Owner, string Mint)
    {
        public static TokenHolding? Read(JsonElement? account) =>
            account is { } existing
            && existing.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("parsed", out var parsed) && parsed.ValueKind == JsonValueKind.Object
            && parsed.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("tokenAmount", out var held) && held.ValueKind == JsonValueKind.Object
            && Text(held, "amount") is { } units && BigInteger.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            && held.TryGetProperty("decimals", out var decimals) && decimals.ValueKind == JsonValueKind.Number
            && Text(info, "owner") is { } owner && Text(info, "mint") is { } mint
                ? new TokenHolding(amount, decimals.GetInt32(), Text(info, "state") == "frozen", owner, mint)
                : null;
    }

    static BigInteger Lamports(JsonElement account) =>
        account.TryGetProperty("lamports", out var lamports) && lamports.ValueKind == JsonValueKind.Number ? lamports.GetUInt64() : BigInteger.Zero;

    static string Sol(BigInteger lamports) => TokenAmount.Format(lamports, NativeDecimals);

    static bool IsSmallRemainder(BigInteger left) => left.Sign > 0 && left < SmallRemainder;

    string TooLittleLeft(BigInteger left, ulong minimum) =>
        $"After this payment the wallet would keep {Sol(left)} {Chain.NativeSymbol}, and an account may hold nothing or at least {Sol(minimum)} {Chain.NativeSymbol}. Add a little {Chain.NativeSymbol} to the wallet, or pay from another one.";

    /// <summary>The least an account without data may hold; it holds that much or nothing. A network parameter, so it is kept for a while.</summary>
    async Task<ulong> MinimumBalanceAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref minimumBalance) is { } known && known.IsFresh(MinimumBalanceLifetime))
            return known.Value;
        var minimum = (await rpc.CallAsync("getMinimumBalanceForRentExemption", [0], ct)).GetUInt64();
        Volatile.Write(ref minimumBalance, new Cached<ulong>(minimum, DateTimeOffset.UtcNow));
        return minimum;
    }

    /// <summary>
    /// A blockhash a transaction can name for about the next half minute: 150 blocks at the pace of November 2026.
    /// Kept for <see cref="BlockhashLifetime"/>, so a burst of clicks costs one call.
    /// </summary>
    async Task<RecentBlockhash> RecentBlockhashAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref blockhash) is { } known && known.IsFresh(BlockhashLifetime))
            return known.Value;
        var answer = await rpc.CallAsync("getLatestBlockhash", [new { commitment = Confirmed }], ct);
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("value", out var value)
            || Text(value, "blockhash") is not { } hash
            || !value.TryGetProperty("lastValidBlockHeight", out var height) || height.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("The node did not give a recent blockhash.");
        var recent = new RecentBlockhash(hash, height.GetUInt64());
        Volatile.Write(ref blockhash, new Cached<RecentBlockhash>(recent, DateTimeOffset.UtcNow));
        return recent;
    }

    /// <summary>Several accounts in one call, each as the node describes it or null when nothing exists there.</summary>
    async Task<JsonElement?[]> AccountsAsync(string[] addresses, CancellationToken ct)
    {
        var answer = await rpc.CallAsync("getMultipleAccounts", [addresses, new { commitment = Confirmed, encoding = "jsonParsed" }], ct);
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != addresses.Length)
            throw new InvalidOperationException("The node did not describe the accounts it was asked for.");
        return value.EnumerateArray().Select(account => account.ValueKind == JsonValueKind.Object ? account : (JsonElement?)null).ToArray();
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
                [signature, new { commitment = Finalized, maxSupportedTransactionVersion = MaxTransactionVersion, encoding = "json" }], ct);
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
        return tokenAccounts[(wallet, mint)] = SolanaAddress.AssociatedTokenAddress(wallet, mint, await TokenProgramAsync(mint, ct));
    }

    /// <summary>The token program that issued a token: the account that owns its mint.</summary>
    async Task<string> TokenProgramAsync(string mint, CancellationToken ct)
    {
        if (tokenPrograms.TryGetValue(mint, out var known))
            return known;
        var program = await AccountAsync(mint, ct) is { } account ? Text(account, "owner") : null;
        if (program is not (SolanaAddress.TokenProgram or SolanaAddress.Token2022Program))
            throw new InvalidOperationException($"{mint} is not a token on Solana {Chain.Network}.");
        return tokenPrograms[mint] = program;
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
