using BTCPayServer.Payments;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Chains;

/// <summary>One currency a store can accept on a chain: the chain's own coin or a token contract.</summary>
public sealed record PaymentMethodDefinition
{
    /// <summary>Currency code as BTCPay Server sees it ("GAS", "BNB", "USDT").</summary>
    public required string Currency { get; init; }
    public required string CurrencyName { get; init; }
    /// <summary>"GAS on Neo N3": the name shown in menus and on the checkout.</summary>
    public required string DisplayName { get; init; }
    /// <summary>
    /// The token contract in the chain's lowercase form, or <see cref="TokenRegistry.Native"/> for the chain's own
    /// coin. Empty means "no contract on this network": the method is dropped unless configuration supplies one.
    /// </summary>
    public required string TokenHash { get; init; }
    /// <summary>Decimals on chain (18 for most EVM tokens, 8 for GAS on Neo N3, 24 for NEAR).</summary>
    public required int Decimals { get; init; }
    /// <summary>Decimals shown to the customer; amounts are kept at this precision so they stay readable.</summary>
    public required int Divisibility { get; init; }
    /// <summary>Default rate rules for this currency, in BTCPay's rule syntax.</summary>
    public string[] RateRules { get; init; } = [];

    public bool IsNative => TokenHash == TokenRegistry.Native;
}

/// <summary>
/// A chain and the payment methods offered on it. The id is the suffix of the payment method ids ("NEO", "NEOX",
/// "BSC") and the prefix of its configuration keys, so it must be unique across all plugins of this family.
/// </summary>
public sealed record ChainDefinition
{
    public required string Id { get; init; }
    /// <summary><see cref="ChainProfile.NeoN3"/>, <see cref="ChainProfile.Evm"/> or <see cref="ChainProfile.Near"/>.</summary>
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string NetworkLabel { get; init; }
    public required string NativeSymbol { get; init; }
    public required int NativeDecimals { get; init; }
    public uint? NetworkMagic { get; init; }
    public long? ChainId { get; init; }
    public required string[] JsonRpcUris { get; init; }
    /// <summary>Explorer link with <c>{0}</c> for the transaction id, as BTCPay Server formats it.</summary>
    public required string BlockExplorerLink { get; init; }
    public required string CryptoImagePath { get; init; }
    public double BlockTimeSeconds { get; init; } = 15;
    /// <summary>How many blocks the listener stays behind the tip. Zero on dBFT chains, where a block is final when it exists.</summary>
    public int FinalityBlocks { get; init; }
    public required IReadOnlyList<PaymentMethodDefinition> PaymentMethods { get; init; }

    public bool IsEvm => Kind == ChainProfile.Evm;
    public bool IsNear => Kind == ChainProfile.Near;
    public string AddressPlaceholder => IsEvm ? "0x..." : IsNear ? "shop.near" : "N...";

    public PaymentMethodId PaymentMethodId(PaymentMethodDefinition method) => new($"{method.Currency}-{Id}");

    public PaymentMethodDefinition? FindByToken(string tokenHash) =>
        PaymentMethods.FirstOrDefault(m => string.Equals(m.TokenHash, tokenHash, StringComparison.OrdinalIgnoreCase));

    public HashSet<string> TokenHashes() => PaymentMethods.Select(m => m.TokenHash).ToHashSet(StringComparer.Ordinal);

    /// <summary>What the vendored chain clients need.</summary>
    public ChainProfile ToChainProfile() => new(
        Kind, NetworkLabel, DisplayName, NetworkLabel, ChainId, NetworkMagic, NativeSymbol, NativeDecimals,
        BlockExplorerLink.Replace("{0}", "{tx}"), FinalityBlocks, JsonRpcUris);
}

/// <summary>The plugin's page where a customer pays from a wallet in the browser; the checkout links to it and puts it in the QR code.</summary>
public sealed record PayPageRoute(string Controller, string Action);

/// <summary>What a plugin tells the shared services about itself.</summary>
public sealed record ChainPluginDefinition
{
    /// <summary>Short stable id ("Neo", "BNBChain"): tells the plugins of this family apart in events and views.</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>The layout's menu item id for the store pages.</summary>
    public required string NavKey { get; init; }
    /// <summary>Folder of the plugin's partial views under Views/Shared ("Neo" gives "Neo/StoreNav").</summary>
    public required string ViewPrefix { get; init; }
    public required IReadOnlyList<ChainDefinition> Chains { get; init; }
    /// <summary>Set when the plugin serves a pay page; chains without a wallet link standard (NEAR) send the customer there.</summary>
    public PayPageRoute? PayPage { get; init; }
}

/// <summary>Server-wide override of the JSON-RPC endpoint for one chain.</summary>
public sealed class ChainServerSettings
{
    public string? JsonRpcUri { get; set; }
}
