namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>Raised when a store or the server changed the settings of one plugin; its clients and caches reload.</summary>
public sealed class ChainSettingsChanged
{
    public required string PluginId { get; init; }
}

public sealed class ChainNodeStateChanged
{
    public required string PluginId { get; init; }
    public required string ChainId { get; init; }
    public required ChainRpcSummary Summary { get; init; }
}
