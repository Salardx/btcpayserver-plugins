using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>Applies the server-side node overrides once BTCPay's settings repository is available.</summary>
public sealed class ChainConfigurationBootstrapper<TPlugin>(ISettingsRepository settings, ChainPluginContext<TPlugin> context, EventAggregator events)
    : IHostedService where TPlugin : class
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var chain in context.Definition.Chains)
            context.Replace(await ChainDefaults.OverrideWithServerSettingsAsync(chain, settings));
        events.Publish(new ChainSettingsChanged { PluginId = context.PluginId });
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Refreshes the node health summary of every activated chain.</summary>
public sealed class ChainSummaryUpdater<TPlugin>(
    ChainRpcProvider<TPlugin> rpc,
    ChainPluginContext<TPlugin> context,
    ChainActivationService<TPlugin> activation,
    ILogger<ChainSummaryUpdater<TPlugin>> logger) : IHostedService where TPlugin : class
{
    CancellationTokenSource? cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var chain in context.Chains)
            _ = Loop(chain.Id, cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cts?.Cancel();
        return Task.CompletedTask;
    }

    async Task Loop(string chainId, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!await activation.IsActivatedAsync(chainId, token))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), token);
                        continue;
                    }
                    await rpc.UpdateSummary(chainId, token);
                    await Task.Delay(rpc.IsAvailable(chainId) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(20), token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    logger.LogError(ex, "{Chain}: summary update failed", chainId);
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
            }
        }
        catch when (token.IsCancellationRequested)
        {
        }
    }
}

public sealed class ChainSyncSummaryProvider<TPlugin>(ChainRpcProvider<TPlugin> rpc, ChainPluginContext<TPlugin> context) : ISyncSummaryProvider
    where TPlugin : class
{
    public string Partial => $"{context.Definition.ViewPrefix}/SyncSummary";

    public bool AllAvailable() => rpc.Summaries.All(pair => pair.Value.RpcAvailable);

    public IEnumerable<ISyncStatus> GetStatuses() =>
        rpc.Summaries.Select(pair => new ChainSyncStatus { PaymentMethodId = pair.Value.DisplayName, Summary = pair.Value });
}
