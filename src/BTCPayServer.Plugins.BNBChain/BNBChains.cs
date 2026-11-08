using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.BNBChain;

/// <summary>
/// BNB Smart Chain: the chain's own coin plus the two dollar tokens merchants ask for. BTCPay's network type picks
/// mainnet (chain 56) or testnet (chain 97); regtest counts as testnet. The testnet has no canonical USDT or USDC,
/// so those methods appear there only when <c>BSC_USDT_CONTRACT</c> or <c>BSC_USDC_CONTRACT</c> names a contract.
/// </summary>
public static class BNBChains
{
    /// <summary>BNB trades on Binance against BTC; the dollar tokens are quoted on Kraken; everything else goes through BTC or USD.</summary>
    internal static readonly string[] BnbRateRules =
    [
        "BNB_BTC = binance(BNB_BTC)",
        "BNB_X = BNB_BTC * BTC_X",
    ];

    internal static readonly string[] UsdtRateRules =
    [
        "USDT_USD = kraken(USDT_USD)",
        "USDT_X = USDT_USD * USD_X",
    ];

    internal static readonly string[] UsdcRateRules =
    [
        "USDC_USD = kraken(USDC_USD)",
        "USDC_X = USDC_USD * USD_X",
    ];

    /// <summary>Binance-Peg BSC-USD (USDT) and Binance-Peg USDC on BNB Smart Chain mainnet; both have 18 decimals, unlike their Ethereum originals.</summary>
    internal const string MainnetUsdt = "0x55d398326f99059ff775485246999027b3197955";
    internal const string MainnetUsdc = "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(BNBChains).Assembly, "BTCPayServer.Plugins.BNBChain.bnb.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "BNBChain",
        DisplayName = "BNB Chain",
        NavKey = BNBChainPlugin.PluginNavKey,
        ViewPrefix = "BNBChain",
        Chains = [ChainDefaults.ApplyConfiguration(SmartChain(network == ChainName.Mainnet), configuration)],
    };

    /// <summary>
    /// Blocks come every 0.75 s since the Maxwell upgrade and are final after a few blocks with fast finality; the
    /// listener stays six blocks (about five seconds) behind the tip and records payments as settled.
    /// </summary>
    internal static ChainDefinition SmartChain(bool mainnet) => new()
    {
        Id = "BSC",
        Kind = ChainProfile.Evm,
        DisplayName = "BNB Smart Chain",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "BNB",
        NativeDecimals = 18,
        ChainId = mainnet ? 56 : 97,
        JsonRpcUris = mainnet
            ? ["https://bsc-dataseed.bnbchain.org", "https://bsc-rpc.publicnode.com"]
            : ["https://bsc-testnet-rpc.publicnode.com", "https://data-seed-prebsc-1-s1.bnbchain.org:8545"],
        BlockExplorerLink = mainnet ? "https://bscscan.com/tx/{0}" : "https://testnet.bscscan.com/tx/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = 0.75,
        FinalityBlocks = 6,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "BNB",
                CurrencyName = "BNB",
                DisplayName = Name("BNB", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 18,
                Divisibility = 8,
                RateRules = BnbRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDT",
                CurrencyName = "Tether USD",
                DisplayName = Name("USDT", mainnet),
                TokenHash = mainnet ? MainnetUsdt : "",
                Decimals = 18,
                Divisibility = 6,
                RateRules = UsdtRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDC",
                CurrencyName = "USD Coin",
                DisplayName = Name("USDC", mainnet),
                TokenHash = mainnet ? MainnetUsdc : "",
                Decimals = 18,
                Divisibility = 6,
                RateRules = UsdcRateRules,
            },
        ],
    };

    static string Name(string currency, bool mainnet) =>
        mainnet ? $"{currency} on BNB Smart Chain" : $"{currency} on BNB Smart Chain (testnet)";
}
