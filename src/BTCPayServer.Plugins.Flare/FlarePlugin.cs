using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

namespace BTCPayServer.Plugins.Flare;

/// <summary>
/// FLR, USD₮0 and FXRP on Flare as BTCPay Server payment methods, and Flare's FTSO oracle as an exchange-rate source
/// for any store. The plugin only watches the chain: the store lists the addresses it controls, every invoice
/// reserves one of them, and the listener records the transfers that arrive there once they are final. No keys, no
/// node, no custody.
/// </summary>
public class FlarePlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = "FlarePlugin";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }];

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        services.AddChainPaymentPlugin<FlarePlugin>(FlareChains.Definition(
            bootstrap.GetRequiredService<NBXplorerNetworkProvider>().NetworkType,
            bootstrap.GetRequiredService<IConfiguration>()));
        services.AddRateProvider<FtsoRateProvider>();
        base.Execute(services);
    }
}
