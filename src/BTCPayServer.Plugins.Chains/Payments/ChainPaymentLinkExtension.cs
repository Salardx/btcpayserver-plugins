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

/// <summary>
/// The link behind the QR code: NEP-9 on Neo N3, EIP-681 on EVM chains (with the token's transfer call for tokens),
/// the plugin's pay page on NEAR (which has no wallet link standard), or the bare address.
/// </summary>
public class ChainPaymentLinkExtension(PaymentMethodId paymentMethodId, ChainDefinition chain, PaymentMethodDefinition method, PayPageRoute? payPage = null) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    public ChainDefinition Chain { get; } = chain;

    public PaymentMethodDefinition Method { get; } = method;

    public PayPageRoute? PayPage { get; } = payPage;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        var format = ReadFormat(prompt.Details);
        if (format == PaymentLinkFormat.Standard && PayPage is not null && urlHelper is not null && prompt.ParentEntity?.Id is { Length: > 0 } invoiceId)
        {
            var request = urlHelper.ActionContext.HttpContext.Request;
            var link = urlHelper.Action(PayPage.Action, PayPage.Controller, new { invoiceId, paymentMethodId = PaymentMethodId.ToString() }, request.Scheme, request.Host.ToUriComponent());
            if (!string.IsNullOrEmpty(link))
                return link;
        }
        return Build(Chain, Method, prompt.Destination, prompt.Calculate().Due, format);
    }

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

    /// <summary>The wallet link for the chain; on NEAR, which has none, the account id itself.</summary>
    public static string? Build(ChainDefinition chain, PaymentMethodDefinition method, string? destination, decimal due, PaymentLinkFormat format)
    {
        if (string.IsNullOrEmpty(destination))
            return null;
        if (format == PaymentLinkFormat.AddressOnly || chain.IsNear)
            return destination;
        var amount = due.ToString(CultureInfo.InvariantCulture);
        var units = ToBaseUnits(due, method.Decimals);
        var token = new TokenInfo(method.TokenHash, method.Currency, method.Decimals);
        return PaymentUris.Build(chain.ToChainProfile(), destination, token, units, amount);
    }

    public static BigInteger ToBaseUnits(decimal due, int decimals)
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
        var link = paymentLink.GetPaymentLink(context.Prompt, context.UrlHelper);
        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        context.Model.InvoiceBitcoinUrl = link;
        context.Model.InvoiceBitcoinUrlQR = link;
        // A wallet link is opened by scanning the QR code; a pay page is a web address, so the button that opens it stays.
        context.Model.ShowPayInWalletButton = link is not null && link.StartsWith("http", StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// On NEAR the id in a payment id is a transaction hash or a receipt id, both base58, or "account@block" when the
/// transfer was not in the chunks; only the first two get an explorer link. Account ids may contain dashes, so the
/// id is cut from the right, by the index and the method suffix, not by the first dash.
/// </summary>
public class NearTransactionLinkProvider(string blockExplorerLink, PaymentMethodId paymentMethodId) : DefaultTransactionLinkProvider(blockExplorerLink)
{
    public override string? GetTransactionLink(string paymentId)
    {
        if (string.IsNullOrEmpty(BlockExplorerLink))
            return null;
        var suffix = "-" + paymentMethodId;
        if (!paymentId.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var rest = paymentId[..^suffix.Length];
        var cut = rest.LastIndexOf('-');
        if (cut <= 0)
            return null;
        var id = rest[..cut].TrimEnd('-');
        if (id.Length == 0 || id.Contains('@'))
            return null;
        return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, id);
    }
}
