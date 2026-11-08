using System.Globalization;
using System.Numerics;
using NeoPaymentWatcher.Services;

namespace NeoPaymentWatcher.Chains;

/// <summary>Payment links wallets turn into a prefilled transfer: NEP-9 on Neo N3, EIP-681 on EVM chains.</summary>
public static class PaymentUris
{
    public static string Build(ChainProfile chain, string address, TokenInfo token, BigInteger units, string amount)
    {
        if (!chain.IsEvm)
        {
            var asset = token.Hash switch
            {
                TokenRegistry.NeoHash => "neo",
                TokenRegistry.GasHash => "gas",
                _ => token.Hash,
            };
            return $"neo:{address}?asset={asset}&amount={amount}";
        }

        var chainId = chain.ChainId!.Value.ToString(CultureInfo.InvariantCulture);
        var value = units.ToString(CultureInfo.InvariantCulture);
        return token.Hash == TokenRegistry.Native
            ? $"ethereum:{address}@{chainId}?value={value}"
            : $"ethereum:{token.Hash}@{chainId}/transfer?address={address}&uint256={value}";
    }
}
