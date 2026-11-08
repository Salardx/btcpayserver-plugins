using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Neo;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Tests;

public class NeoTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    [Fact]
    public void Mainnet_and_testnet_definitions_describe_both_chains()
    {
        var mainnet = NeoChains.Definition(ChainName.Mainnet, Empty);
        var testnet = NeoChains.Definition(ChainName.Regtest, Empty);

        Assert.Equal("Neo", mainnet.Id);
        Assert.Equal(NeoPlugin.PluginNavKey, mainnet.NavKey);
        Assert.Equal(["NEO", "NEOX"], mainnet.Chains.Select(c => c.Id));
        Assert.Equal(["GAS-NEO", "GAS-NEOX"], mainnet.Chains.SelectMany(c => c.PaymentMethods.Select(m => c.PaymentMethodId(m).ToString())));
        Assert.Equal(860833102u, mainnet.Chains[0].NetworkMagic);
        Assert.Equal(47763, mainnet.Chains[1].ChainId);
        Assert.Equal(894710606u, testnet.Chains[0].NetworkMagic);
        Assert.Equal(12227332, testnet.Chains[1].ChainId);
        Assert.All(mainnet.Chains, chain => Assert.Equal(0, chain.FinalityBlocks));
        Assert.All(mainnet.Chains, chain => Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath));
        Assert.Contains("(testnet)", testnet.Chains[0].PaymentMethods[0].DisplayName);
    }

    [Fact]
    public void Chain_profiles_carry_what_the_clients_need()
    {
        var n3 = NeoChains.NeoN3(mainnet: true).ToChainProfile();
        var neox = NeoChains.NeoX(mainnet: true).ToChainProfile();

        Assert.Equal(ChainProfile.NeoN3, n3.Kind);
        Assert.Equal("860833102", n3.NetworkKey);
        Assert.Equal("https://dora.coz.io/transaction/neo3/mainnet/0xabc", n3.ExplorerUrl("0xabc"));
        Assert.Equal(0, n3.Confirmations);
        Assert.True(neox.IsEvm);
        Assert.Equal("evm:47763", neox.NetworkKey);
        Assert.Equal(18, neox.NativeDecimals);
    }

    [Fact]
    public void An_environment_variable_replaces_the_default_nodes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NEOX_JSONRPC_URI"] = "https://rpc.example/neox" })
            .Build();

        var definition = NeoChains.Definition(ChainName.Mainnet, configuration);

        Assert.Equal(["https://rpc.example/neox"], definition.Chains.Single(c => c.Id == "NEOX").JsonRpcUris);
        Assert.Equal(2, definition.Chains.Single(c => c.Id == "NEO").JsonRpcUris.Length);
    }
}
