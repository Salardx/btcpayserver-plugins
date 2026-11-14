using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Flare;

/// <summary>
/// Flare: the chain's own coin, the dollar token USD₮0 and FXRP, the XRP that Flare's FAssets system brings on chain.
/// BTCPay's network type picks mainnet (chain 14) or the Coston2 testnet (chain 114); regtest counts as testnet.
/// Coston2 has a test FXRP; a test USD₮0 appears there only when <c>FLARE_USDT0_CONTRACT</c> names a contract.
/// All three are priced by Flare's own oracle, the FTSO, read on chain by <see cref="FtsoRateProvider"/>.
/// </summary>
public static class FlareChains
{
    internal static readonly string[] FlrRateRules =
    [
        $"FLR_USD = {FtsoRateProvider.SourceId}(FLR_USD)",
        "FLR_X = FLR_USD * USD_X",
    ];

    /// <summary>One FXRP is one XRP held by the FAssets system, so it takes the oracle's XRP price.</summary>
    internal static readonly string[] FxrpRateRules =
    [
        $"FXRP_USD = {FtsoRateProvider.SourceId}(XRP_USD)",
        "FXRP_X = FXRP_USD * USD_X",
    ];

    internal static readonly string[] Usdt0RateRules =
    [
        $"USDT0_USD = {FtsoRateProvider.SourceId}(USDT_USD)",
        "USDT0_X = USDT0_USD * USD_X",
    ];

    /// <summary>USD₮0 on Flare mainnet (6 decimals).</summary>
    internal const string MainnetUsdt0 = "0xe7cd86e13ac4309349f30b3435a9d337750fc82d";

    /// <summary>FXRP (6 decimals, like XRP): the token of the FXRP asset manager on mainnet, and the test token (FTestXRP) of the one on Coston2.</summary>
    internal const string MainnetFxrp = "0xad552a648c74d49e10027ab8a618a3ad4901c5be";
    internal const string Coston2Fxrp = "0x0b6a3645c240605887a5532109323a3e12273dc7";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(FlareChains).Assembly, "BTCPayServer.Plugins.Flare.flr.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "Flare",
        DisplayName = "Flare",
        NavKey = FlarePlugin.PluginNavKey,
        ViewPrefix = "Flare",
        Chains = [ChainDefaults.ApplyConfiguration(Network(network == ChainName.Mainnet), configuration)],
    };

    /// <summary>
    /// A block about every second on mainnet and every two on Coston2 (measured November 2026: 1.07 s and 2.2 s). A
    /// block the node has accepted is final. The listener still stays two blocks behind the tip, so that a node set
    /// up to show blocks before they are accepted cannot make it record a payment that is not final.
    /// </summary>
    internal static ChainDefinition Network(bool mainnet) => new()
    {
        Id = "FLARE",
        Kind = ChainProfile.Evm,
        DisplayName = "Flare",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "FLR",
        NativeDecimals = 18,
        ChainId = mainnet ? 14 : 114,
        JsonRpcUris = mainnet
            ? ["https://flare-api.flare.network/ext/C/rpc", "https://rpc.ankr.com/flare"]
            : ["https://coston2-api.flare.network/ext/C/rpc", "https://rpc.ankr.com/flare_coston2"],
        BlockExplorerLink = mainnet ? "https://flare-explorer.flare.network/tx/{0}" : "https://coston2-explorer.flare.network/tx/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = mainnet ? 1.1 : 2.2,
        FinalityBlocks = 2,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "FLR",
                CurrencyName = "Flare",
                DisplayName = Name("FLR", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 18,
                Divisibility = 6,
                RateRules = FlrRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDT0",
                CurrencyName = "USD₮0",
                DisplayName = Name("USD₮0", mainnet),
                TokenHash = mainnet ? MainnetUsdt0 : "",
                Decimals = 6,
                Divisibility = 6,
                RateRules = Usdt0RateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "FXRP",
                CurrencyName = "FXRP",
                DisplayName = Name("FXRP", mainnet),
                TokenHash = mainnet ? MainnetFxrp : Coston2Fxrp,
                Decimals = 6,
                Divisibility = 6,
                RateRules = FxrpRateRules,
            },
        ],
    };

    static string Name(string currency, bool mainnet) =>
        mainnet ? $"{currency} on Flare" : $"{currency} on Flare (Coston2 testnet)";
}
