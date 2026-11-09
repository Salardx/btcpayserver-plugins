using System.Globalization;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.NEAR.Controllers;

/// <summary>What the pay page shows and what its script needs to build the transaction.</summary>
public class NEARPayViewModel
{
    public required string InvoiceId { get; init; }
    public required string PaymentMethodId { get; init; }
    public required string Status { get; init; }
    public required string Destination { get; init; }
    public required decimal Due { get; init; }
    /// <summary>The amount in the asset's base units (yoctoNEAR, or the token's smallest unit), as the transaction carries it.</summary>
    public required string DueUnits { get; init; }
    public required string Currency { get; init; }
    public required string MethodDisplayName { get; init; }
    /// <summary>The NEP-141 contract for a token; null for NEAR itself.</summary>
    public string? TokenContract { get; init; }
    public required int Decimals { get; init; }
    public required string Network { get; init; }
    public required string RpcUrl { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string CheckoutUrl { get; init; }
    public required string StatusUrl { get; init; }
}

/// <summary>
/// The page the checkout sends a customer to: the invoice's amount and the store's account, paid from a NEAR
/// wallet in the browser, with the invoice id as the transfer memo. Public, like the checkout itself.
/// </summary>
[Route("plugins/near/pay")]
[AllowAnonymous]
public class UINEARPayController(InvoiceRepository invoices, ChainPluginContext<NEARPlugin> context) : Controller
{
    [HttpGet("{invoiceId}/{paymentMethodId}")]
    public async Task<IActionResult> Pay(string invoiceId, PaymentMethodId paymentMethodId)
    {
        if (!context.TryGetPaymentMethod(paymentMethodId, out var chain, out var method))
            return NotFound();
        var invoice = await invoices.GetInvoice(invoiceId);
        var prompt = invoice?.GetPaymentPrompt(paymentMethodId);
        if (invoice is null || string.IsNullOrEmpty(prompt?.Destination))
            return NotFound();
        var due = prompt.Calculate().Due;
        return View(new NEARPayViewModel
        {
            InvoiceId = invoice.Id,
            PaymentMethodId = paymentMethodId.ToString(),
            Status = invoice.Status.ToString(),
            Destination = prompt.Destination,
            Due = due,
            DueUnits = ChainPaymentLinkExtension.ToBaseUnits(due, method.Decimals).ToString(CultureInfo.InvariantCulture),
            Currency = method.Currency,
            MethodDisplayName = method.DisplayName,
            TokenContract = method.IsNative ? null : method.TokenHash,
            Decimals = method.Decimals,
            Network = chain.NetworkLabel,
            RpcUrl = chain.JsonRpcUris[0],
            ExpiresAt = invoice.ExpirationTime,
            CheckoutUrl = Url.Action("Checkout", "UIInvoice", new { invoiceId = invoice.Id }) ?? $"/i/{invoice.Id}",
            StatusUrl = Url.Action("GetStatus", "UIInvoice", new { invoiceId = invoice.Id, paymentMethodId = paymentMethodId.ToString() }) ?? $"/i/{invoice.Id}/status",
        });
    }
}
