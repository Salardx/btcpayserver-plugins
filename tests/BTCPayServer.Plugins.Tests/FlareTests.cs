using System.Globalization;
using System.Numerics;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Flare;
using BTCPayServer.Plugins.Tests.Host;
using BTCPayServer.Rating;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.Tests;

public class FlareTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    /// <summary>The FtsoV2 contract on Flare mainnet, as the registry named it in November 2026.</summary>
    const string Ftso = "0x7bde3df0624114edb3a67dfe6753e62f4e7c1d20";

    /// <summary>What <c>getFeedById</c> returns: value, decimals as a signed byte widened to a word, time of the update.</summary>
    static string Feed(BigInteger value, int decimals, long updated) =>
        "0x" + value.ToString("x64", CultureInfo.InvariantCulture)[^64..]
             + (decimals >= 0 ? decimals.ToString("x64", CultureInfo.InvariantCulture) : new string('f', 62) + ((byte)(sbyte)decimals).ToString("x2", CultureInfo.InvariantCulture))
             + updated.ToString("x64", CultureInfo.InvariantCulture);

    [Fact]
    public void Mainnet_offers_flr_usdt0_and_fxrp_on_chain_14()
    {
        var definition = FlareChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("Flare", definition.Id);
        Assert.Equal(FlarePlugin.PluginNavKey, definition.NavKey);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("FLARE", chain.Id);
        Assert.True(chain.IsEvm);
        Assert.Equal(14, chain.ChainId);
        Assert.Equal(2, chain.FinalityBlocks);
        Assert.Equal(["FLR-FLARE", "USDT0-FLARE", "FXRP-FLARE"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));
        Assert.Equal([18, 6, 6], chain.PaymentMethods.Select(m => m.Decimals));
        Assert.Equal(FlareChains.MainnetUsdt0, chain.PaymentMethods.Single(m => m.Currency == "USDT0").TokenHash);
        Assert.Equal(FlareChains.MainnetFxrp, chain.PaymentMethods.Single(m => m.Currency == "FXRP").TokenHash);
        Assert.Equal("USD₮0 on Flare", chain.PaymentMethods.Single(m => m.Currency == "USDT0").DisplayName);
        Assert.Equal("https://flare-explorer.flare.network/tx/0xabc", string.Format(chain.BlockExplorerLink, "0xabc"));
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);
    }

    [Fact]
    public void Coston2_offers_flr_and_the_test_fxrp_and_usdt0_only_when_a_contract_is_configured()
    {
        var bare = Assert.Single(FlareChains.Definition(ChainName.Testnet, Empty).Chains);
        Assert.Equal(114, bare.ChainId);
        Assert.Equal(["FLR-FLARE", "FXRP-FLARE"], bare.PaymentMethods.Select(m => bare.PaymentMethodId(m).ToString()));
        Assert.Equal(FlareChains.Coston2Fxrp, bare.PaymentMethods.Single(m => m.Currency == "FXRP").TokenHash);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLARE_USDT0_CONTRACT"] = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed" })
            .Build();
        var configured = Assert.Single(FlareChains.Definition(ChainName.Regtest, configuration).Chains);
        Assert.Equal(["FLR-FLARE", "USDT0-FLARE", "FXRP-FLARE"], configured.PaymentMethods.Select(m => configured.PaymentMethodId(m).ToString()));
    }

    [Fact]
    public void Every_method_is_priced_by_the_ftso_and_fxrp_takes_the_xrp_feed()
    {
        Assert.Equal(0.0049m, RateRuleCheck.Evaluate(FlareChains.FlrRateRules, "FLR_USD", ("flareftso", "FLR_USD", 0.0049m)));
        Assert.Equal(0.00441m, RateRuleCheck.Evaluate(FlareChains.FlrRateRules, "FLR_EUR", ("flareftso", "FLR_USD", 0.0049m), ("kraken", "USD_EUR", 0.9m)));
        Assert.Equal(0.99m, RateRuleCheck.Evaluate(FlareChains.FxrpRateRules, "FXRP_USD", ("flareftso", "XRP_USD", 0.99m)));
        Assert.Equal(0.891m, RateRuleCheck.Evaluate(FlareChains.FxrpRateRules, "FXRP_EUR", ("flareftso", "XRP_USD", 0.99m), ("kraken", "USD_EUR", 0.9m)));
        Assert.Equal(0.9989m, RateRuleCheck.Evaluate(FlareChains.Usdt0RateRules, "USDT0_USD", ("flareftso", "USDT_USD", 0.9989m)));
    }

    [Fact]
    public void Ftso_calls_are_encoded_as_the_contracts_expect()
    {
        // Selectors as computed independently with another Keccak implementation.
        Assert.Equal("0x93e9f806", FtsoRateProvider.Selector("getFeedById(bytes21)"));
        Assert.Equal("0x82760fca", FtsoRateProvider.Selector("getContractAddressByName(string)"));

        // Category 1 (crypto), then "FLR/USD" in ASCII, padded to 21 bytes.
        Assert.Equal("01464c522f555344" + new string('0', 26), FtsoRateProvider.FeedId("FLR"));
        Assert.Equal("0x93e9f806" + "01464c522f555344" + new string('0', 48), FtsoRateProvider.GetFeedByIdCall("FLR"));
        Assert.Equal(
            "0x82760fca"
            + "0000000000000000000000000000000000000000000000000000000000000020"
            + "0000000000000000000000000000000000000000000000000000000000000006"
            + "4674736f56320000000000000000000000000000000000000000000000000000",
            FtsoRateProvider.ContractAddressByNameCall("FtsoV2"));
    }

    [Fact]
    public void A_feed_answer_becomes_a_decimal_rate_and_a_stale_or_broken_one_becomes_nothing()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_794_000_000);
        var fresh = now.ToUnixTimeSeconds() - 2;

        Assert.Equal(0.00489089m, FtsoRateProvider.DecodeFeed(Feed(489089, 8, fresh), now));
        Assert.Equal(67223.28m, FtsoRateProvider.DecodeFeed(Feed(6722328, 2, fresh), now));
        Assert.Equal(1200m, FtsoRateProvider.DecodeFeed(Feed(12, -2, fresh), now));

        Assert.Null(FtsoRateProvider.DecodeFeed(Feed(489089, 8, now.ToUnixTimeSeconds() - 3600), now));   // the feed stopped an hour ago
        Assert.Null(FtsoRateProvider.DecodeFeed(Feed(489089, 8, now.ToUnixTimeSeconds() + 3600), now));   // a time in the future
        Assert.Null(FtsoRateProvider.DecodeFeed(Feed(0, 8, fresh), now));
        Assert.Null(FtsoRateProvider.DecodeFeed("0x", now));
        Assert.Null(FtsoRateProvider.DecodeFeed(null, now));
    }

    [Fact]
    public void The_registry_answer_is_an_address_in_one_word()
    {
        Assert.Equal(Ftso, FtsoRateProvider.DecodeAddress("0x" + new string('0', 24) + Ftso[2..]));
        Assert.Null(FtsoRateProvider.DecodeAddress("0x" + new string('0', 64)));
        Assert.Null(FtsoRateProvider.DecodeAddress("0x1234"));
    }

    [Fact]
    public async Task The_ftso_source_reads_all_feeds_in_one_request_and_leaves_out_a_stale_or_refused_one()
    {
        await using var node = new FakeEvmNode(chainId: 114);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var feeds = new Dictionary<string, string>
        {
            [FtsoRateProvider.GetFeedByIdCall("FLR")] = Feed(489089, 8, now - 2),
            [FtsoRateProvider.GetFeedByIdCall("XRP")] = Feed(989336, 6, now - 2),
            [FtsoRateProvider.GetFeedByIdCall("BTC")] = Feed(6722328, 2, now - 2),
            [FtsoRateProvider.GetFeedByIdCall("ETH")] = Feed(1907463, 3, now - 3600),
            [FtsoRateProvider.GetFeedByIdCall("USDT")] = Feed(99887, 5, now - 2),
            // no USDC feed: the contract reverts for it, and the other prices must stand
        };
        node.Call = (to, data) => to == FtsoRateProvider.ContractRegistry && data == FtsoRateProvider.ContractAddressByNameCall("FtsoV2")
            ? "0x" + new string('0', 24) + Ftso[2..]
            : to == Ftso && feeds.TryGetValue(data, out var answer) ? answer : null;
        var definition = FlareChains.Definition(ChainName.Testnet, Empty);
        var context = new ChainPluginContext<FlarePlugin>(definition with { Chains = [definition.Chains[0] with { JsonRpcUris = [node.Url] }] });
        var provider = new FtsoRateProvider(context, NullLogger<FtsoRateProvider>.Instance);

        var rates = await provider.GetRatesAsync(CancellationToken.None);

        Assert.Equal("flareftso", provider.RateSourceInfo.Id);
        Assert.Equal(
            [("FLR_USD", 0.00489089m), ("XRP_USD", 0.989336m), ("BTC_USD", 67223.28m), ("USDT_USD", 0.99887m)],
            rates.Select(r => (r.CurrencyPair.ToString(), r.BidAsk.Bid)));
        Assert.Equal(2, node.Requests);   // the registry once, then all six feeds in one request

        await provider.GetRatesAsync(CancellationToken.None);
        Assert.Equal(3, node.Requests);   // the FtsoV2 address is remembered
    }

    [Fact]
    public async Task A_node_that_cannot_answer_fails_the_ftso_source_instead_of_returning_old_rates()
    {
        await using var node = new FakeEvmNode(chainId: 114);   // no contract answers: every call reverts
        var definition = FlareChains.Definition(ChainName.Testnet, Empty);
        var context = new ChainPluginContext<FlarePlugin>(definition with { Chains = [definition.Chains[0] with { JsonRpcUris = [node.Url] }] });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FtsoRateProvider(context, NullLogger<FtsoRateProvider>.Instance).GetRatesAsync(CancellationToken.None));
    }
}
