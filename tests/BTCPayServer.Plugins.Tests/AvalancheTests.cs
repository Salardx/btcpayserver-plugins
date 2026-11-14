using BTCPayServer.Plugins.Avalanche;
using BTCPayServer.Plugins.Chains.Payments;
using Microsoft.Extensions.Configuration;
using NBitcoin;

namespace BTCPayServer.Plugins.Tests;

public class AvalancheTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    const string Shop = "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed";

    [Fact]
    public void Mainnet_offers_avax_usdc_and_usdt_on_chain_43114()
    {
        var definition = AvalancheChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("Avalanche", definition.Id);
        Assert.Equal(AvalanchePlugin.PluginNavKey, definition.NavKey);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("AVAXC", chain.Id);
        Assert.True(chain.IsEvm);
        Assert.Equal(43114, chain.ChainId);
        Assert.Equal(2, chain.FinalityBlocks);
        Assert.Null(chain.NativeTransferLog);
        Assert.Equal(["AVAX-AVAXC", "USDC-AVAXC", "USDT-AVAXC"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));
        Assert.Equal([18, 6, 6], chain.PaymentMethods.Select(m => m.Decimals));
        Assert.Equal(AvalancheChains.MainnetUsdc, chain.PaymentMethods.Single(m => m.Currency == "USDC").TokenHash);
        Assert.Equal(AvalancheChains.MainnetUsdt, chain.PaymentMethods.Single(m => m.Currency == "USDT").TokenHash);
        Assert.Equal("https://snowtrace.io/tx/0xabc", string.Format(chain.BlockExplorerLink, "0xabc"));
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);
    }

    [Fact]
    public void Fuji_offers_avax_and_circles_test_usdc_and_usdt_only_when_a_contract_is_configured()
    {
        var bare = Assert.Single(AvalancheChains.Definition(ChainName.Testnet, Empty).Chains);
        Assert.Equal(43113, bare.ChainId);
        Assert.Equal(["AVAX-AVAXC", "USDC-AVAXC"], bare.PaymentMethods.Select(m => bare.PaymentMethodId(m).ToString()));
        Assert.Equal(AvalancheChains.FujiUsdc, bare.PaymentMethods.Single(m => m.Currency == "USDC").TokenHash);
        Assert.All(bare.PaymentMethods, m => Assert.Contains("(Fuji testnet)", m.DisplayName));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AVAXC_USDT_CONTRACT"] = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
                ["AVAXC_JSONRPC_URI"] = "https://node.example/ext/bc/C/rpc",
            })
            .Build();
        var configured = Assert.Single(AvalancheChains.Definition(ChainName.Regtest, configuration).Chains);
        Assert.Equal(["AVAX-AVAXC", "USDC-AVAXC", "USDT-AVAXC"], configured.PaymentMethods.Select(m => configured.PaymentMethodId(m).ToString()));
        Assert.Equal(Shop, configured.PaymentMethods.Single(m => m.Currency == "USDT").TokenHash);
        Assert.Equal(["https://node.example/ext/bc/C/rpc"], configured.JsonRpcUris);
    }

    [Fact]
    public void Payment_links_are_eip681_on_chain_43114()
    {
        var chain = AvalancheChains.CChain(mainnet: true);

        Assert.Equal($"ethereum:{Shop}@43114?value=1500000000000000000",
            ChainPaymentLinkExtension.Build(chain, chain.PaymentMethods.Single(m => m.Currency == "AVAX"), Shop, 1.5m, PaymentLinkFormat.Standard));
        Assert.Equal($"ethereum:{AvalancheChains.MainnetUsdc}@43114/transfer?address={Shop}&uint256=25000000",
            ChainPaymentLinkExtension.Build(chain, chain.PaymentMethods.Single(m => m.Currency == "USDC"), Shop, 25m, PaymentLinkFormat.Standard));
    }

    [Fact]
    public void Avax_is_priced_through_its_dollar_rate()
    {
        Assert.Equal(8m, RateRuleCheck.Evaluate(AvalancheChains.AvaxRateRules, "AVAX_USD", ("coinbasepro", "AVAX_USD", 8m)));
        Assert.Equal(7.2m, RateRuleCheck.Evaluate(AvalancheChains.AvaxRateRules, "AVAX_EUR", ("coinbasepro", "AVAX_USD", 8m), ("kraken", "USD_EUR", 0.9m)));
        Assert.Equal(0.999m, RateRuleCheck.Evaluate(AvalancheChains.UsdtRateRules, "USDT_USD", ("kraken", "USDT_USD", 0.999m)));
    }
}
