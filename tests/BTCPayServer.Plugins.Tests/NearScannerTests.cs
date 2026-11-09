using System.Numerics;
using System.Text.Json;
using BTCPayServer.Plugins.Chains.Near;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.NEAR;
using BTCPayServer.Plugins.Tests.Host;
using Microsoft.Extensions.Logging.Abstractions;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Tests;

/// <summary>The NEAR client against a fake node: balances read at heights, bisection, and attribution through account changes and chunks.</summary>
public class NearScannerTests
{
    const string Shop = "shop.testnet";
    const string SecondShop = "pay2.shop.testnet";
    const string Payer = "alice.testnet";
    const string Relayer = "relayer.testnet";
    const string Contract = "intents.testnet";
    static readonly BigInteger OneNear = BigInteger.Pow(10, 24);
    static readonly HashSet<string> Native = [TokenRegistry.Native];
    static readonly HashSet<string> NativeAndUsdc = [TokenRegistry.Native, FakeNearNode.Usdc];

    static NearChainClient Client(FakeNearNode node) =>
        new(NEARChains.Protocol(mainnet: false).ToChainProfile() with { RpcUrls = [node.Url] }, NullLogger<NearChainClient>.Instance);

    static HashSet<string> Watch(params string[] accounts) => new(accounts, StringComparer.Ordinal);

    static FakeNearNode Node()
    {
        var node = new FakeNearNode();
        node.Fund(Shop, OneNear);
        node.Fund(SecondShop, OneNear);
        node.Fund(Payer, 100 * OneNear);
        node.SetTokenBalance(FakeNearNode.Usdc, Payer, 1_000_000_000);
        return node;
    }

    [Fact]
    public async Task A_transfer_within_a_shard_is_found_with_its_transaction()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        node.AddBlocks(3);
        var height = node.AddNativeTransfer(out var txId, Payer, Shop, 3 * OneNear / 2);
        node.AddBlocks(5);

        var range = await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None);

        var transfer = Assert.Single(range.Transfers);
        Assert.Equal(txId, transfer.TxId);
        Assert.Equal(Payer, transfer.From);
        Assert.Equal(Shop, transfer.To);
        Assert.Equal(3 * OneNear / 2, transfer.Amount);
        Assert.Equal((uint)height, transfer.BlockIndex);
        Assert.Equal(TokenRegistry.Native, transfer.TokenHash);
        Assert.Equal((uint)node.Tip, range.To);
        Assert.True(range.ToTime > 0);
    }

    [Fact]
    public async Task A_transfer_across_shards_is_found_in_the_block_where_it_lands()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        var height = node.AddNativeTransfer(out var txId, Payer, Shop, OneNear, crossShard: true);
        node.AddBlocks(2);

        var transfer = Assert.Single((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);

        Assert.Equal(txId, transfer.TxId);
        Assert.Equal((uint)height, transfer.BlockIndex);
        Assert.Equal(Payer, transfer.From);
    }

    [Fact]
    public async Task A_meta_transaction_names_the_sender_not_the_relayer()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        node.AddNativeTransfer(out var txId, Payer, Shop, OneNear / 4, relayer: Relayer);

        var transfer = Assert.Single((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);

        Assert.Equal(txId, transfer.TxId);
        Assert.Equal(Payer, transfer.From);
        Assert.Equal(OneNear / 4, transfer.Amount);
    }

    [Fact]
    public async Task A_transfer_sent_by_a_contract_is_recorded_under_its_receipt()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        var height = node.AddContractTransfer(out var receiptId, Contract, Shop, 2 * OneNear);

        var transfer = Assert.Single((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);

        Assert.Equal(receiptId, transfer.TxId);
        Assert.Equal(Contract, transfer.From);
        Assert.Equal(2 * OneNear, transfer.Amount);
        Assert.Equal((uint)height, transfer.BlockIndex);
    }

    [Fact]
    public async Task A_token_transfer_is_found_by_its_call_within_and_across_shards()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        var first = node.AddTokenTransfer(out var firstTx, FakeNearNode.Usdc, Payer, Shop, 10_000_000, memo: "invoice-1");
        node.AddBlocks(2);
        var second = node.AddTokenTransfer(out var secondTx, FakeNearNode.Usdc, Payer, Shop, 2_500_000, crossShard: true);
        node.AddTokenTransfer(out _, FakeNearNode.Usdc, Payer, Shop, 99_000_000, fail: true);
        node.AddBlocks(2);

        var transfers = (await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), NativeAndUsdc, CancellationToken.None)).Transfers.OrderBy(t => t.BlockIndex).ToArray();

        Assert.Equal(2, transfers.Length);
        Assert.Equal(firstTx, transfers[0].TxId);
        Assert.Equal((uint)first, transfers[0].BlockIndex);
        Assert.Equal(FakeNearNode.Usdc, transfers[0].TokenHash);
        Assert.Equal(new BigInteger(10_000_000), transfers[0].Amount);
        Assert.Equal(Payer, transfers[0].From);
        Assert.Equal(secondTx, transfers[1].TxId);
        Assert.Equal((uint)second, transfers[1].BlockIndex);
        Assert.Equal(new BigInteger(2_500_000), transfers[1].Amount);
    }

    [Fact]
    public async Task A_token_transfer_by_a_contract_is_recorded_under_its_receipt()
    {
        await using var node = Node();
        node.SetTokenBalance(FakeNearNode.Usdc, Contract, 500_000_000);
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        node.AddTokenTransferByContract(out var receiptId, FakeNearNode.Usdc, Contract, Shop, 42_000_000);

        var transfer = Assert.Single((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), NativeAndUsdc, CancellationToken.None)).Transfers);

        Assert.Equal(receiptId, transfer.TxId);
        Assert.Equal(Contract, transfer.From);
        Assert.Equal(new BigInteger(42_000_000), transfer.Amount);
    }

    [Fact]
    public async Task Several_payments_in_one_span_are_told_apart()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        var h1 = node.AddNativeTransfer(out var tx1, Payer, Shop, OneNear);
        node.AddBlocks(7);
        var h2 = node.AddTokenTransfer(out var tx2, FakeNearNode.Usdc, Payer, Shop, 5_000_000);
        var h3 = node.AddNativeTransfer(out var tx3, Payer, SecondShop, 3 * OneNear);
        node.AddBlocks(20);
        var h4 = node.AddNativeTransfer(out var tx4, Payer, Shop, OneNear / 10);
        node.AddBlocks(1);

        var transfers = (await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop, SecondShop), NativeAndUsdc, CancellationToken.None)).Transfers
            .OrderBy(t => t.BlockIndex).ToArray();

        Assert.Equal([tx1, tx2, tx3, tx4], transfers.Select(t => t.TxId));
        Assert.Equal([(uint)h1, (uint)h2, (uint)h3, (uint)h4], transfers.Select(t => t.BlockIndex));
        Assert.Equal([Shop, Shop, SecondShop, Shop], transfers.Select(t => t.To));
        Assert.Equal(OneNear / 10, transfers[3].Amount);
    }

    [Fact]
    public async Task Skipped_heights_read_as_the_block_before_them()
    {
        await using var node = Node();
        var client = Client(node);
        node.SkipHeight();
        var from = (uint)node.Tip + 1;   // the block before the span is a skipped height
        node.SkipHeight();
        var height = node.AddNativeTransfer(out var txId, Payer, Shop, OneNear);
        node.SkipHeight();
        node.AddBlocks(2);
        var to = (uint)node.SkipHeight();   // and so is the end of the span

        var range = await client.ScanRangeAsync(from, to, Watch(Shop), Native, CancellationToken.None);

        var transfer = Assert.Single(range.Transfers);
        Assert.Equal(txId, transfer.TxId);
        Assert.Equal((uint)height, transfer.BlockIndex);
        Assert.Equal(to, range.To);

        // The next span starts from the cached end balance and sees only what is new.
        node.AddBlocks(2);
        Assert.Empty((await client.ScanRangeAsync(to + 1, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);
        node.AddNativeTransfer(out var later, Payer, Shop, OneNear);
        var next = Assert.Single((await client.ScanRangeAsync((uint)node.Tip, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);
        Assert.Equal(later, next.TxId);
    }

    [Fact]
    public async Task Refunds_and_the_accounts_own_spending_are_not_payments()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        node.AddRefund(Shop, OneNear / 10);
        node.AddSpending(Shop, Payer, OneNear / 5);
        node.AddBlocks(2);
        Assert.Empty((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);

        from = (uint)node.Tip + 1;
        node.AddRefund(Shop, OneNear / 10);
        node.AddNativeTransfer(out var txId, Payer, Shop, OneNear);
        node.AddSpending(Shop, Payer, OneNear / 2);
        var transfer = Assert.Single((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);
        Assert.Equal(txId, transfer.TxId);
        Assert.Equal(OneNear, transfer.Amount);
    }

    [Fact]
    public async Task An_implicit_account_that_does_not_exist_yet_is_created_by_its_first_payment()
    {
        await using var node = Node();
        var client = Client(node);
        var implicitAccount = new string('a', 64);
        var from = (uint)node.Tip + 1;
        node.AddNativeTransfer(out var txId, Payer, implicitAccount, OneNear);

        var transfer = Assert.Single((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(implicitAccount), Native, CancellationToken.None)).Transfers);

        Assert.Equal(txId, transfer.TxId);
        Assert.Equal(OneNear, transfer.Amount);
    }

    [Fact]
    public async Task A_start_the_node_no_longer_has_begins_the_watch_at_the_end_of_the_span()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        node.AddNativeTransfer(out _, Payer, Shop, OneNear);
        node.AddBlocks(2);
        node.OldestBlock = (int)from;

        Assert.Empty((await client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);

        var next = (uint)node.Tip + 1;
        node.AddNativeTransfer(out var txId, Payer, Shop, OneNear);
        var transfer = Assert.Single((await client.ScanRangeAsync(next, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None)).Transfers);
        Assert.Equal(txId, transfer.TxId);
    }

    [Fact]
    public async Task Throttling_surfaces_as_a_rate_limit()
    {
        await using var node = Node();
        var client = Client(node);
        var from = (uint)node.Tip + 1;
        node.AddBlocks(2);
        node.Throttle = true;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.ScanRangeAsync(from, (uint)node.Tip, Watch(Shop), Native, CancellationToken.None));

        Assert.True(ChainListener<NEARPlugin>.IsRateLimit(error), error.ToString());
    }

    [Fact]
    public async Task The_node_must_serve_the_expected_network()
    {
        await using var node = Node();
        var client = Client(node);
        await client.VerifyNetworkAsync(CancellationToken.None);
        Assert.Equal((uint)node.Tip, await client.GetTipAsync(CancellationToken.None));
        node.ChainId = "mainnet";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyNetworkAsync(CancellationToken.None));
        Assert.Contains("mainnet", error.Message);

        var usdc = await client.GetTokenInfoAsync(FakeNearNode.Usdc, CancellationToken.None);
        Assert.Equal(("USDC", 6), (usdc.Symbol, usdc.Decimals));
        Assert.Equal(("NEAR", 24), ((await client.GetTokenInfoAsync(TokenRegistry.Native, CancellationToken.None)).Symbol, 24));
    }

    [Fact]
    public async Task Destinations_are_checked_for_existence_and_token_registration()
    {
        await using var node = Node();
        var client = Client(node);
        var chain = NEARChains.Protocol(mainnet: false);
        var near = chain.PaymentMethods.Single(m => m.Currency == "NEAR");
        var usdc = chain.PaymentMethods.Single(m => m.Currency == "USDC");

        var missing = await client.CheckAsync("ghost.testnet", near, CancellationToken.None);
        Assert.False(missing.Usable);
        Assert.Contains("does not exist", missing.Note);
        Assert.Equal(DestinationCheck.Fine, await client.CheckAsync(Shop, near, CancellationToken.None));
        Assert.Equal(DestinationCheck.Fine, await client.CheckAsync(new string('b', 64), near, CancellationToken.None));

        var unregistered = await client.CheckAsync(Shop, usdc, CancellationToken.None);
        Assert.True(unregistered.Usable);
        Assert.Contains("not registered", unregistered.Note);
        node.Register(FakeNearNode.Usdc, Shop);
        Assert.Equal(DestinationCheck.Fine, await client.CheckAsync(Shop, usdc, CancellationToken.None));

        Assert.Null(await DestinationChecks.FirstProblemAsync(client, [Shop, SecondShop], near, CancellationToken.None));
        Assert.Contains("ghost.testnet", await DestinationChecks.FirstProblemAsync(client, [Shop, "ghost.testnet"], near, CancellationToken.None));
    }

    [Fact]
    public async Task Bisection_finds_every_change_with_few_reads()
    {
        var deltas = new Dictionary<uint, BigInteger> { [10] = 5, [37] = 2, [38] = -1, [90] = 7 };
        var reads = 0;
        Task<BigInteger> BalanceAt(uint height)
        {
            reads++;
            return Task.FromResult(deltas.Where(d => d.Key <= height).Aggregate(new BigInteger(100), (sum, d) => sum + d.Value));
        }

        var changes = await BalanceBisection.FindChangesAsync(1, 100, await BalanceAt(0), await BalanceAt(100), BalanceAt, CancellationToken.None);

        Assert.Equal([(10u, new BigInteger(5)), (37u, new BigInteger(2)), (38u, new BigInteger(-1)), (90u, new BigInteger(7))], changes);
        Assert.InRange(reads, 4, 40);
        Assert.Empty(await BalanceBisection.FindChangesAsync(1, 100, 7, 7, BalanceAt, CancellationToken.None));
    }

    [Fact]
    public void Chunks_are_read_for_transfers_and_token_calls_to_the_watched_accounts()
    {
        var args = Convert.ToBase64String("""{"receiver_id":"shop.testnet","amount":"2500000","memo":"inv"}"""u8.ToArray());
        var elsewhere = Convert.ToBase64String("""{"receiver_id":"other.testnet","amount":"2500000"}"""u8.ToArray());
        using var chunk = JsonDocument.Parse($$"""
            {
              "transactions": [
                { "hash": "T1", "signer_id": "alice.testnet", "receiver_id": "shop.testnet", "actions": [ { "Transfer": { "deposit": "1000" } } ] },
                { "hash": "T2", "signer_id": "relayer.testnet", "receiver_id": "bob.testnet", "actions": [ { "Delegate": { "delegate_action": { "sender_id": "bob.testnet", "receiver_id": "shop.testnet", "actions": [ { "Transfer": { "deposit": "2000" } } ] } } } ] },
                { "hash": "T3", "signer_id": "carol.testnet", "receiver_id": "{{FakeNearNode.Usdc}}", "actions": [ { "FunctionCall": { "method_name": "ft_transfer", "args": "{{args}}", "deposit": "1", "gas": 1 } } ] },
                { "hash": "T4", "signer_id": "carol.testnet", "receiver_id": "{{FakeNearNode.Usdc}}", "actions": [ { "FunctionCall": { "method_name": "ft_transfer", "args": "{{elsewhere}}", "deposit": "1", "gas": 1 } } ] },
                { "hash": "T5", "signer_id": "dave.testnet", "receiver_id": "game.testnet", "actions": [ { "FunctionCall": { "method_name": "l2_claim", "args": "e30=", "deposit": "0", "gas": 1 } } ] },
                { "hash": "T6", "signer_id": "shop.testnet", "receiver_id": "shop.testnet", "actions": [ { "Transfer": { "deposit": "5" } } ] }
              ],
              "receipts": [
                { "receipt_id": "R1", "predecessor_id": "system", "receiver_id": "shop.testnet", "receipt": { "Action": { "actions": [ { "Transfer": { "deposit": "77" } } ] } } },
                { "receipt_id": "R2", "predecessor_id": "intents.testnet", "receiver_id": "shop.testnet", "receipt": { "Action": { "actions": [ { "Transfer": { "deposit": "3000" } } ] } } },
                { "receipt_id": "R3", "predecessor_id": "intents.testnet", "receiver_id": "{{FakeNearNode.Usdc}}", "receipt": { "Action": { "actions": [ { "FunctionCall": { "method_name": "ft_transfer", "args": "{{args}}", "deposit": "1", "gas": 1 } } ] } } }
              ]
            }
            """);

        var found = NearTransfers.FromChunk(chunk.RootElement, Watch("shop.testnet"), NativeAndUsdc);

        Assert.Equal(["T1", "T2", "T3", "R2", "R3"], found.Select(c => c.Id));
        Assert.Equal(["alice.testnet", "bob.testnet", "carol.testnet", "intents.testnet", "intents.testnet"], found.Select(c => c.From));
        Assert.Equal([TokenRegistry.Native, TokenRegistry.Native, FakeNearNode.Usdc, TokenRegistry.Native, FakeNearNode.Usdc], found.Select(c => c.Token));
        Assert.Equal(new BigInteger(2500000), found[2].Amount);
        Assert.Equal("inv", found[2].Memo);
        Assert.Equal([true, true, true, false, false], found.Select(c => c.IsTransaction));

        Assert.False(NearTransfers.TryReadFungibleArguments("not base64!", out _, out _, out _));
        Assert.False(NearTransfers.TryReadFungibleArguments(Convert.ToBase64String("""{"receiver_id":"x","amount":"-5"}"""u8.ToArray()), out _, out _, out _));
    }
}
