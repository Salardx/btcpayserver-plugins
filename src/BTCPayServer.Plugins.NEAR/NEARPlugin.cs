using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

namespace BTCPayServer.Plugins.NEAR;

/// <summary>
/// NEAR, USDC and USDT on NEAR as BTCPay Server payment methods. The plugin only watches the chain: the store lists
/// the accounts it controls, every invoice reserves one of them, and the listener records what arrives there by
/// watching the accounts' balances. The customer pays from any NEAR wallet on the plugin's pay page. No keys, no
/// node, no custody.
/// </summary>
public class NEARPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = "NEARPlugin";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }];

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        services.AddChainPaymentPlugin<NEARPlugin>(NEARChains.Definition(
            bootstrap.GetRequiredService<NBXplorerNetworkProvider>().NetworkType,
            bootstrap.GetRequiredService<IConfiguration>()));
        base.Execute(services);
    }
}
