using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

namespace BTCPayServer.Plugins.IoTeX;

/// <summary>
/// IOTX and ioUSDT on IoTeX as BTCPay Server payment methods. The plugin only watches the chain:
/// the store lists the addresses it controls, every invoice reserves one of them, and the listener records the
/// transfers that arrive there once they are final. No keys, no node, no custody.
/// </summary>
public class IoTeXPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = "IoTeXPlugin";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }];

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        services.AddChainPaymentPlugin<IoTeXPlugin>(IoTeXChains.Definition(
            bootstrap.GetRequiredService<NBXplorerNetworkProvider>().NetworkType,
            bootstrap.GetRequiredService<IConfiguration>()));
        base.Execute(services);
    }
}
