using System.Globalization;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>
/// A Neo N3 node reduced to the four calls the plugin makes: getversion, getblockcount, getblock and
/// getapplicationlog. Blocks carry real block times, so a transfer added after an invoice was created
/// falls inside the invoice's window the way it does on chain.
/// </summary>
internal sealed class FakeNeoNode : FakeJsonRpcNode
{
    readonly List<JObject> blocks = [];
    readonly Dictionary<string, JObject> logs = new(StringComparer.OrdinalIgnoreCase);

    public uint NetworkMagic { get; set; } = 894710606;

    public FakeNeoNode(int initialHeight = 100)
    {
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

    /// <summary>Appends a block, empty or with one GAS transfer. Returns its index and, through <paramref name="txId"/>, the transaction id.</summary>
    public int AddBlock(string? from = null, string? to = null, long units = 0, bool halt = true, string contract = TokenRegistry.GasHash) =>
        AddBlock(out _, from, to, units, halt, contract);

    public int AddBlock(out string? txId, string? from = null, string? to = null, long units = 0, bool halt = true, string contract = TokenRegistry.GasHash)
    {
        lock (Gate)
        {
            var index = blocks.Count;
            var transactions = new JArray();
            txId = null;
            if (to is not null)
            {
                txId = Hash($"neo:{index}:{to}:{units}");
                transactions.Add(new JObject { ["hash"] = txId });
                logs[txId] = new JObject
                {
                    ["txid"] = txId,
                    ["executions"] = new JArray(new JObject
                    {
                        ["trigger"] = "Application",
                        ["vmstate"] = halt ? "HALT" : "FAULT",
                        ["notifications"] = new JArray(new JObject
                        {
                            ["contract"] = contract,
                            ["eventname"] = "Transfer",
                            ["state"] = new JObject
                            {
                                ["type"] = "Array",
                                ["value"] = new JArray(Account(from), Account(to),
                                    new JObject { ["type"] = "Integer", ["value"] = units.ToString(CultureInfo.InvariantCulture) }),
                            },
                        }),
                    }),
                };
            }
            blocks.Add(new JObject
            {
                ["index"] = index,
                ["hash"] = Hash($"neo-block:{index}"),
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["tx"] = transactions,
            });
            return index;
        }
    }

    static JObject Account(string? address)
    {
        if (address is null)
            return new JObject { ["type"] = "Any", ["value"] = null };
        if (!NeoAddress.TryGetScriptHash(address, out var hash))
            throw new ArgumentException($"{address} is not a Neo N3 address.", nameof(address));
        return new JObject { ["type"] = "ByteString", ["value"] = Convert.ToBase64String(hash) };
    }

    protected override JToken? Reply(string method, JArray parameters)
    {
        lock (Gate)
        {
            switch (method)
            {
                case "getversion":
                    return new JObject { ["protocol"] = new JObject { ["network"] = NetworkMagic } };
                case "getblockcount":
                    return blocks.Count;
                case "getblock":
                    {
                        var index = parameters[0]!.Value<int>();
                        if (index < 0 || index >= blocks.Count)
                            throw new RpcError(-100, "Unknown block");
                        return blocks[index];
                    }
                case "getapplicationlog":
                    {
                        var id = parameters[0]!.Value<string>()!;
                        return logs.TryGetValue(id, out var log) ? log : throw new RpcError(-100, "Unknown transaction");
                    }
                default:
                    throw new RpcError(-32601, $"Method not found: {method}");
            }
        }
    }
}
