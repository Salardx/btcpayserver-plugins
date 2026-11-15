using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.Chains.Solana;
using NeoPaymentWatcher.Chains;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>
/// A Solana node reduced to what the plugin asks for: getGenesisHash, getSlot, getSignaturesForAddress (newest first,
/// with <c>before</c> and <c>minContextSlot</c>), getTransaction with the balances before and after, getAccountInfo
/// and getMinimumBalanceForRentExemption. Transactions are indexed by every account they name, as on a real node, so
/// a Solana Pay reference finds its payment and a token transfer is found at the token account, not at the wallet.
/// A transaction of a numbered version is returned only to a client that names that version or a newer one.
/// </summary>
internal sealed class FakeSolanaNode : FakeJsonRpcNode
{
    public const string Usdc = "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";
    public const string Devnet = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";
    public const ulong RentExemptMinimum = 650_240;

    /// <summary>What every account of a fake transaction holds before it: 1000 SOL, and a payer's token account 1,000,000 units of a 6-decimal token.</summary>
    const long StartingLamports = 1_000_000_000_000;
    const long StartingTokens = 1_000_000_000_000;

    sealed record Entry(string Signature, ulong Slot, long BlockTime, bool Failed, JObject Transaction);

    readonly Dictionary<string, JObject> accounts = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Entry>> histories = new(StringComparer.Ordinal);
    readonly Dictionary<string, Entry> transactions = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> tokenPrograms = new(StringComparer.Ordinal);
    ulong slot;
    int counter;

    public string Genesis { get; set; } = Devnet;

    /// <summary>How many of an address's newest transactions getSignaturesForAddress leaves out, as a node does whose address index is behind its blocks.</summary>
    public int HiddenSignatures { get; set; }

    /// <summary>When set, getTransaction answers null, as a node does that has not caught up with the one that listed the signature.</summary>
    public bool HidesTransactions { get; set; }

    public FakeSolanaNode(ulong initialSlot = 400_000_000)
    {
        slot = initialSlot;
        AddMint(Usdc, SolanaAddress.TokenProgram, 6);
    }

    public ulong Slot
    {
        get
        {
            lock (Gate)
                return slot;
        }
    }

    public void AddSlots(int count)
    {
        lock (Gate)
            slot += (ulong)count;
    }

    /// <summary>A transaction as getTransaction returns it.</summary>
    public JObject Transaction(string signature)
    {
        lock (Gate)
            return transactions[signature].Transaction;
    }

    /// <summary>A fresh address, the same for the same name.</summary>
    public static string Address(string name) => Base58.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("solana-address:" + name)));

    public void AddMint(string mint, string program, int decimals)
    {
        lock (Gate)
        {
            tokenPrograms[mint] = program;
            accounts[mint] = Account(program, new JObject { ["parsed"] = new JObject { ["info"] = new JObject { ["decimals"] = decimals }, ["type"] = "mint" } });
        }
    }

    /// <summary>A wallet that exists on chain: a system account holding some SOL.</summary>
    public void AddWallet(string wallet)
    {
        lock (Gate)
            accounts[wallet] = Account(SolanaAddress.SystemProgram, new JArray("", "base64"));
    }

    /// <summary>An account owned by some program, which is not a wallet.</summary>
    public void AddProgramAccount(string address, string program)
    {
        lock (Gate)
            accounts[address] = Account(program, new JArray("", "base64"));
    }

    /// <summary>The wallet's token account for a token, at its derived address.</summary>
    public string AddTokenAccount(string wallet, string mint, bool frozen = false)
    {
        lock (Gate)
        {
            var program = tokenPrograms[mint];
            var address = SolanaAddress.AssociatedTokenAddress(wallet, mint, program);
            accounts[address] = Account(program, new JObject
            {
                ["parsed"] = new JObject
                {
                    ["info"] = new JObject { ["mint"] = mint, ["owner"] = wallet, ["state"] = frozen ? "frozen" : "initialized" },
                    ["type"] = "account",
                },
            });
            return address;
        }
    }

    /// <summary>
    /// A transfer of SOL in a new slot, as a wallet builds it from a Solana Pay request: payer, receiver, the references
    /// as extra accounts, the system program.
    /// </summary>
    public ulong AddSolTransfer(out string signature, string from, string to, ulong lamports, string[]? references = null, bool failed = false, string? signedAlsoBy = null,
        int? version = null)
    {
        lock (Gate)
        {
            var keys = new List<string> { from };
            if (signedAlsoBy is not null)
                keys.Add(signedAlsoBy);
            if (!keys.Contains(to))
                keys.Add(to);
            keys.AddRange(references ?? []);
            keys.Add(SolanaAddress.SystemProgram);
            if (lamports > StartingLamports / 2)
                throw new ArgumentOutOfRangeException(nameof(lamports), "The fake payer does not hold that much.");
            var before = keys.Select(_ => (BigInteger)StartingLamports).ToArray();
            var after = before.ToArray();
            after[0] -= 5000;
            if (!failed)
            {
                after[keys.IndexOf(from)] -= lamports;
                after[keys.IndexOf(to)] += lamports;
            }
            var meta = Meta(failed, before, after, [], []);
            return Add(out signature, keys, signedAlsoBy is null ? 1 : 2, meta, failed, version: version);
        }
    }

    /// <summary>
    /// A token transfer in a new slot: payer, the sender's token account, the receiver's token account, the mint, the
    /// references, the token program. The receiving wallet itself is not among the accounts, as on chain.
    /// <paramref name="received"/> is what arrives when the token keeps a fee; by default all of it.
    /// </summary>
    public ulong AddTokenTransfer(out string signature, string mint, string fromWallet, string toWallet, ulong amount,
        string[]? references = null, bool failed = false, ulong? received = null, bool referencesFromLookupTable = false, int? version = null)
    {
        lock (Gate)
        {
            var program = tokenPrograms[mint];
            var source = SolanaAddress.AssociatedTokenAddress(fromWallet, mint, program);
            var destination = SolanaAddress.AssociatedTokenAddress(toWallet, mint, program);
            var keys = new List<string> { fromWallet, source, destination, mint };
            var loaded = new List<string>();
            if (referencesFromLookupTable)
                loaded.AddRange(references ?? []);
            else
                keys.AddRange(references ?? []);
            keys.Add(program);
            var all = keys.Concat(loaded).ToList();
            var before = all.Select(_ => (BigInteger)2_039_280).ToArray();
            var after = before.ToArray();
            after[0] -= 5000;
            if (amount > StartingTokens / 2)
                throw new ArgumentOutOfRangeException(nameof(amount), "The fake payer does not hold that much.");
            var arrived = failed ? 0 : received ?? amount;
            var spent = failed ? 0 : amount;
            JObject Balance(int index, string owner, BigInteger units) => new()
            {
                ["accountIndex"] = index,
                ["mint"] = mint,
                ["owner"] = owner,
                ["programId"] = program,
                ["uiTokenAmount"] = new JObject { ["amount"] = units.ToString(), ["decimals"] = 6 },
            };
            var meta = Meta(failed, before, after,
                [Balance(1, fromWallet, StartingTokens), Balance(2, toWallet, 1_000_000)],
                [Balance(1, fromWallet, StartingTokens - (long)spent), Balance(2, toWallet, 1_000_000 + arrived)]);
            meta["loadedAddresses"] = new JObject { ["writable"] = new JArray(), ["readonly"] = new JArray(loaded) };
            return Add(out signature, keys, 1, meta, failed, loaded, version);
        }
    }

    /// <summary>
    /// A node labels a transaction "legacy" or with its version number: 0 when it loads addresses from a lookup table,
    /// 1 for the format of September 2026, whose message also carries the limits the sender set.
    /// </summary>
    ulong Add(out string signature, List<string> keys, int signers, JObject meta, bool failed, List<string>? loaded = null, int? version = null)
    {
        slot++;
        counter++;
        var seed = Encoding.UTF8.GetBytes($"solana-tx:{slot}:{counter}");
        signature = Base58.Encode(SHA512.HashData(seed));
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JToken label = "legacy";
        if (version is { } number)
            label = number;
        else if (loaded is { Count: > 0 })
            label = 0;
        var message = new JObject
        {
            ["accountKeys"] = new JArray(keys),
            ["header"] = new JObject { ["numRequiredSignatures"] = signers, ["numReadonlySignedAccounts"] = 0, ["numReadonlyUnsignedAccounts"] = 1 },
            ["instructions"] = new JArray(),
            ["recentBlockhash"] = Address("blockhash"),
        };
        if (version >= 1)
        {
            message["transactionConfig"] = new JObject
            {
                ["computeUnitLimit"] = 10_000,
                ["heapSize"] = JValue.CreateNull(),
                ["loadedAccountsDataSizeLimit"] = 67_108_864,
                ["priorityFee"] = 0,
            };
        }
        var transaction = new JObject
        {
            ["slot"] = slot,
            ["blockTime"] = time,
            ["version"] = label,
            ["meta"] = meta,
            ["transaction"] = new JObject { ["signatures"] = new JArray(signature), ["message"] = message },
        };
        var entry = new Entry(signature, slot, time, failed, transaction);
        transactions[signature] = entry;
        foreach (var key in keys.Concat(loaded ?? []))
        {
            if (!histories.TryGetValue(key, out var history))
                histories[key] = history = [];
            history.Add(entry);
        }
        return slot;
    }

    static JObject Meta(bool failed, BigInteger[] before, BigInteger[] after, JObject[] tokensBefore, JObject[] tokensAfter) => new()
    {
        ["err"] = failed ? new JObject { ["InstructionError"] = new JArray(0, "Custom") } : JValue.CreateNull(),
        ["fee"] = 5000,
        ["preBalances"] = new JArray(before.Select(b => (ulong)b)),
        ["postBalances"] = new JArray(after.Select(b => (ulong)b)),
        ["preTokenBalances"] = new JArray(tokensBefore.Cast<object>().ToArray()),
        ["postTokenBalances"] = new JArray(tokensAfter.Cast<object>().ToArray()),
        ["loadedAddresses"] = new JObject { ["writable"] = new JArray(), ["readonly"] = new JArray() },
    };

    static JObject Account(string owner, JToken data) => new()
    {
        ["data"] = data,
        ["executable"] = false,
        ["lamports"] = 2_039_280,
        ["owner"] = owner,
        ["space"] = 0,
    };

    protected override JToken? Reply(string method, JToken? parameters)
    {
        var args = parameters as JArray ?? [];
        lock (Gate)
        {
            switch (method)
            {
                case "getGenesisHash":
                    return Genesis;
                case "getSlot":
                    return slot;
                case "getMinimumBalanceForRentExemption":
                    return RentExemptMinimum;
                case "getAccountInfo":
                    {
                        var address = args[0]!.Value<string>()!;
                        return new JObject
                        {
                            ["context"] = new JObject { ["slot"] = slot },
                            ["value"] = accounts.TryGetValue(address, out var account) ? account : JValue.CreateNull(),
                        };
                    }
                case "getSignaturesForAddress":
                    {
                        var address = args[0]!.Value<string>()!;
                        var options = args.Count > 1 ? (JObject)args[1]! : new JObject();
                        if (options["minContextSlot"]?.Value<ulong>() > slot)
                            throw new RpcError(-32016, "Minimum context slot has not been reached");
                        var limit = options["limit"]?.Value<int>() ?? 1000;
                        var before = options["before"]?.Value<string>();
                        var newestFirst = histories.TryGetValue(address, out var history) ? history.AsEnumerable().Reverse().Skip(HiddenSignatures).ToList() : [];
                        if (before is not null)
                            newestFirst = newestFirst.SkipWhile(e => e.Signature != before).Skip(1).ToList();
                        return new JArray(newestFirst.Take(limit).Select(e => new JObject
                        {
                            ["signature"] = e.Signature,
                            ["slot"] = e.Slot,
                            ["blockTime"] = e.BlockTime,
                            ["err"] = e.Failed ? new JObject { ["InstructionError"] = new JArray(0, "Custom") } : JValue.CreateNull(),
                            ["memo"] = JValue.CreateNull(),
                            ["confirmationStatus"] = "finalized",
                        }));
                    }
                case "getTransaction":
                    {
                        if (HidesTransactions || !transactions.TryGetValue(args[0]!.Value<string>()!, out var found))
                            return null;
                        // A numbered version goes only to a client that says it reads that version; "legacy" needs no saying.
                        var supported = args.Count > 1 ? args[1]!["maxSupportedTransactionVersion"]?.Value<int?>() : null;
                        if (found.Transaction["version"] is JValue { Type: JTokenType.Integer } numbered && !(numbered.Value<int>() <= supported))
                        {
                            throw new RpcError(-32015, $"Transaction version ({numbered.Value<int>()}) is not supported by the requesting client. "
                                                       + $"Please try the request again with the following configuration parameter: \"maxSupportedTransactionVersion\": {numbered.Value<int>()}");
                        }
                        return found.Transaction;
                    }
                default:
                    throw new RpcError(-32601, $"Method not found: {method}");
            }
        }
    }
}
