using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>A chain is scanned only once some store has set up one of its payment methods. Until then the listener and the summary updater sleep.</summary>
public sealed class ChainActivationService<TPlugin> : IDisposable where TPlugin : class
{
    static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    readonly StoreRepository stores;
    readonly PaymentMethodHandlerDictionary handlers;
    readonly ChainPluginContext<TPlugin> context;
    readonly IEventAggregatorSubscription[] subscriptions;
    readonly object gate = new();
    HashSet<string>? active;
    DateTimeOffset expiresAt;
    long version;

    public ChainActivationService(StoreRepository stores, PaymentMethodHandlerDictionary handlers,
        ChainPluginContext<TPlugin> context, EventAggregator events)
    {
        this.stores = stores;
        this.handlers = handlers;
        this.context = context;
        subscriptions =
        [
            events.Subscribe<ChainSettingsChanged>(e =>
            {
                if (e.PluginId == this.context.PluginId)
                    Invalidate();
            }),
            events.SubscribeAny<StoreEvent>(_ => Invalidate()),
        ];
    }

    public async Task<bool> IsActivatedAsync(string chainId, CancellationToken cancellationToken) =>
        (await GetActive(cancellationToken)).Contains(chainId);

    public void Invalidate()
    {
        lock (gate)
        {
            version++;
            active = null;
            expiresAt = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        foreach (var subscription in subscriptions)
            subscription.Dispose();
    }

    async Task<HashSet<string>> GetActive(CancellationToken cancellationToken)
    {
        long seen;
        lock (gate)
        {
            if (active is not null && expiresAt > DateTimeOffset.UtcNow)
                return [.. active];
            seen = version;
        }
        var loaded = await Load(cancellationToken);
        lock (gate)
        {
            if (seen != version)
                return loaded;
            active = loaded;
            expiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
            return [.. active];
        }
    }

    async Task<HashSet<string>> Load(CancellationToken cancellationToken)
    {
        var methods = context.PaymentMethods.Select(pair => (pair.Chain.Id, Pmi: pair.Chain.PaymentMethodId(pair.Method))).ToArray();
        var chainCount = context.Chains.Count;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in (await stores.GetStores()).Where(store => !store.Archived))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var excluded = store.GetStoreBlob().GetExcludedPaymentMethods();
            var updated = false;
            foreach (var (chainId, pmi) in methods)
            {
                if (result.Contains(chainId) || !handlers.TryGetValue(pmi, out var handler))
                    continue;
                var config = store.GetPaymentMethodConfig<ChainPaymentMethodConfig>(pmi, handlers);
                if (config is null || !config.ActivatesChain(excluded.Match(pmi)))
                    continue;
                result.Add(chainId);
                if (!config.Activated)
                {
                    config.MarkActivated();
                    store.SetPaymentMethodConfig(handler, config);
                    updated = true;
                }
            }
            if (updated)
                await stores.UpdateStore(store);
            if (result.Count == chainCount)
                break;
        }
        return result;
    }
}
