using System.Numerics;
using NeoPaymentWatcher.Chains.Evm;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>
/// An EVM node (Neo X, BNB Smart Chain) reduced to what the plugins ask for: eth_chainId, eth_blockNumber,
/// eth_getBlockByNumber with transactions, eth_getTransactionReceipt, and eth_getLogs answered by block hash.
/// Blocks carry native transfers in their transaction list and ERC-20 transfers as Transfer logs.
/// </summary>
internal sealed class FakeEvmNode : FakeJsonRpcNode
{
    readonly List<JObject> blocks = [];
    readonly Dictionary<string, JObject> receipts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, JArray> logs = new(StringComparer.OrdinalIgnoreCase);

    public long ChainId { get; }

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

    protected override JToken? Reply(string method, JArray parameters)
    {
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
                        var tag = parameters[0]!.Value<string>()!;
                        var index = tag is "latest" or "finalized" or "safe" ? blocks.Count - 1 : (int)EvmHex.ToInt64(tag);
                        return index >= 0 && index < blocks.Count ? blocks[index] : null;
                    }
                case "eth_getTransactionReceipt":
                    return receipts.TryGetValue(parameters[0]!.Value<string>()!, out var receipt) ? receipt : null;
                case "eth_getLogs":
                    {
                        var filter = (JObject)parameters[0]!;
                        var blockHash = filter["blockHash"]?.Value<string>();
                        if (blockHash is null)
                            throw new RpcError(-32602, "this node only answers eth_getLogs by block hash");
                        if (blocks.All(b => !string.Equals(b["hash"]!.Value<string>(), blockHash, StringComparison.OrdinalIgnoreCase)))
                            throw new RpcError(-32000, "unknown block");
                        return logs.TryGetValue(blockHash, out var entries) ? entries : new JArray();
                    }
                default:
                    throw new RpcError(-32601, $"Method not found: {method}");
            }
        }
    }
}
