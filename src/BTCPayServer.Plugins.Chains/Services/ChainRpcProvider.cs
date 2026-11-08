using System.Collections.Concurrent;
using System.Collections.Immutable;
using BTCPayServer.Abstractions.Contracts;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Chains.Evm;
using NeoPaymentWatcher.Neo;

namespace BTCPayServer.Plugins.Chains.Services;

public sealed class ChainRpcSummary
{
    public required string ChainId { get; init; }
    public required string DisplayName { get; init; }
    public bool RpcAvailable { get; set; }
    public bool Synced { get; set; }
    public long? LatestBlockOnNode { get; set; }
    public long? LatestBlockScanned { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Available => RpcAvailable && Synced;
}

public sealed class ChainSyncStatus : ISyncStatus
{
    public required string PaymentMethodId { get; set; }
    public required ChainRpcSummary Summary { get; init; }
    public bool Available => Summary.Available;
}

/// <summary>One chain client per chain, rebuilt when settings change, plus the health summary the UI and the handlers consult.</summary>
public sealed class ChainRpcProvider<TPlugin> where TPlugin : class
{
    readonly ChainPluginContext<TPlugin> context;
    readonly EventAggregator events;
    readonly ChainStateStore states;
    readonly ILoggerFactory loggers;
    ImmutableDictionary<string, IChainClient> clients = ImmutableDictionary<string, IChainClient>.Empty;

    public ChainRpcProvider(ChainPluginContext<TPlugin> context, EventAggregator events, ChainStateStore states, ILoggerFactory loggers)
    {
        this.context = context;
        this.events = events;
        this.states = states;
        this.loggers = loggers;
        events.Subscribe<ChainSettingsChanged>(e =>
        {
            if (e.PluginId == this.context.PluginId)
                LoadClients();
        });
        LoadClients();
    }

    /// <summary>Health per chain id.</summary>
    public ConcurrentDictionary<string, ChainRpcSummary> Summaries { get; } = new();

    public IChainClient GetClient(string chainId) => clients[chainId];

    public bool IsAvailable(string chainId) =>
        Summaries.TryGetValue(chainId, out var summary) && summary.Available;

    void LoadClients()
    {
        lock (this)
        {
            clients = context.Chains.ToImmutableDictionary(chain => chain.Id, Create, StringComparer.Ordinal);
        }
    }

    IChainClient Create(ChainDefinition chain) => chain.IsEvm
        ? new EvmChainClient(chain.ToChainProfile(), loggers.CreateLogger<EvmChainClient>())
        : new NeoChainClient(chain.ToChainProfile(), loggers.CreateLogger<NeoChainClient>());

    /// <summary>Node reachable, right network, and the listener within ten minutes of the tip.</summary>
    public async Task UpdateSummary(string chainId, CancellationToken cancellationToken)
    {
        if (!context.TryGetChain(chainId, out var chain))
            return;
        var summary = new ChainRpcSummary { ChainId = chain.Id, DisplayName = chain.DisplayName, UpdatedAt = DateTimeOffset.UtcNow };
        try
        {
            var client = GetClient(chainId);
            await client.VerifyNetworkAsync(cancellationToken);
            var tip = (long)await client.GetTipAsync(cancellationToken);
            var state = await states.Get(ChainStateStore.ListenerStateKey(chain));
            summary.LatestBlockOnNode = tip;
            summary.LatestBlockScanned = state?.LastBlockIndex;
            summary.RpcAvailable = true;
            var lag = (long)Math.Max(1, TimeSpan.FromMinutes(10).TotalSeconds / chain.BlockTimeSeconds) + chain.FinalityBlocks;
            summary.Synced = state is not null && tip - state.LastBlockIndex <= lag;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            summary.RpcAvailable = false;
            summary.Error = ex.Message;
        }
        var changed = !Summaries.TryGetValue(chainId, out var previous) || previous.Available != summary.Available;
        Summaries[chainId] = summary;
        if (changed)
            events.Publish(new ChainNodeStateChanged { PluginId = context.PluginId, ChainId = chainId, Summary = summary });
    }
}
