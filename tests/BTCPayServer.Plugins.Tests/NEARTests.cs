using System.Numerics;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Near;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.NEAR;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Tests;

public class NEARTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();
    static readonly ChainDefinition Mainnet = NEARChains.Protocol(mainnet: true);

    static PaymentMethodDefinition Method(ChainDefinition chain, string currency) => chain.PaymentMethods.Single(m => m.Currency == currency);

    [Fact]
    public void Mainnet_offers_near_usdc_and_usdt_and_sends_customers_to_the_pay_page()
    {
        var definition = NEARChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("NEAR", definition.Id);
        Assert.Equal(NEARPlugin.PluginNavKey, definition.NavKey);
        Assert.Equal(new PayPageRoute("UINEARPay", "Pay"), definition.PayPage);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("NEAR", chain.Id);
        Assert.True(chain.IsNear);
        Assert.False(chain.IsEvm);
        Assert.Equal("shop.near", chain.AddressPlaceholder);
        Assert.Equal(["NEAR-NEAR", "USDC-NEAR", "USDT-NEAR"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));
        Assert.Equal((24, 8), (Method(chain, "NEAR").Decimals, Method(chain, "NEAR").Divisibility));
        Assert.Equal((NEARChains.MainnetUsdc, 6), (Method(chain, "USDC").TokenHash, Method(chain, "USDC").Decimals));
        Assert.Equal(NEARChains.MainnetUsdt, Method(chain, "USDT").TokenHash);
        Assert.Equal(0, chain.FinalityBlocks);
        Assert.Equal(0.6, chain.BlockTimeSeconds);
        Assert.Equal(3, chain.JsonRpcUris.Length);
        Assert.Equal("https://nearblocks.io/txns/abc", string.Format(chain.BlockExplorerLink, "abc"));
        Assert.Contains("NEAR_BTC = binance(NEAR_BTC)", Method(chain, "NEAR").RateRules);
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);

        var profile = chain.ToChainProfile();
        Assert.Equal(ChainProfile.Near, profile.Kind);
        Assert.True(profile.IsNear);
        Assert.Equal("near:mainnet", profile.NetworkKey);
        Assert.Equal("mainnet", profile.Network);
    }

    [Fact]
    public void Testnet_has_circles_usdc_and_takes_usdt_from_configuration()
    {
        var bare = Assert.Single(NEARChains.Definition(ChainName.Testnet, Empty).Chains);
        Assert.Equal("testnet", bare.NetworkLabel);
        Assert.Equal(["NEAR-NEAR", "USDC-NEAR"], bare.PaymentMethods.Select(m => bare.PaymentMethodId(m).ToString()));
        Assert.Equal(NEARChains.TestnetUsdc, Method(bare, "USDC").TokenHash);
        Assert.Contains("(testnet)", Method(bare, "NEAR").DisplayName);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NEAR_USDT_CONTRACT"] = " USDT.Fakes.Testnet ", ["NEAR_JSONRPC_URI"] = "https://rpc.example/near" })
            .Build();
        var configured = Assert.Single(NEARChains.Definition(ChainName.Regtest, configuration).Chains);
        Assert.Equal(["NEAR-NEAR", "USDC-NEAR", "USDT-NEAR"], configured.PaymentMethods.Select(m => configured.PaymentMethodId(m).ToString()));
        Assert.Equal("usdt.fakes.testnet", Method(configured, "USDT").TokenHash);
        Assert.Equal(["https://rpc.example/near"], configured.JsonRpcUris);

        var broken = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NEAR_USDT_CONTRACT"] = "not an account!" }).Build();
        Assert.Throws<InvalidOperationException>(() => NEARChains.Definition(ChainName.Testnet, broken));
    }

    [Theory]
    [InlineData("shop.near", true, "shop.near")]
    [InlineData(" Pay-1.Shop.near ", true, "pay-1.shop.near")]
    [InlineData("a_b-c.d", true, "a_b-c.d")]
    [InlineData("225922199c374492db2c888aa7b646d97127350a2827986a354e703cdcd9e32b", true, "225922199c374492db2c888aa7b646d97127350a2827986a354e703cdcd9e32b")]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", true, "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed")]
    [InlineData("a", false, "")]
    [InlineData("shop..near", false, "")]
    [InlineData(".shop.near", false, "")]
    [InlineData("-shop.near", false, "")]
    [InlineData("shop_.near", false, "")]
    [InlineData("shop near", false, "")]
    [InlineData("shöp.near", false, "")]
    [InlineData("", false, "")]
    public void Account_ids_follow_the_near_rules(string input, bool valid, string canonical)
    {
        Assert.Equal(valid, NearAccountId.TryNormalize(input, out var normalized));
        Assert.Equal(canonical, normalized);
        Assert.Equal(valid, AddressRules.TryNormalize(Mainnet, input, out var address, out var key));
        if (valid)
        {
            Assert.Equal(canonical, address);
            Assert.Equal(canonical, key);
        }
        Assert.False(NearAccountId.TryNormalize(new string('a', 65), out _));
        Assert.True(NearAccountId.IsImplicit(new string('f', 64)));
        Assert.False(NearAccountId.IsImplicit("shop.near"));
    }

    [Fact]
    public void Account_lists_refuse_duplicates_and_explain_a_bad_entry()
    {
        Assert.True(AddressRules.TryNormalizeAll(Mainnet, ["Shop.near", "pay.shop.near"], out var normalized, out _));
        Assert.Equal(["shop.near", "pay.shop.near"], normalized);
        Assert.False(AddressRules.TryNormalizeAll(Mainnet, ["shop.near", "SHOP.NEAR"], out _, out var error));
        Assert.Contains("Duplicate", error);
        Assert.False(AddressRules.TryNormalizeAll(Mainnet, ["shop.near", "not an account"], out _, out error));
        Assert.Contains("Address 2 is not a NEAR account id", error);
    }

    [Fact]
    public void Payment_ids_link_to_the_explorer_by_transaction_or_receipt_only()
    {
        var pmi = Mainnet.PaymentMethodId(Method(Mainnet, "NEAR"));
        var links = new NearTransactionLinkProvider(Mainnet.BlockExplorerLink, pmi);
        var byTransaction = new TokenTransfer("GqAegenFWRzDJeqXxw8WCzRiyZkkT14RhmB1rW6GtY7L", -1, 5, 1, TokenRegistry.Native, "alice.near", "my-shop.near", 1);
        var byReceipt = new TokenTransfer("5kEgBKJq8s2Lm6MEcFrKH573Do8EJvg67vXkjtMYHGjB", 2, 5, 1, TokenRegistry.Native, "intents.near", "my-shop.near", 1);
        var byBlock = new TokenTransfer("my-shop.near@6mBs73xnKQ39ynjRdMCeJDBmVkF1WpPtCGCs1NaYBbJy", -1, 5, 1, TokenRegistry.Native, "", "my-shop.near", 1);

        Assert.Equal("GqAegenFWRzDJeqXxw8WCzRiyZkkT14RhmB1rW6GtY7L--1-NEAR-NEAR", PaymentRecorder.PaymentId(pmi, byTransaction));
        Assert.Equal("https://nearblocks.io/txns/GqAegenFWRzDJeqXxw8WCzRiyZkkT14RhmB1rW6GtY7L", links.GetTransactionLink(PaymentRecorder.PaymentId(pmi, byTransaction)));
        Assert.Equal("https://nearblocks.io/txns/5kEgBKJq8s2Lm6MEcFrKH573Do8EJvg67vXkjtMYHGjB", links.GetTransactionLink(PaymentRecorder.PaymentId(pmi, byReceipt)));
        Assert.Null(links.GetTransactionLink(PaymentRecorder.PaymentId(pmi, byBlock)));
        Assert.Null(links.GetTransactionLink("something-else"));
    }

    [Fact]
    public void Amounts_have_24_decimals_and_the_checkout_link_is_the_account_until_the_pay_page_is_known()
    {
        var near = Method(Mainnet, "NEAR");
        Assert.Equal(BigInteger.Parse("1500000000000000000000000"), ChainPaymentLinkExtension.ToBaseUnits(1.5m, 24));
        Assert.Equal(1.5m, PaymentRecorder.ToDecimal(BigInteger.Parse("1500000000000000000000000"), 24));
        Assert.Equal(0.000001m, PaymentRecorder.ToDecimal(1, 6));
        Assert.Equal("shop.near", ChainPaymentLinkExtension.Build(Mainnet, near, "shop.near", 1.5m, PaymentLinkFormat.Standard));
        Assert.Equal("shop.near", ChainPaymentLinkExtension.Build(Mainnet, near, "shop.near", 1.5m, PaymentLinkFormat.AddressOnly));
        Assert.Null(ChainPaymentLinkExtension.Build(Mainnet, near, null, 1.5m, PaymentLinkFormat.Standard));
        Assert.Equal(new PaymentMethodId("USDC-NEAR"), Mainnet.PaymentMethodId(Method(Mainnet, "USDC")));
        Assert.Equal("USDC", Mainnet.FindByToken(NEARChains.MainnetUsdc)?.Currency);
    }
}
