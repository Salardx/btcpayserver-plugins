using BTCPayServer.Plugins.BNBChain;
using Microsoft.Extensions.Configuration;
using NBitcoin;

namespace BTCPayServer.Plugins.Tests;

public class BNBChainTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    [Fact]
    public void Mainnet_offers_bnb_usdt_and_usdc_on_chain_56()
    {
        var definition = BNBChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("BNBChain", definition.Id);
        Assert.Equal(BNBChainPlugin.PluginNavKey, definition.NavKey);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("BSC", chain.Id);
        Assert.True(chain.IsEvm);
        Assert.Equal(56, chain.ChainId);
        Assert.Equal(6, chain.FinalityBlocks);
        Assert.Equal(["BNB-BSC", "USDT-BSC", "USDC-BSC"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));
        Assert.Equal(BNBChains.MainnetUsdt, chain.PaymentMethods.Single(m => m.Currency == "USDT").TokenHash);
        Assert.All(chain.PaymentMethods, m => Assert.Equal(18, m.Decimals));
        Assert.Equal("https://bscscan.com/tx/0xabc", string.Format(chain.BlockExplorerLink, "0xabc"));
        Assert.Contains("BNB_BTC = binance(BNB_BTC)", chain.PaymentMethods.Single(m => m.Currency == "BNB").RateRules);
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);
    }

    [Fact]
    public void Testnet_offers_only_bnb_unless_token_contracts_are_configured()
    {
        var bare = Assert.Single(BNBChains.Definition(ChainName.Testnet, Empty).Chains);
        Assert.Equal(97, bare.ChainId);
        Assert.Equal(["BNB-BSC"], bare.PaymentMethods.Select(m => bare.PaymentMethodId(m).ToString()));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BSC_USDT_CONTRACT"] = "0x337610d27c682E347C9cD60BD4b3b107C9d34dDd",
                ["BSC_USDC_CONTRACT"] = "0x64544969ed7EBf5f083679233325356EbE738930",
            })
            .Build();
        var configured = Assert.Single(BNBChains.Definition(ChainName.Regtest, configuration).Chains);
        Assert.Equal(["BNB-BSC", "USDT-BSC", "USDC-BSC"], configured.PaymentMethods.Select(m => configured.PaymentMethodId(m).ToString()));
        Assert.Equal("0x337610d27c682e347c9cd60bd4b3b107c9d34ddd", configured.PaymentMethods.Single(m => m.Currency == "USDT").TokenHash);
        Assert.Contains("(testnet)", configured.PaymentMethods[0].DisplayName);
    }
}
