using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Avalanche;

/// <summary>
/// The Avalanche C-Chain: the chain's own coin plus the two dollar tokens, both issued natively there (USDC by Circle,
/// USDT by Tether). BTCPay's network type picks mainnet (chain 43114) or the Fuji testnet (chain 43113); regtest counts
/// as testnet. Circle issues test USDC on Fuji; there is no canonical test USDT, so that method appears on Fuji only
/// when <c>AVAXC_USDT_CONTRACT</c> names a contract.
/// </summary>
public static class AvalancheChains
{
    /// <summary>
    /// AVAX is quoted in USD on Coinbase, which answers from anywhere (Binance refuses US addresses, and BTCPay's Kraken
    /// source reads a fixed list of pairs without AVAX); the dollar tokens are quoted on Kraken; everything else goes through USD.
    /// </summary>
    internal static readonly string[] AvaxRateRules =
    [
        "AVAX_USD = coinbasepro(AVAX_USD)",
        "AVAX_X = AVAX_USD * USD_X",
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

    /// <summary>USDC issued by Circle on the C-Chain (6 decimals): the mainnet contract and Circle's Fuji contract.</summary>
    internal const string MainnetUsdc = "0xb97ef9ef8734c71904d8002f8b6bc66dd9c48a6e";
    internal const string FujiUsdc = "0x5425890298aed601595a70ab815c96711a31bc65";

    /// <summary>USDt issued by Tether on the C-Chain (6 decimals).</summary>
    internal const string MainnetUsdt = "0x9702230a8ea53601f5cd2dc00fdbc13d4df4a8c7";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(AvalancheChains).Assembly, "BTCPayServer.Plugins.Avalanche.avax.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "Avalanche",
        DisplayName = "Avalanche",
        NavKey = AvalanchePlugin.PluginNavKey,
        ViewPrefix = "Avalanche",
        Chains = [ChainDefaults.ApplyConfiguration(CChain(network == ChainName.Mainnet), configuration)],
    };

    /// <summary>
    /// A block about every second (measured November 2026: 0.99 s on mainnet, 1.4 s on Fuji). A block the node has
    /// accepted is final: Avalanche's consensus does not reorganise. The listener still stays two blocks behind the
    /// tip, about two seconds, so that a node set up to show blocks before they are accepted cannot make it record
    /// a payment that is not final.
    /// </summary>
    internal static ChainDefinition CChain(bool mainnet) => new()
    {
        Id = "AVAXC",
        Kind = ChainProfile.Evm,
        DisplayName = "Avalanche C-Chain",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "AVAX",
        NativeDecimals = 18,
        ChainId = mainnet ? 43114 : 43113,
        JsonRpcUris = mainnet
            ? ["https://api.avax.network/ext/bc/C/rpc", "https://avalanche-c-chain-rpc.publicnode.com"]
            : ["https://api.avax-test.network/ext/bc/C/rpc", "https://avalanche-fuji-c-chain-rpc.publicnode.com"],
        BlockExplorerLink = mainnet ? "https://snowtrace.io/tx/{0}" : "https://testnet.snowtrace.io/tx/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = 1,
        FinalityBlocks = 2,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "AVAX",
                CurrencyName = "Avalanche",
                DisplayName = Name("AVAX", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 18,
                Divisibility = 8,
                RateRules = AvaxRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDC",
                CurrencyName = "USD Coin",
                DisplayName = Name("USDC", mainnet),
                TokenHash = mainnet ? MainnetUsdc : FujiUsdc,
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

    static string Name(string currency, bool mainnet) =>
        mainnet ? $"{currency} on Avalanche" : $"{currency} on Avalanche (Fuji testnet)";
}
