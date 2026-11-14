using System.Numerics;
using NeoPaymentWatcher.Chains.Evm;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>
/// An EVM node (Neo X, BNB Smart Chain, Arc) reduced to what the plugins ask for: eth_chainId, eth_blockNumber,
/// eth_getBlockByNumber with transactions, eth_getTransactionReceipt, eth_getLogs answered by block hash and, when
/// <see cref="AnswersLogRanges"/> is set, over a span of blocks, and eth_call through <see cref="Call"/>.
/// Blocks carry native transfers in their transaction list and ERC-20 transfers as Transfer logs.
/// </summary>
internal sealed class FakeEvmNode : FakeJsonRpcNode
{
    readonly List<JObject> blocks = [];
    readonly Dictionary<string, JObject> receipts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, JArray> logs = new(StringComparer.OrdinalIgnoreCase);

    public long ChainId { get; }

    /// <summary>Most test chains are read block by block, and a span query there would be a bug; Arc is read by span.</summary>
    public bool AnswersLogRanges { get; set; }

    /// <summary>When set, a span query leaves out the logs of its last block and reports no error, as a node does that is one block behind.</summary>
    public bool HidesNewestLogs { get; set; }

    /// <summary>Answers eth_call: the contract and the call data in, the return data out; null reverts.</summary>
    public Func<string, string, string?>? Call { get; set; }

    public FakeEvmNode(long chainId = 12227332, int initialHeight = 100)
    {
        ChainId = chainId;
        for (var i = 0; i <= initialHeight; i++)
            AddBlock();
    }

    public int Tip
    {
        get
        {
            lock (Gate)
                return blocks.Count - 1;
        }
    }

    public int AddBlock() => AddBlock(out _);

    public void AddBlocks(int count)
    {
        for (var i = 0; i < count; i++)
            AddBlock();
    }

    /// <summary>Appends a block, empty or with one native transfer of <paramref name="wei"/>.</summary>
    public int AddBlock(out string? txId, string? from = null, string? to = null, BigInteger? wei = null, bool success = true)
    {
        lock (Gate)
        {
            var index = blocks.Count;
            var hash = Hash($"evm-block:{index}");
            var transactions = new JArray();
            txId = null;
            if (to is not null)
            {
                txId = Hash($"evm-tx:{index}:{to}:{wei}");
                transactions.Add(new JObject
                {
                    ["hash"] = txId,
                    ["from"] = from,
                    ["to"] = to,
                    ["value"] = Hex(wei ?? BigInteger.Zero),
                });
                receipts[txId] = Receipt(txId, hash, index, success);
            }
            blocks.Add(Block(index, hash, transactions));
            return index;
        }
    }

    /// <summary>Appends a block with one ERC-20 transfer: a transaction to the token contract and the Transfer log it emitted.</summary>
    public int AddTokenTransfer(out string txId, string token, string from, string to, BigInteger amount)
    {
        lock (Gate)
        {
            var index = blocks.Count;
            var hash = Hash($"evm-block:{index}");
            txId = Hash($"evm-token-tx:{index}:{token}:{to}:{amount}");
            var transactions = new JArray(new JObject { ["hash"] = txId, ["from"] = from, ["to"] = token, ["value"] = "0x0" });
            receipts[txId] = Receipt(txId, hash, index, success: true);
            logs[hash] = new JArray(new JObject
            {
                ["address"] = token,
                ["topics"] = new JArray(EvmTransfers.TransferTopic, EvmHex.AddressTopic(from.ToLowerInvariant()), EvmHex.AddressTopic(to.ToLowerInvariant())),
                ["data"] = Hex(amount),
                ["transactionHash"] = txId,
                ["blockHash"] = hash,
                ["blockNumber"] = EvmHex.Quantity((ulong)index),
                ["logIndex"] = "0x0",
                ["removed"] = false,
            });
            blocks.Add(Block(index, hash, transactions));
            return index;
        }
    }

    /// <summary>
    /// Appends a block with one transaction, which may carry native value, and the Transfer logs it emitted, in order:
    /// on Arc a call of USDC's ERC-20 interface emits two, and a plain transfer of the coin emits one next to its value.
    /// </summary>
    public int AddLogs(out string txId, string from, string to, BigInteger wei, params (string Emitter, string From, string To, BigInteger Amount)[] entries)
    {
        lock (Gate)
        {
            var index = blocks.Count;
            var hash = Hash($"evm-block:{index}");
            txId = Hash($"evm-logs-tx:{index}:{to}:{wei}:{entries.Length}");
            var transactions = new JArray(new JObject { ["hash"] = txId, ["from"] = from, ["to"] = to, ["value"] = Hex(wei) });
            receipts[txId] = Receipt(txId, hash, index, success: true);
            var emitted = new JArray();
            for (var i = 0; i < entries.Length; i++)
            {
                emitted.Add(new JObject
                {
                    ["address"] = entries[i].Emitter,
                    ["topics"] = new JArray(EvmTransfers.TransferTopic, EvmHex.AddressTopic(entries[i].From.ToLowerInvariant()), EvmHex.AddressTopic(entries[i].To.ToLowerInvariant())),
                    ["data"] = Hex(entries[i].Amount),
                    ["transactionHash"] = txId,
                    ["blockHash"] = hash,
                    ["blockNumber"] = EvmHex.Quantity((ulong)index),
                    ["logIndex"] = EvmHex.Quantity((ulong)i),
                    ["removed"] = false,
                });
            }
            logs[hash] = emitted;
            blocks.Add(Block(index, hash, transactions));
            return index;
        }
    }

    static string Hex(BigInteger value) => "0x" + value.ToString("x").TrimStart('0').PadLeft(1, '0');

    static JObject Receipt(string txId, string blockHash, int index, bool success) => new()
    {
        ["status"] = success ? "0x1" : "0x0",
        ["blockHash"] = blockHash,
        ["blockNumber"] = EvmHex.Quantity((ulong)index),
        ["transactionHash"] = txId,
    };

    JObject Block(int index, string hash, JArray transactions) => new()
    {
        ["number"] = EvmHex.Quantity((ulong)index),
        ["hash"] = hash,
        ["parentHash"] = index == 0 ? Hash("evm-genesis") : blocks[index - 1]["hash"],
        ["timestamp"] = EvmHex.Quantity((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        ["transactions"] = transactions,
    };

    protected override JToken? Reply(string method, JToken? parameters)
    {
        var args = parameters as JArray ?? [];
        lock (Gate)
        {
            switch (method)
            {
                case "eth_chainId":
                    return EvmHex.Quantity((ulong)ChainId);
                case "eth_blockNumber":
                    return EvmHex.Quantity((ulong)(blocks.Count - 1));
                case "eth_getBlockByNumber":
                    {
                        var tag = args[0]!.Value<string>()!;
                        var index = tag is "latest" or "finalized" or "safe" ? blocks.Count - 1 : (int)EvmHex.ToInt64(tag);
                        return index >= 0 && index < blocks.Count ? blocks[index] : null;
                    }
                case "eth_getTransactionReceipt":
                    return receipts.TryGetValue(args[0]!.Value<string>()!, out var receipt) ? receipt : null;
                case "eth_getLogs":
                    {
                        var filter = (JObject)args[0]!;
                        var blockHash = filter["blockHash"]?.Value<string>();
                        if (blockHash is null && AnswersLogRanges)
                            return LogsInRange(filter);
                        if (blockHash is null)
                            throw new RpcError(-32602, "this node only answers eth_getLogs by block hash");
                        if (blocks.All(b => !string.Equals(b["hash"]!.Value<string>(), blockHash, StringComparison.OrdinalIgnoreCase)))
                            throw new RpcError(-32000, "unknown block");
                        return logs.TryGetValue(blockHash, out var entries) ? entries : new JArray();
                    }
                case "eth_call":
                    {
                        var call = (JObject)args[0]!;
                        return Call?.Invoke(call["to"]!.Value<string>()!, call["data"]!.Value<string>()!)
                               ?? throw new RpcError(-32000, "execution reverted");
                    }
                default:
                    throw new RpcError(-32601, $"Method not found: {method}");
            }
        }
    }

    /// <summary>Logs of blocks fromBlock to toBlock from the named contracts to the named receivers; a span past the tip is an error, as on Arc's nodes.</summary>
    JArray LogsInRange(JObject filter)
    {
        var from = (int)EvmHex.ToInt64(filter["fromBlock"]!.Value<string>());
        var to = (int)EvmHex.ToInt64(filter["toBlock"]!.Value<string>());
        if (to >= blocks.Count)
            throw new RpcError(-32014, "block range extends beyond current head block");
        var emitters = Strings(filter["address"]);
        var receivers = Strings((filter["topics"] as JArray)?.ElementAtOrDefault(2));
        var found = new JArray();
        for (var index = Math.Max(0, from); index <= (HidesNewestLogs ? to - 1 : to); index++)
        {
            if (!logs.TryGetValue(blocks[index]["hash"]!.Value<string>()!, out var entries))
                continue;
            foreach (var entry in entries)
            {
                if (emitters.Count > 0 && !emitters.Contains(entry["address"]!.Value<string>()!))
                    continue;
                if (receivers.Count > 0 && !receivers.Contains(entry["topics"]![2]!.Value<string>()!))
                    continue;
                found.Add(entry);
            }
        }
        return found;
    }

    static HashSet<string> Strings(JToken? token) => token switch
    {
        JArray array => array.Select(t => t.Value<string>()!).ToHashSet(StringComparer.OrdinalIgnoreCase),
        JValue { Type: JTokenType.String } value => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value.Value<string>()! },
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };
}
