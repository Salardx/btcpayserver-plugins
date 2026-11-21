using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.IoTeX;

/// <summary>
/// IoTeX: the chain's own coin, IOTX, plus ioUSDT, Tether USD bridged to IoTeX by ioTube. IoTeX answers the Ethereum
/// JSON-RPC on its public endpoints, and a plain IOTX transfer, the native kind sent by IoTeX wallets included, appears
/// there as an ordinary transaction with a value and a receipt (checked on mainnet, November 2026), so the shared EVM
/// client reads IoTeX as it reads any EVM chain. BTCPay's network type picks mainnet (chain 4689) or the testnet
/// (chain 4690); regtest counts as testnet. There is no test ioUSDT, so that method appears on the testnet only when
/// <c>IOTEX_USDT_CONTRACT</c> names a contract.
/// </summary>
public static class IoTeXChains
{
    /// <summary>
    /// IOTX is quoted against USDT on Binance and turned into dollars at Kraken's USDT rate: Coinbase has delisted IOTX,
    /// Kraken does not list it, and its Binance BTC pair moves in steps of one satoshi, about a tenth of the price.
    /// Everything else goes through USD.
    /// </summary>
    internal static readonly string[] IotxRateRules =
    [
        "IOTX_USD = binance(IOTX_USDT) * kraken(USDT_USD)",
        "IOTX_X = IOTX_USD * USD_X",
    ];

    internal static readonly string[] UsdtRateRules =
    [
        "USDT_USD = kraken(USDT_USD)",
        "USDT_X = USDT_USD * USD_X",
    ];

    /// <summary>ioUSDT: Tether USD bridged by ioTube (6 decimals), the dollar token the IoTeX Foundation pays its grants in.</summary>
    internal const string MainnetUsdt = "0x6fbcdc1169b5130c59e72e51ed68a84841c98cd1";

    static readonly string Image = ChainDefaults.EmbeddedImage(typeof(IoTeXChains).Assembly, "BTCPayServer.Plugins.IoTeX.iotx.svg");

    public static ChainPluginDefinition Definition(ChainName network, IConfiguration configuration) => new()
    {
        Id = "IoTeX",
        DisplayName = "IoTeX",
        NavKey = IoTeXPlugin.PluginNavKey,
        ViewPrefix = "IoTeX",
        Chains = [ChainDefaults.ApplyConfiguration(Chain(network == ChainName.Mainnet), configuration)],
    };

    /// <summary>
    /// A block every 2.5 seconds (measured November 2026 on mainnet and testnet). IoTeX's Roll-DPoS consensus makes a
    /// block final once its delegates commit it; the listener still stays two blocks behind the tip, five seconds, so
    /// that a node which shows blocks early cannot make it record a payment that is not final.
    /// </summary>
    internal static ChainDefinition Chain(bool mainnet) => new()
    {
        Id = "IOTEX",
        Kind = ChainProfile.Evm,
        DisplayName = "IoTeX",
        NetworkLabel = mainnet ? "mainnet" : "testnet",
        NativeSymbol = "IOTX",
        NativeDecimals = 18,
        ChainId = mainnet ? 4689 : 4690,
        JsonRpcUris = mainnet
            ? ["https://babel-api.mainnet.iotex.io", "https://rpc.ankr.com/iotex"]
            : ["https://babel-api.testnet.iotex.io", "https://rpc.ankr.com/iotex_testnet"],
        BlockExplorerLink = mainnet ? "https://iotexscan.io/tx/{0}" : "https://testnet.iotexscan.io/tx/{0}",
        CryptoImagePath = Image,
        BlockTimeSeconds = 2.5,
        FinalityBlocks = 2,
        PaymentMethods =
        [
            new PaymentMethodDefinition
            {
                Currency = "IOTX",
                CurrencyName = "IoTeX",
                DisplayName = Name("IOTX", mainnet),
                TokenHash = TokenRegistry.Native,
                Decimals = 18,
                Divisibility = 8,
                RateRules = IotxRateRules,
            },
            new PaymentMethodDefinition
            {
                Currency = "USDT",
                CurrencyName = "Tether USD (ioTube)",
                DisplayName = Name("ioUSDT", mainnet),
                TokenHash = mainnet ? MainnetUsdt : "",
                Decimals = 6,
                Divisibility = 6,
                RateRules = UsdtRateRules,
            },
        ],
    };

    static string Name(string currency, bool mainnet) =>
        mainnet ? $"{currency} on IoTeX" : $"{currency} on IoTeX (testnet)";
}
