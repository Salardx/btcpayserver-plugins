using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>What the chain says about an address a merchant wants to receive on. A usable address may still carry a note worth showing.</summary>
public sealed record DestinationCheck(bool Usable, string? Note)
{
    public static readonly DestinationCheck Fine = new(true, null);
}

/// <summary>
/// A chain client that can tell whether an address can receive a payment method before the merchant saves it:
/// on NEAR a named account must exist, and a token contract may require the account to be registered first.
/// </summary>
public interface IDestinationChecker
{
    Task<DestinationCheck> CheckAsync(string address, PaymentMethodDefinition method, CancellationToken ct);
}

public static class DestinationChecks
{
    /// <summary>The first address the chain refuses, with the reason; null when all are fine or the node cannot be asked right now.</summary>
    public static async Task<string?> FirstProblemAsync(IChainClient client, IEnumerable<string> addresses, PaymentMethodDefinition method, CancellationToken ct)
    {
        if (client is not IDestinationChecker checker)
            return null;
        foreach (var address in addresses)
        {
            DestinationCheck check;
            try
            {
                check = await checker.CheckAsync(address, method, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The node is unreachable or throttling: the health summary reports that; it must not block the merchant.
                return null;
            }
            if (!check.Usable)
                return check.Note;
        }
        return null;
    }

    /// <summary>The note for one address, if the chain has one; null when there is nothing to say or the node cannot be asked.</summary>
    public static async Task<string?> NoteAsync(IChainClient client, string address, PaymentMethodDefinition method, CancellationToken ct)
    {
        if (client is not IDestinationChecker checker)
            return null;
        try
        {
            return (await checker.CheckAsync(address, method, ct)).Note;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
