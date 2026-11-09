using System.Globalization;

namespace NeoPaymentWatcher.Chains;

/// <summary>
/// Everything chain-specific the watcher needs at runtime, resolved once at startup from
/// <c>Watcher:Chain</c>, <c>Watcher:Network</c> and the overrides in <see cref="WatcherOptions"/>.
/// </summary>
public sealed record ChainProfile(
    string Kind,
    string Network,
    string DisplayName,
    string NetworkLabel,
    long? ChainId,
    uint? NetworkMagic,
    string NativeSymbol,
    int NativeDecimals,
    string? ExplorerTx,
    int Confirmations,
    string[] RpcUrls)
{
    public const string NeoN3 = "neo-n3";
    public const string Evm = "evm";
    public const string Near = "near";

    public bool IsEvm => Kind == Evm;

    public bool IsNear => Kind == Near;

    /// <summary>Pins a database to one network. Neo N3 keeps its magic number, so databases from before multi-chain support still open.</summary>
    public string NetworkKey => IsEvm
        ? $"evm:{ChainId!.Value.ToString(CultureInfo.InvariantCulture)}"
        : IsNear
            ? $"near:{Network}"
            : NetworkMagic!.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Neo X · testnet", as the checkout page shows it.</summary>
    public string Label => $"{DisplayName} · {NetworkLabel}";

    public string? ExplorerUrl(string? txId) => txId is null || ExplorerTx is null ? null : ExplorerTx.Replace("{tx}", txId);
}
