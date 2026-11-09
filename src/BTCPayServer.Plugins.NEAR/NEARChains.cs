using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.NEAR;

/// <summary>
/// NEAR and the two dollar tokens on it. BTCPay's network type picks mainnet or testnet; regtest counts as testnet.
/// Circle issues USDC on both networks; Tether's USDT exists on mainnet only, so on testnet it appears only when
/// <c>NEAR_USDT_CONTRACT</c> names a contract.
/// </summary>
public static class NEARChains
{
    /// <summary>NEAR trades on Binance against BTC and USDT; the dollar tokens are quoted on Kraken; everything else goes through BTC or USD.</summary>
    internal static readonly string[] NearRateRules =
    [
        "NEAR_BTC = binance(NEAR_BTC)",
        "NEAR_USDT = binance(NEAR_USDT)",
        "NEAR_X = NEAR_BTC * BTC_X",
    ];

    internal static readonly string[] UsdcRateRules =
    [
        "USDC_USD = kraken(USDC_USD)",
        "USDC_X = USDC_USD * USD_X",
    ];

    internal static readonly string[] UsdtRateRules =
    [
        "USDT_USD = kraken(USDT_USD)",
        "USDT_X = USDT_USD * USD_X",
    ];

    /// <summary>USDC issued by Circle on NEAR (NEP-141, 6 decimals): the mainnet contract and Circle's testnet contract.</summary>
    internal const string MainnetUsdc = "17208628f84f5d6ad33f0da3bbbeb27ffcb398eac501a31bd6ad2011e36133a1";
    internal const string TestnetUsdc = "3e2210e1184b45b64c8a434c0a7e7b23cc04ea7eb7a6c3c32520d03d4afcb8af";

    /// <summary>USDT issued by Tether on NEAR mainnet (NEP-141, 6 decimals).</summary>
    internal const string MainnetUsdt = "usdt.tether-token.near";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(NEARChains).Assembly, "BTCPayServer.Plugins.NEAR.near.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "NEAR",
        DisplayName = "NEAR",
        NavKey = NEARPlugin.PluginNavKey,
        ViewPrefix = "NEAR",
        Chains = [ChainDefaults.ApplyConfiguration(Protocol(network == ChainName.Mainnet), configuration)],
        PayPage = new PayPageRoute("UINEARPay", "Pay"),
    };

    /// <summary>
    /// A block every 0.6 seconds, final two or three blocks later; the client reads the final block only, so the
    /// listener needs no extra distance from the tip. NEAR has 24 decimals and is shown with 8.
    /// </summary>
    internal static ChainDefinition Protocol(bool mainnet) => new()
    {
        Id = "NEAR",
        Kind = ChainProfile.Near,
        DisplayName = "NEAR Protocol",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "NEAR",
        NativeDecimals = 24,
        JsonRpcUris = mainnet
            ? ["https://free.rpc.fastnear.com", "https://near.drpc.org", "https://rpc.intea.rs"]
            : ["https://test.rpc.fastnear.com", "https://near-testnet.drpc.org", "https://testnet-rpc.intea.rs"],
        BlockExplorerLink = mainnet ? "https://nearblocks.io/txns/{0}" : "https://testnet.nearblocks.io/txns/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = 0.6,
        FinalityBlocks = 0,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "NEAR",
                CurrencyName = "NEAR",
                DisplayName = Name("NEAR", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 24,
                Divisibility = 8,
                RateRules = NearRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDC",
                CurrencyName = "USD Coin",
                DisplayName = Name("USDC", mainnet),
                TokenHash = mainnet ? MainnetUsdc : TestnetUsdc,
                Decimals = 6,
                Divisibility = 6,
                RateRules = UsdcRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDT",
                CurrencyName = "Tether USD",
                DisplayName = Name("USDT", mainnet),
                TokenHash = mainnet ? MainnetUsdt : "",
                Decimals = 6,
                Divisibility = 6,
                RateRules = UsdtRateRules,
            },
        ],
    };

    static string Name(string currency, bool mainnet) => mainnet ? $"{currency} on NEAR" : $"{currency} on NEAR (testnet)";
}
