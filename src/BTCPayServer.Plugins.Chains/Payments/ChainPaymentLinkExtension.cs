using System.Globalization;
using System.Numerics;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using NeoPaymentWatcher.Chains;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Chains.Payments;

/// <summary>The link behind the QR code: NEP-9 on Neo N3, EIP-681 on EVM chains (with the token's transfer call for tokens), or the bare address.</summary>
public class ChainPaymentLinkExtension(PaymentMethodId paymentMethodId, ChainDefinition chain, PaymentMethodDefinition method) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    public ChainDefinition Chain { get; } = chain;

    public PaymentMethodDefinition Method { get; } = method;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) =>
        Build(Chain, Method, prompt.Destination, prompt.Calculate().Due, ReadFormat(prompt.Details));

    /// <summary>The format stored with the prompt, as a number or a name; anything unreadable means the default.</summary>
    internal static PaymentLinkFormat ReadFormat(JToken? details)
    {
        try
        {
            var format = details?["paymentLinkFormat"]?.ToObject<PaymentLinkFormat?>();
            return format is { } value && Enum.IsDefined(value) ? value : PaymentLinkFormat.Standard;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return PaymentLinkFormat.Standard;
        }
    }

    public static string? Build(ChainDefinition chain, PaymentMethodDefinition method, string? destination, decimal due, PaymentLinkFormat format)
    {
        if (string.IsNullOrEmpty(destination))
            return null;
        if (format == PaymentLinkFormat.AddressOnly)
            return destination;
        var amount = due.ToString(CultureInfo.InvariantCulture);
        var units = ToBaseUnits(due, method.Decimals);
        var token = new TokenInfo(method.TokenHash, method.Currency, method.Decimals);
        return PaymentUris.Build(chain.ToChainProfile(), destination, token, units, amount);
    }

    internal static BigInteger ToBaseUnits(decimal due, int decimals)
    {
        if (due <= 0)
            return BigInteger.Zero;
        var bits = decimal.GetBits(due);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        var scale = (bits[3] >> 16) & 0xff;
        return coefficient * BigInteger.Pow(10, decimals) / BigInteger.Pow(10, scale);
    }
}

/// <summary>Reuses BTCPay's Bitcoin checkout body: amount, address, QR code of the payment link.</summary>
public class ChainCheckoutModelExtension(
    PaymentMethodId paymentMethodId,
    string image,
    string currencyDisplayName,
    IEnumerable<IPaymentLinkExtension> paymentLinkExtensions) : ICheckoutModelExtension
{
    readonly IPaymentLinkExtension paymentLink = paymentLinkExtensions.Single(p => p.PaymentMethodId == paymentMethodId);

    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;
    public string Image => image;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler?.PaymentMethodId != PaymentMethodId)
            return;
        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        context.Model.InvoiceBitcoinUrl = paymentLink.GetPaymentLink(context.Prompt, context.UrlHelper);
        context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl;
        context.Model.ShowPayInWalletButton = false;
        context.Model.PaymentMethodCurrency = currencyDisplayName;
    }
}

/// <summary>Payment ids are "txid-index-method"; the explorer wants the transaction id.</summary>
public class ChainTransactionLinkProvider(string blockExplorerLink) : DefaultTransactionLinkProvider(blockExplorerLink)
{
    public override string? GetTransactionLink(string paymentId)
    {
        if (string.IsNullOrEmpty(BlockExplorerLink))
            return null;
        var txId = paymentId.Split('-')[0];
        return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, txId);
    }
}
