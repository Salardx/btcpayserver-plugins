using BTCPayServer.Plugins.Chains.Near;
using BTCPayServer.Plugins.Chains.Solana;
using NeoPaymentWatcher.Chains.Evm;
using NeoPaymentWatcher.Neo;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>
/// Address checks per chain, without a node. Neo N3 addresses are Base58Check with the N3 version byte; EVM
/// addresses are 20-byte hex, and a mixed-case one must carry a correct EIP-55 checksum; NEAR addresses are
/// account ids; Solana addresses are 32 bytes of Base58. The canonical form is what is stored and shown; the key is
/// what notifications and logs are matched on.
/// </summary>
public static class AddressRules
{
    const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    public static bool TryNormalize(ChainDefinition chain, string? input, out string canonical, out string key)
    {
        canonical = "";
        key = "";
        var text = input?.Trim() ?? "";
        if (chain.IsEvm)
        {
            if (!EvmHex.TryNormalizeAddress(text, out canonical) || canonical == ZeroAddress)
                return false;
            key = canonical;
            return true;
        }
        if (chain.IsNear)
        {
            if (!NearAccountId.TryNormalize(text, out canonical))
                return false;
            key = canonical;
            return true;
        }
        if (chain.IsSolana)
        {
            if (!SolanaAddress.TryNormalize(text, out canonical))
                return false;
            key = canonical;
            return true;
        }
        if (!NeoAddress.TryGetScriptHash(text, out var hash))
            return false;
        canonical = text;
        key = Convert.ToHexStringLower(hash);
        return true;
    }

    /// <summary>
    /// What a transfer names as its receiver: an address, or on a chain that matches by reference an address together
    /// with the reference of the invoice sharing it. The canonical form is the address; the key is what the invoice's
    /// claim is stored under.
    /// </summary>
    public static bool TryParseReceiver(ChainDefinition chain, string? receiver, out string canonical, out string key)
    {
        if (chain.MatchesByReference && SolanaAddress.TrySplitSharedKey(receiver, out var address, out _))
        {
            canonical = address;
            key = receiver!;
            return true;
        }
        return TryNormalize(chain, receiver, out canonical, out key);
    }

    /// <summary>Validates a list the merchant typed or pasted: every entry valid, no duplicates.</summary>
    public static bool TryNormalizeAll(ChainDefinition chain, IEnumerable<string?>? addresses, out string[] normalized, out string? error)
    {
        normalized = [];
        error = null;
        if (addresses is null)
        {
            error = "Addresses must be a list.";
            return false;
        }
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in addresses)
        {
            if (!TryNormalize(chain, value, out var canonical, out var key))
            {
                error = chain.IsEvm
                    ? $"Address {result.Count + 1} is not a valid {chain.DisplayName} address. A mixed-case address must have a correct checksum, so check it for typing errors."
                    : chain.IsNear
                        ? $"Address {result.Count + 1} is not a NEAR account id: 2 to 64 characters of lowercase letters, digits, - and _ in parts separated by dots (shop.near), or the 64 hex digits of an implicit account."
                        : chain.IsSolana
                            ? $"Address {result.Count + 1} is not a Solana address: 32 to 44 characters of Base58, as a wallet shows its own address."
                            : $"Address {result.Count + 1} is not a valid {chain.DisplayName} address.";
                return false;
            }
            if (!seen.Add(key))
            {
                error = $"Duplicate address: {canonical}.";
                return false;
            }
            result.Add(canonical);
        }
        normalized = result.ToArray();
        return true;
    }
}
