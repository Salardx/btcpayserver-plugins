using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Neo;

/// <summary>GAS on Neo N3 and on Neo X. BTCPay's own network type picks mainnet or testnet for both chains; regtest counts as testnet.</summary>
public static class NeoChains
{
    /// <summary>GAS trades on Binance against BTC and USDT; everything else goes through BTC like the other coins.</summary>
    internal static readonly string[] GasRateRules =
    [
        "GAS_BTC = binance(GAS_BTC)",
        "GAS_USDT = binance(GAS_USDT)",
        "GAS_X = GAS_BTC * BTC_X",
    ];

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(NeoChains).Assembly, "BTCPayServer.Plugins.Neo.neo.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration)
    {
        var mainnet = network == ChainName.Mainnet;
        return new ChainPluginDefinition
        {
            Id = "Neo",
            DisplayName = "Neo",
            NavKey = NeoPlugin.PluginNavKey,
            ViewPrefix = "Neo",
            Chains =
            [
                ChainDefaults.ApplyConfiguration(NeoN3(mainnet), configuration),
                ChainDefaults.ApplyConfiguration(NeoX(mainnet), configuration),
            ],
        };
    }

    /// <summary>GAS is the NEP-17 contract on Neo N3, 8 decimals, final at one block (dBFT).</summary>
    internal static ChainDefinition NeoN3(bool mainnet) => new()
    {
        Id = "NEO",
        Kind = ChainProfile.NeoN3,
        DisplayName = "Neo N3",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "GAS",
        NativeDecimals = 8,
        NetworkMagic = mainnet ? 860833102u : 894710606u,
        JsonRpcUris = mainnet
            ? ["https://mainnet1.neo.coz.io:443", "http://seed1.neo.org:10332"]
            : ["https://testnet1.neo.coz.io:443", "http://seed1t5.neo.org:20332"],
        BlockExplorerLink = mainnet ? "https://dora.coz.io/transaction/neo3/mainnet/{0}" : "https://dora.coz.io/transaction/neo3/testnet/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = 15,
        FinalityBlocks = 0,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "GAS",
                CurrencyName = "Neo GAS",
                DisplayName = mainnet ? "GAS on Neo N3" : "GAS on Neo N3 (testnet)",
                TokenHash = TokenRegistry.GasHash,
                Decimals = 8,
                Divisibility = 8,
                RateRules = GasRateRules,
            },
        ],
    };

    /// <summary>GAS is the native coin of Neo X: 18 decimals on chain, shown with 8 so amounts stay readable. Also dBFT.</summary>
    internal static ChainDefinition NeoX(bool mainnet) => new()
    {
        Id = "NEOX",
        Kind = ChainProfile.Evm,
        DisplayName = "Neo X",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "GAS",
        NativeDecimals = 18,
        ChainId = mainnet ? 47763 : 12227332,
        JsonRpcUris = mainnet
            ? ["https://mainnet-1.rpc.banelabs.org", "https://mainnet-2.rpc.banelabs.org"]
            : ["https://neoxt4seed1.ngd.network", "https://neoxt4seed2.ngd.network"],
        BlockExplorerLink = mainnet ? "https://xexplorer.neo.org/tx/{0}" : "https://xt4scan.ngd.network/tx/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = 5,
        FinalityBlocks = 0,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "GAS",
                CurrencyName = "Neo GAS",
                DisplayName = mainnet ? "GAS on Neo X" : "GAS on Neo X (testnet)",
                TokenHash = TokenRegistry.Native,
                Decimals = 18,
                Divisibility = 8,
                RateRules = GasRateRules,
            },
        ],
    };
}
