using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

namespace BTCPayServer.Plugins.Arc;

/// <summary>
/// USDC and EURC on Arc as BTCPay Server payment methods. The plugin only watches the chain: the store lists the
/// addresses it controls, every invoice reserves one of them, and the listener records the transfers that arrive
/// there once they are final. USDC is read from the transfer log Arc writes for every movement of its coin, so a
/// payment made by a contract, a smart account for example, is seen like any other. No keys, no node, no custody.
/// </summary>
public class ArcPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = "ArcPlugin";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }];

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        services.AddChainPaymentPlugin<ArcPlugin>(ArcChains.Definition(
            bootstrap.GetRequiredService<NBXplorerNetworkProvider>().NetworkType,
            bootstrap.GetRequiredService<IConfiguration>()));
        base.Execute(services);
    }
}
