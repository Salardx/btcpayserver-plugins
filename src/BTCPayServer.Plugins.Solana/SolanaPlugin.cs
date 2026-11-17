using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.Chains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

namespace BTCPayServer.Plugins.Solana;

/// <summary>
/// SOL, USDC and USDT on Solana as BTCPay Server payment methods. The plugin only watches the chain. The checkout
/// shows a Solana Pay transfer request: the customer's wallet scans it and pays the store's own wallet, adding the
/// invoice's reference to the transfer, and the listener finds the payment by that reference once it is final. So
/// one address serves any number of open invoices. A wallet in the browser, which cannot scan, pays the same
/// transfer on the plugin's pay page: the server builds it unsigned, the wallet signs and sends it. No keys, no
/// node, no custody.
/// </summary>
public class SolanaPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = "SolanaPlugin";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }];

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        services.AddChainPaymentPlugin<SolanaPlugin>(SolanaChains.Definition(
            bootstrap.GetRequiredService<NBXplorerNetworkProvider>().NetworkType,
            bootstrap.GetRequiredService<IConfiguration>()));
        base.Execute(services);
    }
}
