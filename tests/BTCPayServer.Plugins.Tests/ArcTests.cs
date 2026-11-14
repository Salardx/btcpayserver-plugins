using System.Numerics;
using BTCPayServer.Plugins.Arc;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Evm;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Tests.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Tests;

public class ArcTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    const string Shop = "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed";
    const string Payer = "0xfb6916095ca1df60bb79ce92ce3ea74c37c5d359";
    const string Stranger = "0xdbf03b407c01e7cd3cbea99509d93f8dddc8c6fb";

    /// <summary>USDC's ERC-20 interface on Arc: its logs repeat, in 6 decimals, what the coin's own log already said.</summary>
    const string UsdcInterface = "0x3600000000000000000000000000000000000000";

    static BigInteger Coin(string amount)
    {
        Assert.True(TokenAmount.TryParse(amount, 18, out var units));
        return units;
    }

    static EvmLogChainClient Client(ChainDefinition chain) => new(chain.ToChainProfile(), NullLogger<EvmLogChainClient>.Instance);

    [Fact]
    public void Mainnet_offers_usdc_as_the_coin_and_eurc_as_a_token_on_chain_5042()
    {
        var definition = ArcChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("Arc", definition.Id);
        Assert.Equal(ArcPlugin.PluginNavKey, definition.NavKey);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("ARC", chain.Id);
        Assert.True(chain.IsEvm);
        Assert.Equal(5042, chain.ChainId);
        Assert.Equal(1, chain.FinalityBlocks);
        Assert.Equal(ArcChains.UsdcTransferLog, chain.NativeTransferLog);
        Assert.Equal(ArcChains.UsdcTransferLog, chain.ToChainProfile().NativeTransferLog);
        Assert.Equal(["USDC-ARC", "EURC-ARC"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));

        var usdc = chain.PaymentMethods.Single(m => m.Currency == "USDC");
        Assert.True(usdc.IsNative);
        Assert.Equal((18, 6), (usdc.Decimals, usdc.Divisibility));
        var eurc = chain.PaymentMethods.Single(m => m.Currency == "EURC");
        Assert.Equal((ArcChains.MainnetEurc, 6), (eurc.TokenHash, eurc.Decimals));
        Assert.Equal("https://explorer.arc.io/tx/0xabc", string.Format(chain.BlockExplorerLink, "0xabc"));
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);
    }

    [Fact]
    public void Testnet_is_chain_5042002_with_circles_test_eurc()
    {
        var chain = Assert.Single(ArcChains.Definition(ChainName.Regtest, Empty).Chains);

        Assert.Equal(5042002, chain.ChainId);
        Assert.Equal(ArcChains.TestnetEurc, chain.PaymentMethods.Single(m => m.Currency == "EURC").TokenHash);
        Assert.All(chain.PaymentMethods, m => Assert.Contains("(testnet)", m.DisplayName));
        Assert.All(chain.JsonRpcUris, uri => Assert.Contains("testnet", uri));
    }

    [Fact]
    public void The_usdc_link_asks_for_a_plain_transfer_of_the_coin_and_the_eurc_link_for_a_token_transfer()
    {
        var chain = ArcChains.Network(mainnet: true);
        var usdc = chain.PaymentMethods.Single(m => m.Currency == "USDC");
        var eurc = chain.PaymentMethods.Single(m => m.Currency == "EURC");

        Assert.Equal($"ethereum:{Shop}@5042?value=12500000000000000000",
            ChainPaymentLinkExtension.Build(chain, usdc, Shop, 12.5m, PaymentLinkFormat.Standard));
        Assert.Equal($"ethereum:{ArcChains.MainnetEurc}@5042/transfer?address={Shop}&uint256=12500000",
            ChainPaymentLinkExtension.Build(chain, eurc, Shop, 12.5m, PaymentLinkFormat.Standard));
    }

    [Fact]
    public void Eurc_is_priced_at_par_with_the_euro_and_usdc_at_its_market_rate()
    {
        Assert.Equal(1m, RateRuleCheck.Evaluate(ArcChains.EurcRateRules, "EURC_EUR"));
        Assert.Equal(1.25m, Math.Round(RateRuleCheck.Evaluate(ArcChains.EurcRateRules, "EURC_USD", ("kraken", "BTC_EUR", 60000m), ("kraken", "BTC_USD", 75000m)), 12));
        Assert.Equal(0.999m, RateRuleCheck.Evaluate(ArcChains.UsdcRateRules, "USDC_USD", ("kraken", "USDC_USD", 0.999m)));
        Assert.Equal(0.8991m, RateRuleCheck.Evaluate(ArcChains.UsdcRateRules, "USDC_EUR", ("kraken", "USDC_USD", 0.999m), ("kraken", "USD_EUR", 0.9m)));
    }

    [Fact]
    public async Task A_span_of_blocks_is_read_from_the_transfer_logs_in_one_request()
    {
        await using var node = new FakeEvmNode(chainId: 5042002) { AnswersLogRanges = true };
        var chain = ArcChains.Network(mainnet: false) with { JsonRpcUris = [node.Url] };
        var client = Client(chain);
        var first = node.Tip + 1;

        // A plain transfer of the coin: the value in the transaction, and the coin's log.
        var plain = node.AddLogs(out var plainTx, Payer, Shop, Coin("2.5"), (ArcChains.UsdcTransferLog, Payer, Shop, Coin("2.5")));
        node.AddBlock();
        // A call of the ERC-20 interface: the coin's log in 18 decimals, then the interface's own in 6.
        var call = node.AddLogs(out var callTx, Payer, UsdcInterface, BigInteger.Zero,
            (ArcChains.UsdcTransferLog, Payer, Shop, Coin("4")), (UsdcInterface, Payer, Shop, 4_000_000));
        // A smart account paying: no value and no ERC-20 call in the transaction, only the coin's log.
        var account = node.AddLogs(out var accountTx, Payer, Stranger, BigInteger.Zero, (ArcChains.UsdcTransferLog, Stranger, Shop, Coin("0.75")));
        var token = node.AddLogs(out var tokenTx, Payer, ArcChains.TestnetEurc, BigInteger.Zero, (ArcChains.TestnetEurc, Payer, Shop, 7_500_000));
        node.AddLogs(out _, Payer, Stranger, Coin("9"), (ArcChains.UsdcTransferLog, Payer, Stranger, Coin("9")));
        var last = node.AddBlock();

        var before = node.Requests;
        var range = await client.ScanRangeAsync((uint)first, (uint)last, new HashSet<string> { Shop }, chain.TokenHashes(), CancellationToken.None);

        Assert.Equal((uint)last, range.To);
        Assert.Equal(
            [
                (plainTx, 0, (uint)plain, TokenRegistry.Native, Payer, Coin("2.5")),
                (callTx, 0, (uint)call, TokenRegistry.Native, Payer, Coin("4")),
                (accountTx, 0, (uint)account, TokenRegistry.Native, Stranger, Coin("0.75")),
                (tokenTx, 0, (uint)token, ArcChains.TestnetEurc, Payer, new BigInteger(7_500_000)),
            ],
            range.Transfers.Select(t => (t.TxId, t.NotifyIndex, t.BlockIndex, t.TokenHash, t.From, t.Amount)));
        Assert.All(range.Transfers, t => Assert.Equal(Shop, t.To));
        Assert.All(range.Transfers, t => Assert.True(t.BlockTime > 0));
        // One request for the span, then the time of each of the four blocks that held a payment.
        Assert.Equal(1 + 4, node.Requests - before);
    }

    [Fact]
    public async Task A_span_past_the_nodes_tip_is_refused_instead_of_being_read_short()
    {
        await using var node = new FakeEvmNode(chainId: 5042002) { AnswersLogRanges = true };
        var chain = ArcChains.Network(mainnet: false) with { JsonRpcUris = [node.Url] };
        var first = node.Tip + 1;
        node.AddLogs(out _, Payer, Shop, Coin("1"), (ArcChains.UsdcTransferLog, Payer, Shop, Coin("1")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(chain).ScanRangeAsync((uint)first, (uint)node.Tip + 3, new HashSet<string> { Shop }, chain.TokenHashes(), CancellationToken.None));
    }

    [Fact]
    public async Task A_span_with_nothing_to_watch_costs_one_call_and_reports_the_time_of_its_last_block()
    {
        await using var node = new FakeEvmNode(chainId: 5042002) { AnswersLogRanges = true };
        var chain = ArcChains.Network(mainnet: false) with { JsonRpcUris = [node.Url] };
        var before = node.Requests;

        var range = await Client(chain).ScanRangeAsync((uint)node.Tip - 5, (uint)node.Tip, new HashSet<string>(), chain.TokenHashes(), CancellationToken.None);

        Assert.Empty(range.Transfers);
        Assert.True(range.ToTime > 0);
        Assert.Equal(1, node.Requests - before);
    }

    [Fact]
    public async Task A_single_block_is_read_from_the_logs_too_so_the_coin_is_counted_once()
    {
        await using var node = new FakeEvmNode(chainId: 5042002) { AnswersLogRanges = true };
        var chain = ArcChains.Network(mainnet: false) with { JsonRpcUris = [node.Url] };
        var index = node.AddLogs(out var txId, Payer, Shop, Coin("2.5"), (ArcChains.UsdcTransferLog, Payer, Shop, Coin("2.5")));

        var block = await Client(chain).ScanBlockAsync((uint)index, new HashSet<string> { Shop }, chain.TokenHashes(), CancellationToken.None);

        var transfer = Assert.Single(block.Transfers);
        Assert.Equal((txId, 0, TokenRegistry.Native, Coin("2.5")), (transfer.TxId, transfer.NotifyIndex, transfer.TokenHash, transfer.Amount));
    }

    [Fact]
    public async Task A_log_that_a_node_left_out_is_found_by_the_next_turn()
    {
        await using var node = new FakeEvmNode(chainId: 5042002) { AnswersLogRanges = true };
        var chain = ArcChains.Network(mainnet: false) with { JsonRpcUris = [node.Url] };
        var client = Client(chain);
        var watched = new HashSet<string> { Shop };
        var first = node.Tip + 1;
        var paid = node.AddLogs(out var txId, Payer, Shop, Coin("3"), (ArcChains.UsdcTransferLog, Payer, Shop, Coin("3")));

        // The node that answers is one block behind: it returns the span without the newest block's log, and no error.
        node.HidesNewestLogs = true;
        Assert.Empty((await client.ScanRangeAsync((uint)first, (uint)paid, watched, chain.TokenHashes(), CancellationToken.None)).Transfers);

        // The next turn starts after that block and still finds the payment in it, because it reads the last blocks again.
        node.HidesNewestLogs = false;
        node.AddBlocks(2);
        var next = await client.ScanRangeAsync((uint)paid + 1, (uint)node.Tip, watched, chain.TokenHashes(), CancellationToken.None);
        var transfer = Assert.Single(next.Transfers);
        Assert.Equal((txId, (uint)paid, Coin("3")), (transfer.TxId, transfer.BlockIndex, transfer.Amount));

        // Reading it once more costs one request: the block's time is remembered. One block alone is read without the look back.
        node.AddBlock();
        var before = node.Requests;
        Assert.Equal(transfer, Assert.Single((await client.ScanRangeAsync((uint)node.Tip, (uint)node.Tip, watched, chain.TokenHashes(), CancellationToken.None)).Transfers));
        Assert.Equal(1, node.Requests - before);
        Assert.Empty((await client.ScanBlockAsync((uint)node.Tip, watched, chain.TokenHashes(), CancellationToken.None)).Transfers);
    }

    [Fact]
    public async Task The_node_must_be_on_the_configured_arc_network()
    {
        await using var node = new FakeEvmNode(chainId: 5042);
        var chain = ArcChains.Network(mainnet: false) with { JsonRpcUris = [node.Url] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(chain).VerifyNetworkAsync(CancellationToken.None));
    }
}
