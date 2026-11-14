using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Arc;

/// <summary>
/// Arc, the chain whose own coin is USDC: fees are paid in USDC, so a customer needs nothing else to pay. EURC is
/// a token there, issued by Circle like USDC. BTCPay's network type picks mainnet (chain 5042) or the testnet
/// (chain 5042002); regtest counts as testnet.
/// </summary>
public static class ArcChains
{
    internal static readonly string[] UsdcRateRules =
    [
        "USDC_USD = kraken(USDC_USD)",
        "USDC_X = USDC_USD * USD_X",
    ];

    /// <summary>
    /// EURC is priced at par with the euro: none of BTCPay's rate sources quotes it. An invoice in euros is one EURC
    /// per euro; any other currency goes through BTC, at the euro rate of the source the store uses.
    /// </summary>
    internal static readonly string[] EurcRateRules =
    [
        "EURC_EUR = 1",
        "EURC_BTC = 1 / BTC_EUR",
        "EURC_X = EURC_BTC * BTC_X",
    ];

    /// <summary>
    /// Every movement of USDC on Arc, a plain transfer, a call of its ERC-20 interface or a transfer made by a
    /// contract, is announced by one Transfer log from this address, with the amount in the coin's 18 decimals.
    /// The ERC-20 interface at 0x3600...0000 logs its own calls a second time in 6 decimals; those are not read.
    /// </summary>
    internal const string UsdcTransferLog = "0xfffffffffffffffffffffffffffffffffffffffe";

    /// <summary>EURC issued by Circle on Arc (6 decimals): the mainnet contract and the testnet one.</summary>
    internal const string MainnetEurc = "0xbef5f6d51cb62b58e6a8f77868681825c6fe21c1";
    internal const string TestnetEurc = "0x89b50855aa3be2f677cd6303cec089b5f319d72a";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(ArcChains).Assembly, "BTCPayServer.Plugins.Arc.arc.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "Arc",
        DisplayName = "Arc",
        NavKey = ArcPlugin.PluginNavKey,
        ViewPrefix = "Arc",
        Chains = [ChainDefaults.ApplyConfiguration(Network(network == ChainName.Mainnet), configuration)],
    };

    /// <summary>
    /// A block every half second on mainnet and every 0.7 s on the testnet (measured November 2026), final when it
    /// exists: Arc's consensus does not reorganise. The listener stays one block behind the tip, because the public
    /// endpoint is served by several nodes and one of them can be a moment behind the others.
    /// </summary>
    internal static ChainDefinition Network(bool mainnet) => new()
    {
        Id = "ARC",
        Kind = ChainProfile.Evm,
        DisplayName = "Arc",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "USDC",
        NativeDecimals = 18,
        ChainId = mainnet ? 5042 : 5042002,
        JsonRpcUris = mainnet
            ? ["https://rpc.mainnet.arc.io", "https://rpc.drpc.mainnet.arc.io"]
            : ["https://rpc.testnet.arc.io", "https://rpc.drpc.testnet.arc.io"],
        BlockExplorerLink = mainnet ? "https://explorer.arc.io/tx/{0}" : "https://explorer.testnet.arc.io/tx/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = mainnet ? 0.5 : 0.7,
        FinalityBlocks = 1,
        NativeTransferLog = UsdcTransferLog,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "USDC",
                CurrencyName = "USD Coin",
                DisplayName = Name("USDC", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 18,
                Divisibility = 6,
                RateRules = UsdcRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "EURC",
                CurrencyName = "Euro Coin",
                DisplayName = Name("EURC", mainnet),
                TokenHash = mainnet ? MainnetEurc : TestnetEurc,
                Decimals = 6,
                Divisibility = 6,
                RateRules = EurcRateRules,
            },
        ],
    };

    static string Name(string currency, bool mainnet) => mainnet ? $"{currency} on Arc" : $"{currency} on Arc (testnet)";
}
