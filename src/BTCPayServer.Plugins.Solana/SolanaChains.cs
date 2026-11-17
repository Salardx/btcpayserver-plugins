using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Solana;

/// <summary>
/// Solana: SOL and the two dollar tokens, both issued natively there (USDC by Circle, USDT by Tether). BTCPay's network
/// type picks mainnet or devnet; regtest counts as devnet. Circle issues test USDC on devnet; there is no canonical
/// test USDT, so that method appears on devnet only when <c>SOLANA_USDT_CONTRACT</c> names a token mint.
/// </summary>
public static class SolanaChains
{
    /// <summary>
    /// SOL is quoted in USD on Coinbase, which answers from anywhere (Binance refuses US addresses, and BTCPay's Kraken
    /// source reads a fixed list of pairs without SOL); the dollar tokens are quoted on Kraken; everything else goes through USD.
    /// </summary>
    internal static readonly string[] SolRateRules =
    [
        "SOL_USD = coinbasepro(SOL_USD)",
        "SOL_X = SOL_USD * USD_X",
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

    /// <summary>USDC issued by Circle on Solana (6 decimals): the mint on mainnet and Circle's mint on devnet.</summary>
    internal const string MainnetUsdc = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
    internal const string DevnetUsdc = "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";

    /// <summary>USDT issued by Tether on Solana mainnet (6 decimals).</summary>
    internal const string MainnetUsdt = "Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(SolanaChains).Assembly, "BTCPayServer.Plugins.Solana.sol.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "Solana",
        DisplayName = "Solana",
        NavKey = SolanaPlugin.PluginNavKey,
        ViewPrefix = "Solana",
        Chains = [ChainDefaults.ApplyConfiguration(Cluster(network == ChainName.Mainnet), configuration)],
        // The QR code keeps the Solana Pay link, which wallet apps scan; the checkout's button opens the pay page,
        // for a wallet in the browser, which cannot scan.
        PayPage = new PayPageRoute("UISolanaPay", "Pay", ButtonOnly: true),
    };

    /// <summary>
    /// About five blocks a second (measured November 2026: a slot every 0.22 s), and a block is final within about a
    /// second. The client reads at the finalized commitment only; the listener stays eight slots, under two seconds,
    /// behind the newest finalized one, because the public endpoint is served by several nodes and one of them can
    /// be a moment behind the others.
    /// </summary>
    internal static ChainDefinition Cluster(bool mainnet) => new()
    {
        Id = "SOLANA",
        Kind = ChainProfile.Solana,
        DisplayName = "Solana",
        NetworkLabel = mainnet ? "mainnet" : "devnet",
        NativeSymbol = "SOL",
        NativeDecimals = 9,
        JsonRpcUris = mainnet
            ? ["https://api.mainnet-beta.solana.com", "https://solana-rpc.publicnode.com"]
            : ["https://api.devnet.solana.com"],
        BlockExplorerLink = mainnet ? "https://explorer.solana.com/tx/{0}" : "https://explorer.solana.com/tx/{0}?cluster=devnet",
        CryptoImagePath = Image,
        BlockTimeSeconds = 0.2,
        FinalityBlocks = 8,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "SOL",
                CurrencyName = "Solana",
                DisplayName = Name("SOL", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 9,
                Divisibility = 6,
                RateRules = SolRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDC",
                CurrencyName = "USD Coin",
                DisplayName = Name("USDC", mainnet),
                TokenHash = mainnet ? MainnetUsdc : DevnetUsdc,
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

    static string Name(string currency, bool mainnet) => mainnet ? $"{currency} on Solana" : $"{currency} on Solana (devnet)";
}
