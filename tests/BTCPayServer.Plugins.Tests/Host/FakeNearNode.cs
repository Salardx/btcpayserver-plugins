using System.Globalization;
using System.Numerics;
using System.Text;
using BTCPayServer.Plugins.Chains.Near;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>
/// A NEAR node reduced to what the plugin asks for: status, block (final, by height or by hash), chunk, query
/// (view_account and the NEP-141 view calls, at a height or final), EXPERIMENTAL_changes for an account and
/// EXPERIMENTAL_receipt. It keeps every block's balances, so a balance can be read at any height the way a node
/// serves it, and lists transactions and receipts in chunks the way the chain does: a transfer within a shard is a
/// transaction in its block and a receipt nobody lists; a transfer across shards is a transaction in one block
/// and a receipt in the next block's chunk, where the balance moves. Heights can be skipped. Errors travel on
/// HTTP 422 like behind a public proxy.
/// </summary>
internal sealed class FakeNearNode : FakeJsonRpcNode
{
    public const string Usdc = "3e2210e1184b45b64c8a434c0a7e7b23cc04ea7eb7a6c3c32520d03d4afcb8af";
    const string EmptyRoot = NearRpc.EmptyRoot;
    const string NoCode = "11111111111111111111111111111111";

    sealed class Chunk
    {
        public required string Hash { get; init; }
        public required int Shard { get; init; }
        public JArray Transactions { get; } = [];
        public JArray Receipts { get; } = [];
    }

    sealed class Block
    {
        public required int Height { get; init; }
        public required string Hash { get; init; }
        public required string PrevHash { get; init; }
        public required long TimeNs { get; init; }
        public List<Chunk> Chunks { get; } = [];
        public required Dictionary<string, BigInteger> Native { get; init; }
        public required Dictionary<(string Token, string Account), BigInteger> Tokens { get; init; }
        /// <summary>account_changes entries of this block, in execution order.</summary>
        public List<JObject> Changes { get; } = [];
    }

    readonly List<Block?> blocks = [];
    readonly Dictionary<string, Block> byHash = new(StringComparer.Ordinal);
    readonly Dictionary<string, JObject> receipts = new(StringComparer.Ordinal);
    readonly HashSet<(string Token, string Account)> registered = [];
    Dictionary<string, BigInteger> native = new(StringComparer.Ordinal);
    Dictionary<(string Token, string Account), BigInteger> tokens = [];
    int sequence;

    public string ChainId { get; set; } = "testnet";

    /// <summary>Blocks below this height answer like a node that garbage-collected them.</summary>
    public int OldestBlock { get; set; }

    public FakeNearNode(int initialHeight = 100)
    {
        ErrorStatusCode = 422;
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

    /// <summary>Creates or funds an account in a new block; a named account must exist before it can receive, like on chain.</summary>
    public void Fund(string account, BigInteger yocto)
    {
        lock (Gate)
        {
            native[account] = native.GetValueOrDefault(account) + yocto;
            Append();
        }
    }

    public void Register(string token, string account)
    {
        lock (Gate)
            registered.Add((token, account));
    }

    /// <summary>Gives an account a token balance in a new block, registering it with the token contract.</summary>
    public void SetTokenBalance(string token, string account, BigInteger units)
    {
        lock (Gate)
        {
            registered.Add((token, account));
            tokens[(token, account)] = units;
            Append();
        }
    }

    public BigInteger NativeBalance(string account)
    {
        lock (Gate)
            return native.GetValueOrDefault(account);
    }

    /// <summary>Appends an empty block.</summary>
    public int AddBlock()
    {
        lock (Gate)
            return Append().Height;
    }

    public void AddBlocks(int count)
    {
        for (var i = 0; i < count; i++)
            AddBlock();
    }

    /// <summary>Leaves a height without a block, as happens when a block producer misses its slot.</summary>
    public int SkipHeight()
    {
        lock (Gate)
        {
            blocks.Add(null);
            return blocks.Count - 1;
        }
    }

    /// <summary>
    /// A wallet sends NEAR. Within one shard the transaction and the balance change share a block; across shards
    /// the transaction is in one block and the receipt is listed in, and the balance moves in, the next. With a
    /// relayer the transaction is a meta transaction: signed by the relayer, the transfer inside a Delegate action.
    /// Returns the height where the balance moved.
    /// </summary>
    public int AddNativeTransfer(out string txId, string from, string to, BigInteger yocto, bool crossShard = false, string? relayer = null)
    {
        lock (Gate)
        {
            var transfer = new JObject { ["Transfer"] = new JObject { ["deposit"] = Amount(yocto) } };
            var block = Append();
            txId = NextHash("tx");
            block.Chunks[Shard(from)].Transactions.Add(Transaction(txId, relayer ?? from, relayer is null ? to : from,
                relayer is null ? transfer : Delegated(from, to, transfer)));
            var receipt = ReceiptJson(NextHash("receipt"), from, to, transfer);
            if (crossShard)
            {
                block = Append();
                block.Chunks[Shard(from)].Receipts.Add(receipt);
            }
            Move(block, receipt, from, to, yocto);
            return block.Height;
        }
    }

    /// <summary>A contract sends NEAR: there is no transaction to the account, only a receipt from the contract.</summary>
    public int AddContractTransfer(out string receiptId, string contract, string to, BigInteger yocto)
    {
        lock (Gate)
        {
            var transfer = new JObject { ["Transfer"] = new JObject { ["deposit"] = Amount(yocto) } };
            receiptId = NextHash("receipt");
            var receipt = ReceiptJson(receiptId, contract, to, transfer);
            var block = Append();
            block.Chunks[Shard(contract)].Receipts.Add(receipt);
            Move(block, receipt, contract, to, yocto);
            return block.Height;
        }
    }

    /// <summary>The account's own money coming back after a failed call of its own: a transfer from "system".</summary>
    public int AddRefund(string to, BigInteger yocto)
    {
        lock (Gate)
        {
            var transfer = new JObject { ["Transfer"] = new JObject { ["deposit"] = Amount(yocto) } };
            var receipt = ReceiptJson(NextHash("receipt"), "system", to, transfer);
            var block = Append();
            block.Chunks[Shard(to)].Receipts.Add(receipt);
            Move(block, receipt, null, to, yocto);
            return block.Height;
        }
    }

    /// <summary>The account spends: a transaction it signed, which lowers its balance.</summary>
    public int AddSpending(string from, string to, BigInteger yocto)
    {
        lock (Gate)
        {
            var block = Append();
            var txId = NextHash("tx");
            var transfer = new JObject { ["Transfer"] = new JObject { ["deposit"] = Amount(yocto) } };
            block.Chunks[Shard(from)].Transactions.Add(Transaction(txId, from, to, transfer));
            native[from] = native.GetValueOrDefault(from) - yocto;
            native[to] = native.GetValueOrDefault(to) + yocto;
            block.Changes.Add(Change(from, "transaction_processing", "tx_hash", txId, native[from]));
            block.Native[from] = native[from];
            block.Native[to] = native[to];
            return block.Height;
        }
    }

    /// <summary>A wallet calls ft_transfer on the token contract. A failed call leaves the balances alone. Returns the height where the balance moved.</summary>
    public int AddTokenTransfer(out string txId, string token, string from, string to, BigInteger units, string? memo = null, bool crossShard = false, bool fail = false)
    {
        lock (Gate)
        {
            var call = FungibleCall(to, units, memo);
            var block = Append();
            txId = NextHash("tx");
            block.Chunks[Shard(from)].Transactions.Add(Transaction(txId, from, token, call));
            if (crossShard)
            {
                block = Append();
                block.Chunks[Shard(from)].Receipts.Add(ReceiptJson(NextHash("receipt"), from, token, call));
            }
            if (!fail)
                MoveToken(block, token, from, to, units);
            return block.Height;
        }
    }

    /// <summary>A contract (an intents settlement, say) calls ft_transfer: a receipt to the token contract, no transaction naming the account.</summary>
    public int AddTokenTransferByContract(out string receiptId, string token, string contract, string to, BigInteger units)
    {
        lock (Gate)
        {
            receiptId = NextHash("receipt");
            var block = Append();
            block.Chunks[Shard(contract)].Receipts.Add(ReceiptJson(receiptId, contract, token, FungibleCall(to, units, null)));
            MoveToken(block, token, contract, to, units);
            return block.Height;
        }
    }

    Block Append()
    {
        var height = blocks.Count;
        var previous = blocks.LastOrDefault(b => b is not null);
        var block = new Block
        {
            Height = height,
            Hash = NextHash("block"),
            PrevHash = previous?.Hash ?? NoCode,
            TimeNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
            Native = new Dictionary<string, BigInteger>(native, StringComparer.Ordinal),
            Tokens = new Dictionary<(string, string), BigInteger>(tokens),
        };
        for (var shard = 0; shard < 4; shard++)
            block.Chunks.Add(new Chunk { Hash = NextHash("chunk"), Shard = shard });
        blocks.Add(block);
        byHash[block.Hash] = block;
        return block;
    }

    void Move(Block block, JObject receipt, string? from, string to, BigInteger yocto)
    {
        var receiptId = receipt["receipt_id"]!.Value<string>()!;
        receipts[receiptId] = receipt;
        if (from is not null)
        {
            native[from] = native.GetValueOrDefault(from) - yocto;
            block.Native[from] = native[from];
        }
        native[to] = native.GetValueOrDefault(to) + yocto;
        block.Native[to] = native[to];
        block.Changes.Add(Change(to, "receipt_processing", "receipt_hash", receiptId, native[to]));
    }

    void MoveToken(Block block, string token, string from, string to, BigInteger units)
    {
        tokens[(token, from)] = tokens.GetValueOrDefault((token, from)) - units;
        tokens[(token, to)] = tokens.GetValueOrDefault((token, to)) + units;
        registered.Add((token, to));
        block.Tokens[(token, from)] = tokens[(token, from)];
        block.Tokens[(token, to)] = tokens[(token, to)];
    }

    string NextHash(string kind) => Hash($"near-{kind}:{++sequence}")[2..];

    static int Shard(string account) => Math.Abs(account.Sum(c => c) % 4);

    static string Amount(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

    static JObject Transaction(string hash, string signer, string receiver, JObject action) => new()
    {
        ["hash"] = hash,
        ["signer_id"] = signer,
        ["receiver_id"] = receiver,
        ["nonce"] = 1,
        ["public_key"] = "ed25519:11111111111111111111111111111111",
        ["signature"] = "ed25519:1111",
        ["actions"] = new JArray(action),
    };

    static JObject Delegated(string sender, string receiver, JObject action) => new()
    {
        ["Delegate"] = new JObject
        {
            ["delegate_action"] = new JObject
            {
                ["sender_id"] = sender,
                ["receiver_id"] = receiver,
                ["actions"] = new JArray(action),
                ["nonce"] = 1,
                ["max_block_height"] = 999_999_999,
                ["public_key"] = "ed25519:11111111111111111111111111111111",
            },
            ["signature"] = "ed25519:1111",
        },
    };

    static JObject ReceiptJson(string id, string predecessor, string receiver, JObject action) => new()
    {
        ["predecessor_id"] = predecessor,
        ["receiver_id"] = receiver,
        ["receipt_id"] = id,
        ["priority"] = 0,
        ["receipt"] = new JObject
        {
            ["Action"] = new JObject
            {
                ["signer_id"] = predecessor,
                ["signer_public_key"] = "ed25519:11111111111111111111111111111111",
                ["gas_price"] = "100000000",
                ["output_data_receivers"] = new JArray(),
                ["input_data_ids"] = new JArray(),
                ["actions"] = new JArray(action),
            },
        },
    };

    static JObject FungibleCall(string to, BigInteger units, string? memo)
    {
        var args = new JObject { ["receiver_id"] = to, ["amount"] = Amount(units) };
        if (memo is not null)
            args["memo"] = memo;
        return new JObject
        {
            ["FunctionCall"] = new JObject
            {
                ["method_name"] = "ft_transfer",
                ["args"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(args.ToString(Formatting.None))),
                ["gas"] = 30_000_000_000_000,
                ["deposit"] = "1",
            },
        };
    }

    static JObject Change(string account, string causeType, string hashName, string hash, BigInteger amount) => new()
    {
        ["cause"] = new JObject { ["type"] = causeType, [hashName] = hash },
        ["type"] = "account_update",
        ["change"] = new JObject
        {
            ["account_id"] = account,
            ["amount"] = Amount(amount),
            ["locked"] = "0",
            ["code_hash"] = NoCode,
            ["storage_usage"] = 182,
            ["storage_paid_at"] = 0,
        },
    };

    protected override JToken? Reply(string method, JToken? parameters)
    {
        lock (Gate)
        {
            var args = parameters as JObject ?? [];
            switch (method)
            {
                case "status":
                    return new JObject
                    {
                        ["chain_id"] = ChainId,
                        ["protocol_version"] = 88,
                        ["sync_info"] = new JObject { ["latest_block_height"] = blocks.Count - 1, ["syncing"] = false },
                    };
                case "block":
                    return BlockJson(ResolveBlock(args));
                case "chunk":
                    {
                        var id = args["chunk_id"]?.Value<string>();
                        var owner = blocks.FirstOrDefault(b => b is not null && b.Chunks.Any(c => c.Hash == id));
                        var chunk = owner?.Chunks.First(c => c.Hash == id) ?? throw new RpcError(-32000, "Server error", "HANDLER_ERROR", "UNKNOWN_CHUNK");
                        return new JObject
                        {
                            ["header"] = ChunkHeader(owner!, chunk),
                            ["transactions"] = chunk.Transactions,
                            ["receipts"] = chunk.Receipts,
                        };
                    }
                case "query":
                    return Query(args);
                case "EXPERIMENTAL_changes":
                case "changes":
                    {
                        var block = ResolveBlock(args);
                        var accounts = args["account_ids"]!.Select(a => a.Value<string>()!).ToHashSet(StringComparer.Ordinal);
                        return new JObject
                        {
                            ["block_hash"] = block.Hash,
                            ["changes"] = new JArray(block.Changes.Where(c => accounts.Contains(c["change"]!["account_id"]!.Value<string>()!))),
                        };
                    }
                case "EXPERIMENTAL_receipt":
                    return receipts.TryGetValue(args["receipt_id"]!.Value<string>()!, out var receipt)
                        ? receipt
                        : throw new RpcError(-32000, "Server error", "HANDLER_ERROR", "UNKNOWN_RECEIPT");
                default:
                    throw new RpcError(-32601, $"Method not found: {method}");
            }
        }
    }

    Block ResolveBlock(JObject args)
    {
        if (args["finality"] is not null)
            return blocks.Last(b => b is not null)!;
        var id = args["block_id"] ?? throw new RpcError(-32700, "Parse error", "REQUEST_VALIDATION_ERROR", "PARSE_ERROR");
        if (id.Type == JTokenType.String)
            return byHash.TryGetValue(id.Value<string>()!, out var found) ? found : throw new RpcError(-32000, "Server error", "HANDLER_ERROR", NearRpc.UnknownBlock);
        var height = id.Value<int>();
        if (height < OldestBlock)
            throw new RpcError(-32000, "Server error", "HANDLER_ERROR", NearRpc.GarbageCollectedBlock);
        if (height < 0 || height >= blocks.Count || blocks[height] is null)
            throw new RpcError(-32000, "Server error", "HANDLER_ERROR", NearRpc.UnknownBlock);
        return blocks[height]!;
    }

    JToken Query(JObject args)
    {
        var block = ResolveBlock(args);
        var account = args["account_id"]!.Value<string>()!;
        switch (args["request_type"]?.Value<string>())
        {
            case "view_account":
                if (!block.Native.TryGetValue(account, out var amount))
                    throw new RpcError(-32000, "Server error", "HANDLER_ERROR", NearRpc.UnknownAccount);
                return new JObject
                {
                    ["amount"] = Amount(amount),
                    ["locked"] = "0",
                    ["code_hash"] = NoCode,
                    ["storage_usage"] = 182,
                    ["storage_paid_at"] = 0,
                    ["block_height"] = block.Height,
                    ["block_hash"] = block.Hash,
                };
            case "call_function":
                {
                    var callArgs = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(args["args_base64"]!.Value<string>()!)));
                    var target = callArgs["account_id"]?.Value<string>() ?? "";
                    var result = args["method_name"]?.Value<string>() switch
                    {
                        "ft_balance_of" => JsonConvert.SerializeObject(Amount(block.Tokens.GetValueOrDefault((account, target)))),
                        "ft_metadata" => """{"spec":"ft-1.0.0","name":"USD Coin","symbol":"USDC","decimals":6}""",
                        "storage_balance_of" => registered.Contains((account, target)) ? """{"total":"1250000000000000000000","available":"0"}""" : "null",
                        "storage_balance_bounds" => """{"min":"1250000000000000000000","max":"1250000000000000000000"}""",
                        _ => throw new RpcError(-32000, "Server error", "HANDLER_ERROR", "CONTRACT_EXECUTION_ERROR"),
                    };
                    return new JObject
                    {
                        ["result"] = new JArray(Encoding.UTF8.GetBytes(result).Select(b => (int)b)),
                        ["logs"] = new JArray(),
                        ["block_height"] = block.Height,
                        ["block_hash"] = block.Hash,
                    };
                }
            default:
                throw new RpcError(-32700, "Parse error", "REQUEST_VALIDATION_ERROR", "PARSE_ERROR");
        }
    }

    static JObject BlockJson(Block block) => new()
    {
        ["author"] = "node0",
        ["header"] = new JObject
        {
            ["height"] = block.Height,
            ["hash"] = block.Hash,
            ["prev_hash"] = block.PrevHash,
            ["timestamp"] = block.TimeNs,
            ["timestamp_nanosec"] = block.TimeNs.ToString(CultureInfo.InvariantCulture),
        },
        ["chunks"] = new JArray(block.Chunks.Select(c => ChunkHeader(block, c))),
    };

    static JObject ChunkHeader(Block block, Chunk chunk) => new()
    {
        ["chunk_hash"] = chunk.Hash,
        ["shard_id"] = chunk.Shard,
        ["height_created"] = block.Height,
        ["height_included"] = block.Height,
        ["tx_root"] = chunk.Transactions.Count == 0 ? EmptyRoot : Hash($"txroot:{chunk.Hash}")[2..],
        ["outgoing_receipts_root"] = chunk.Receipts.Count == 0 ? EmptyRoot : Hash($"rcroot:{chunk.Hash}")[2..],
    };
}
