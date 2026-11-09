using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Chains.Near;
using Microsoft.Extensions.Configuration;
using NeoPaymentWatcher.Chains.Evm;
using NeoPaymentWatcher.Neo;

namespace BTCPayServer.Plugins.Chains;

/// <summary>Configuration and server-side overrides applied on top of a plugin's built-in chain definitions.</summary>
public static class ChainDefaults
{
    /// <summary>
    /// <c>{CHAIN}_JSONRPC_URI</c> replaces the default nodes and <c>{CHAIN}_{CURRENCY}_CONTRACT</c> sets or replaces a
    /// token contract. A token without a contract on this network is dropped, so a plugin can offer a token on
    /// mainnet only and still let a testnet operator supply one.
    /// </summary>
    public static ChainDefinition ApplyConfiguration(ChainDefinition chain, IConfiguration configuration)
    {
        var methods = new List<PaymentMethodDefinition>();
        foreach (var method in chain.PaymentMethods)
        {
            var resolved = method;
            if (!method.IsNative)
            {
                var contract = configuration.GetOrDefault<string?>($"{chain.Id}_{method.Currency}_CONTRACT", null);
                if (!string.IsNullOrWhiteSpace(contract))
                    resolved = method with { TokenHash = NormalizeContract(chain, method.Currency, contract) };
            }
            if (resolved.TokenHash.Length > 0)
                methods.Add(resolved);
        }
        return chain with
        {
            JsonRpcUris = Override(configuration.GetOrDefault<string?>($"{chain.Id}_JSONRPC_URI", null), chain.JsonRpcUris),
            PaymentMethods = methods,
        };
    }

    static string NormalizeContract(ChainDefinition chain, string currency, string contract)
    {
        string hash;
        var ok = chain.IsEvm
            ? EvmHex.TryNormalizeAddress(contract, out hash)
            : chain.IsNear
                ? NearAccountId.TryNormalize(contract, out hash)
                : NeoAddress.TryNormalizeContractHash(contract, out hash);
        if (!ok)
            throw new InvalidOperationException($"{chain.Id}_{currency}_CONTRACT is not a valid {chain.DisplayName} contract address: {contract}");
        return hash;
    }

    public static string[] Override(string? uri, string[] defaults) => string.IsNullOrWhiteSpace(uri) ? defaults : [uri.Trim()];

    public static string ServerSettingsKey(ChainDefinition chain) => $"Chains.{chain.Id}.Server";

    public static async Task<ChainDefinition> OverrideWithServerSettingsAsync(ChainDefinition chain, ISettingsRepository settings)
    {
        var saved = await settings.GetSettingAsync<ChainServerSettings>(ServerSettingsKey(chain));
        return chain with { JsonRpcUris = Override(saved?.JsonRpcUri, chain.JsonRpcUris) };
    }

    /// <summary>An embedded SVG as a data URI, for the checkout's payment method icon.</summary>
    public static string EmbeddedImage(Assembly assembly, string resourceName)
    {
        using var icon = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource {resourceName} is missing.");
        using var buffer = new MemoryStream();
        icon.CopyTo(buffer);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(buffer.ToArray());
    }
}
