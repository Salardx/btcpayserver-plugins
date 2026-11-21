using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.IoTeX;
using Microsoft.Extensions.Configuration;
using NBitcoin;

namespace BTCPayServer.Plugins.Tests;

public class IoTeXTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    const string Shop = "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed";

    [Fact]
    public void Mainnet_offers_iotx_and_iousdt_on_chain_4689()
    {
        var definition = IoTeXChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("IoTeX", definition.Id);
        Assert.Equal(IoTeXPlugin.PluginNavKey, definition.NavKey);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("IOTEX", chain.Id);
        Assert.True(chain.IsEvm);
        Assert.Equal(4689, chain.ChainId);
        Assert.Equal(2.5, chain.BlockTimeSeconds);
        Assert.Equal(2, chain.FinalityBlocks);
        Assert.Null(chain.NativeTransferLog);
        Assert.Equal(["IOTX-IOTEX", "USDT-IOTEX"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));
        Assert.Equal([18, 6], chain.PaymentMethods.Select(m => m.Decimals));
        Assert.Equal(IoTeXChains.MainnetUsdt, chain.PaymentMethods.Single(m => m.Currency == "USDT").TokenHash);
        Assert.Equal("ioUSDT on IoTeX", chain.PaymentMethods.Single(m => m.Currency == "USDT").DisplayName);
        Assert.Equal("https://iotexscan.io/tx/0xabc", string.Format(chain.BlockExplorerLink, "0xabc"));
        Assert.Equal("https://babel-api.mainnet.iotex.io", chain.JsonRpcUris[0]);
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);
    }

    [Fact]
    public void Testnet_offers_iotx_and_iousdt_only_when_a_contract_is_configured()
    {
        var bare = Assert.Single(IoTeXChains.Definition(ChainName.Testnet, Empty).Chains);
        Assert.Equal(4690, bare.ChainId);
        Assert.Equal(["IOTX-IOTEX"], bare.PaymentMethods.Select(m => bare.PaymentMethodId(m).ToString()));
        Assert.All(bare.PaymentMethods, m => Assert.Contains("(testnet)", m.DisplayName));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IOTEX_USDT_CONTRACT"] = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
                ["IOTEX_JSONRPC_URI"] = "https://node.example/iotex",
            })
            .Build();
        var configured = Assert.Single(IoTeXChains.Definition(ChainName.Regtest, configuration).Chains);
        Assert.Equal(["IOTX-IOTEX", "USDT-IOTEX"], configured.PaymentMethods.Select(m => configured.PaymentMethodId(m).ToString()));
        Assert.Equal(Shop, configured.PaymentMethods.Single(m => m.Currency == "USDT").TokenHash);
        Assert.Equal(["https://node.example/iotex"], configured.JsonRpcUris);
    }

    [Fact]
    public void Payment_links_are_eip681_on_chain_4689()
    {
        var chain = IoTeXChains.Chain(mainnet: true);

        Assert.Equal($"ethereum:{Shop}@4689?value=1500000000000000000",
            ChainPaymentLinkExtension.Build(chain, chain.PaymentMethods.Single(m => m.Currency == "IOTX"), Shop, 1.5m, PaymentLinkFormat.Standard));
        Assert.Equal($"ethereum:{IoTeXChains.MainnetUsdt}@4689/transfer?address={Shop}&uint256=25000000",
            ChainPaymentLinkExtension.Build(chain, chain.PaymentMethods.Single(m => m.Currency == "USDT"), Shop, 25m, PaymentLinkFormat.Standard));
    }

    [Fact]
    public void Iotx_is_priced_through_binances_usdt_pair_and_krakens_usdt_rate()
    {
        Assert.Equal(0.0024m, RateRuleCheck.Evaluate(IoTeXChains.IotxRateRules, "IOTX_USD", ("binance", "IOTX_USDT", 0.0024m), ("kraken", "USDT_USD", 1m)));
        Assert.Equal(0.002376m, RateRuleCheck.Evaluate(IoTeXChains.IotxRateRules, "IOTX_USD", ("binance", "IOTX_USDT", 0.0024m), ("kraken", "USDT_USD", 0.99m)));
        Assert.Equal(0.00216m, RateRuleCheck.Evaluate(IoTeXChains.IotxRateRules, "IOTX_EUR",
            ("binance", "IOTX_USDT", 0.0024m), ("kraken", "USDT_USD", 1m), ("kraken", "USD_EUR", 0.9m)));
        Assert.Equal(0.999m, RateRuleCheck.Evaluate(IoTeXChains.UsdtRateRules, "USDT_USD", ("kraken", "USDT_USD", 0.999m)));
    }
}
