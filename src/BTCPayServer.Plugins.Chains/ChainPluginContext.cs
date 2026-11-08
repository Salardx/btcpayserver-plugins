using System.Diagnostics.CodeAnalysis;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Chains;

/// <summary>
/// The chains of one plugin as they are right now: the built-in definitions with the server's node overrides
/// applied. Generic on the plugin type, so that two plugins of this family can run in one BTCPay Server with their
/// own services, even when the test runner loads them into the same assembly load context.
/// </summary>
public sealed class ChainPluginContext<TPlugin> where TPlugin : class
{
    readonly object gate = new();
    ChainDefinition[] chains;

    public ChainPluginContext(ChainPluginDefinition definition)
    {
        Definition = definition;
        chains = definition.Chains.ToArray();
        if (chains.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != chains.Length)
            throw new ArgumentException("Chain ids must be unique within a plugin.", nameof(definition));
    }

    public ChainPluginDefinition Definition { get; }

    public string PluginId => Definition.Id;

    /// <summary>The chains with the current node settings.</summary>
    public IReadOnlyList<ChainDefinition> Chains => Volatile.Read(ref chains);

    public IEnumerable<(ChainDefinition Chain, PaymentMethodDefinition Method)> PaymentMethods =>
        Chains.SelectMany(chain => chain.PaymentMethods.Select(method => (chain, method)));

    public bool TryGetChain(string? id, [NotNullWhen(true)] out ChainDefinition? chain)
    {
        chain = Chains.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        return chain is not null;
    }

    public bool TryGetPaymentMethod(PaymentMethodId paymentMethodId,
        [NotNullWhen(true)] out ChainDefinition? chain, [NotNullWhen(true)] out PaymentMethodDefinition? method)
    {
        foreach (var (c, m) in PaymentMethods)
        {
            if (c.PaymentMethodId(m) == paymentMethodId)
            {
                chain = c;
                method = m;
                return true;
            }
        }
        chain = null;
        method = null;
        return false;
    }

    /// <summary>The built-in definition of a chain, before any server override.</summary>
    public ChainDefinition Default(string chainId) =>
        Definition.Chains.First(c => string.Equals(c.Id, chainId, StringComparison.Ordinal));

    public void Replace(ChainDefinition chain)
    {
        lock (gate)
        {
            var updated = chains.Select(c => string.Equals(c.Id, chain.Id, StringComparison.Ordinal) ? chain : c).ToArray();
            Volatile.Write(ref chains, updated);
        }
    }
}
