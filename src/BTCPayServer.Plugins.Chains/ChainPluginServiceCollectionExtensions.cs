using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BTCPayServer.Plugins.Chains;

public static class ChainPluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything one plugin of this family needs: its context; the shared stores and recorder (once per
    /// container); its own activation cache, clients, listener and summaries; and per payment method the handler,
    /// payment link, checkout extension, explorer link, pretty name, currency and default rate rules. The plugin
    /// still supplies its controllers and views.
    /// </summary>
    public static ChainPluginContext<TPlugin> AddChainPaymentPlugin<TPlugin>(this IServiceCollection services, ChainPluginDefinition definition)
        where TPlugin : class
    {
        var context = new ChainPluginContext<TPlugin>(definition);
        services.AddSingleton(context);
        services.TryAddSingleton<ChainStateStore>();
        services.TryAddSingleton<AddressAllocator>();
        services.TryAddSingleton<PaymentRecorder>();
        services.AddSingleton<ChainActivationService<TPlugin>>();
        services.AddSingleton<ChainRpcProvider<TPlugin>>();
        services.AddHostedService<ChainConfigurationBootstrapper<TPlugin>>();
        services.AddHostedService<ChainSummaryUpdater<TPlugin>>();
        services.AddHostedService<ChainListener<TPlugin>>();
        services.AddSingleton<ISyncSummaryProvider, ChainSyncSummaryProvider<TPlugin>>();

        var currencies = new Dictionary<string, PaymentMethodDefinition>(StringComparer.Ordinal);
        var rules = new List<string>();
        foreach (var chain in definition.Chains)
        {
            foreach (var method in chain.PaymentMethods)
            {
                var pmi = chain.PaymentMethodId(method);
                if (currencies.TryAdd(method.Currency, method))
                    rules.AddRange(method.RateRules);
                services.AddTransactionLinkProvider(pmi, new ChainTransactionLinkProvider(chain.BlockExplorerLink));
                services.AddDefaultPrettyName(pmi, method.DisplayName);
                services.AddSingleton<IPaymentMethodHandler>(provider =>
                    ActivatorUtilities.CreateInstance<ChainPaymentMethodHandler<TPlugin>>(provider, chain, method));
                services.AddSingleton<IPaymentLinkExtension>(new ChainPaymentLinkExtension(pmi, chain, method));
                services.AddSingleton<ICheckoutModelExtension>(provider =>
                    ActivatorUtilities.CreateInstance<ChainCheckoutModelExtension>(provider, pmi, chain.CryptoImagePath, method.Currency));
            }
        }
        foreach (var method in currencies.Values)
        {
            services.AddCurrencyData(new CurrencyData
            {
                Code = method.Currency,
                Name = method.CurrencyName,
                Symbol = method.Currency,
                Divisibility = method.Divisibility,
                Crypto = true,
            });
        }
        if (rules.Count > 0)
            services.AddSingleton(new DefaultRules(rules.ToArray()));

        var prefix = definition.ViewPrefix;
        services.AddUIExtension("store-wallets-nav", $"{prefix}/StoreNav");
        services.AddUIExtension("server-nav", $"{prefix}/ServerNav");
        services.AddUIExtension("store-invoices-payments", $"{prefix}/PaymentDetails");
        return context;
    }
}
