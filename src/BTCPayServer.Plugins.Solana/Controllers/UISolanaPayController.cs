using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.Chains.Solana;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NeoPaymentWatcher.Chains;
using NicolasDorier.RateLimits;

namespace BTCPayServer.Plugins.Solana.Controllers;

/// <summary>What the pay page shows and what its script needs.</summary>
public class SolanaPayViewModel
{
    public required string InvoiceId { get; init; }
    public required string PaymentMethodId { get; init; }
    public required string Destination { get; init; }
    public required decimal Due { get; init; }
    public required string Currency { get; init; }
    public required string MethodDisplayName { get; init; }
    /// <summary>The token's mint; null for SOL itself.</summary>
    public string? TokenMint { get; init; }
    /// <summary>"mainnet" or "devnet".</summary>
    public required string Network { get; init; }
    /// <summary>The chain as wallets name it: "solana:mainnet" or "solana:devnet".</summary>
    public required string WalletChain { get; init; }
    /// <summary>The Solana Pay link the checkout's QR code holds, for the wallet app of the phone this page is shown on.</summary>
    public required string WalletLink { get; init; }
    public required string Reference { get; init; }
    /// <summary>Explorer link with <c>{0}</c> for the transaction's signature.</summary>
    public required string ExplorerLink { get; init; }
    public required string CheckoutUrl { get; init; }
    public required string StatusUrl { get; init; }
    public required string TransactionUrl { get; init; }
    public required string SentUrl { get; init; }
}

/// <summary>What the page's script posts: the account of the wallet that is about to pay.</summary>
public class SolanaPayRequest
{
    public string? Account { get; set; }
}

/// <summary>
/// The page the checkout's button opens: the invoice paid from a Solana wallet in the browser. Such a wallet cannot
/// scan the checkout's Solana Pay code, and a transfer typed by hand carries no reference, so the page asks this
/// controller for the invoice's transfer, with the reference in it, built for the connected account; the wallet
/// signs and sends it. Public, like the checkout itself. Nothing here holds a key or sends a transaction.
///
/// Everything here is reachable by anyone who has an invoice's address, so every answer is cheap or rationed: the
/// two actions that spend calls to the store's node are limited per remote address through BTCPay's own rate
/// limiter, under a server-wide ceiling, and the body they accept is small.
/// </summary>
[Route("plugins/solana/pay")]
[AllowAnonymous]
public class UISolanaPayController(
    InvoiceRepository invoices,
    ChainPluginContext<SolanaPlugin> context,
    ChainRpcProvider<SolanaPlugin> rpc,
    ChainStateStore states,
    IRateLimitService rateLimits,
    ILogger<UISolanaPayController> logger) : Controller
{
    /// <summary>
    /// What the whole server may spend on these two actions, however many addresses ask: a ceiling under the
    /// per-address limit, so that a crowd cannot crowd out the listener at a public node.
    /// </summary>
    static readonly PayPageCeiling Ceiling = new(window: TimeSpan.FromSeconds(10), perWindow: 300);

    /// <summary>The account of a wallet is 44 characters at most; nothing here needs a larger body than this.</summary>
    const int MaxBodyBytes = 1024;

    [HttpGet("{invoiceId}/{paymentMethodId}")]
    public async Task<IActionResult> Pay(string invoiceId, PaymentMethodId paymentMethodId)
    {
        if (await Find(invoiceId, paymentMethodId) is not { } found)
            return NotFound();
        var due = found.Prompt.Calculate().Due;
        // A paid or expired invoice is the checkout's to show.
        if (NotPayable(found.Invoice.Status, found.Invoice.ExpirationTime, DateTimeOffset.UtcNow, due) is not null)
            return RedirectToAction("Checkout", "UIInvoice", new { invoiceId = found.Invoice.Id });
        var id = paymentMethodId.ToString();
        return View(new SolanaPayViewModel
        {
            InvoiceId = found.Invoice.Id,
            PaymentMethodId = id,
            Destination = found.Prompt.Destination,
            Due = due,
            Currency = found.Method.Currency,
            MethodDisplayName = found.Method.DisplayName,
            TokenMint = found.Method.IsNative ? null : found.Method.TokenHash,
            Network = found.Chain.NetworkLabel,
            WalletChain = found.Chain.ToChainProfile().NetworkKey,
            WalletLink = ChainPaymentLinkExtension.Build(found.Chain, found.Method, found.Prompt.Destination, due, PaymentLinkFormat.Standard, found.Reference) ?? "",
            Reference = found.Reference,
            ExplorerLink = found.Chain.BlockExplorerLink,
            CheckoutUrl = Url.Action("Checkout", "UIInvoice", new { invoiceId = found.Invoice.Id }) ?? $"/i/{found.Invoice.Id}",
            StatusUrl = Url.Action("GetStatus", "UIInvoice", new { invoiceId = found.Invoice.Id, paymentMethodId = id }) ?? $"/i/{found.Invoice.Id}/status",
            TransactionUrl = Url.Action(nameof(Transaction), new { invoiceId = found.Invoice.Id, paymentMethodId = id }) ?? $"/plugins/solana/pay/{found.Invoice.Id}/{id}/transaction",
            SentUrl = Url.Action(nameof(Sent), new { invoiceId = found.Invoice.Id, paymentMethodId = id }) ?? $"/plugins/solana/pay/{found.Invoice.Id}/{id}/sent",
        });
    }

    /// <summary>
    /// The invoice's transfer for one paying account, unsigned and in Base64, as a Solana Pay transaction request
    /// answers: <c>{"transaction": "..."}</c>, or <c>{"message": "..."}</c> with the reason there is none.
    /// </summary>
    [HttpPost("{invoiceId}/{paymentMethodId}/transaction")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Transaction(string invoiceId, PaymentMethodId paymentMethodId, [FromBody] SolanaPayRequest? request, CancellationToken cancellationToken)
    {
        if (await Find(invoiceId, paymentMethodId) is not { } found)
            return NotFound(new { message = "This invoice has no Solana pay page." });
        var due = found.Prompt.Calculate().Due;
        if (NotPayable(found.Invoice.Status, found.Invoice.ExpirationTime, DateTimeOffset.UtcNow, due) is { } closed)
            return Conflict(new { message = closed });
        if (!SolanaAddress.TryNormalize(request?.Account, out var payer))
            return BadRequest(new { message = "The wallet did not name a Solana account." });
        if (await Rationed(cancellationToken) is { } rationed)
            return rationed;
        if (rpc.GetClient(found.Chain.Id) is not SolanaChainClient client)
            return NotFound(new { message = "This invoice has no Solana pay page." });
        // While the listener is behind, a payment made here would not be seen for as long as that lasts, and the
        // check below for one already on its way would be reading a cursor that says nothing. Build nothing.
        var scannedThrough = rpc.IsAvailable(found.Chain.Id)
            ? (await states.Get(ChainStateStore.ListenerStateKey(found.Chain)))?.LastBlockIndex
            : null;
        if (scannedThrough is not { } cursor)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = $"This store is not watching Solana {found.Chain.NetworkLabel} right now, so a payment would not be seen. Try again in a few minutes." });
        try
        {
            // A payment that is on the chain but not recorded yet: a second one would pay the invoice twice.
            var recorded = found.Invoice.GetPayments(false).Select(payment => payment.Id.Split('-')[0]).ToHashSet(StringComparer.Ordinal);
            if (await client.PendingPaymentAsync(found.Prompt.Destination, found.Reference, found.Method.TokenHash, (uint)cursor, recorded, cancellationToken) is { } pending)
                return Conflict(new { message = "A payment for this invoice was sent already. It is recorded here as soon as the network has finalized it. Do not pay again.", signature = pending });
            var units = ChainPaymentLinkExtension.ToBaseUnits(due, found.Method.Decimals);
            var prepared = await client.PreparePaymentAsync(payer, found.Prompt.Destination, found.Method, units, found.Reference, cancellationToken);
            return prepared.Transaction is { } transaction
                ? Json(new
                {
                    transaction = Convert.ToBase64String(transaction),
                    // What the page was showing may be a moment old: a payment on another method changes what is due.
                    amount = due.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture),
                    currency = found.Method.Currency,
                    lastValidBlockHeight = prepared.LastValidBlockHeight,
                })
                : UnprocessableEntity(new { message = prepared.Refusal });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException && !cancellationToken.IsCancellationRequested)
        {
            // The node is unreachable, throttling, or answered something this cannot use.
            logger.LogWarning("{Chain}: no payment could be prepared for invoice {Invoice}: {Error}", found.Chain.DisplayName, found.Invoice.Id, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The Solana node did not answer. Try again in a moment." });
        }
    }

    /// <summary>
    /// Where a transaction this page handed out stands: <c>{"state": "onItsWay" | "paid" | "failed" | "gone"}</c>.
    /// A wallet can drop a transaction without telling the page, and a blockhash goes stale, so the page asks here
    /// instead of leaving a customer with a payment that is never going to arrive.
    /// </summary>
    [HttpGet("{invoiceId}/{paymentMethodId}/sent")]
    public async Task<IActionResult> Sent(string invoiceId, PaymentMethodId paymentMethodId, string? signature, ulong lastValidBlockHeight, CancellationToken cancellationToken)
    {
        if (await Find(invoiceId, paymentMethodId) is not { } found)
            return NotFound(new { message = "This invoice has no Solana pay page." });
        // 64 bytes in Base58: 64 characters if every byte were zero, 88 at the most. Length first, because decoding
        // costs time squared in it and this is a public address.
        if (signature is not { Length: >= 64 and <= 88 } || !Base58.TryDecode(signature, out var raw) || raw.Length != 64)
            return BadRequest(new { message = "That is not a transaction signature." });
        if (await Rationed(cancellationToken) is { } rationed)
            return rationed;
        if (rpc.GetClient(found.Chain.Id) is not SolanaChainClient client)
            return NotFound(new { message = "This invoice has no Solana pay page." });
        try
        {
            var state = await client.SignatureStateAsync(signature, lastValidBlockHeight, cancellationToken);
            return Json(new
            {
                state = state switch
                {
                    SolanaSignatureState.Paid => "paid",
                    SolanaSignatureState.Failed => "failed",
                    SolanaSignatureState.Gone => "gone",
                    _ => "onItsWay",
                },
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Chain}: the state of {Signature} could not be read: {Error}", found.Chain.DisplayName, signature, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The Solana node did not answer. Try again in a moment." });
        }
    }

    /// <summary>
    /// The answer to send when this caller has had its share, or null to go on. Per remote address through BTCPay's
    /// own limiter, in the zone it uses for its public invoice pages, under a ceiling for the whole server.
    /// </summary>
    async Task<IActionResult?> Rationed(CancellationToken cancellationToken)
    {
        var tooMany = (IActionResult)StatusCode(StatusCodes.Status429TooManyRequests,
            new { message = "Too many payments are being prepared right now. Try again in a few seconds." });
        if (HttpContext.Connection is { RemoteIpAddress: { } address }
            && !await rateLimits.Throttle(ZoneLimits.PublicInvoices, address.ToString(), cancellationToken))
            return tooMany;
        return Ceiling.TryEnter(DateTimeOffset.UtcNow) ? null : tooMany;
    }

    /// <summary>Why an invoice takes no payment any more, in words for the customer; null while it does.</summary>
    internal static string? NotPayable(InvoiceStatus status, DateTimeOffset expires, DateTimeOffset now, decimal due) => status switch
    {
        InvoiceStatus.Settled or InvoiceStatus.Processing => "This invoice is paid already.",
        InvoiceStatus.Expired => "This invoice has expired. Go back to the store for a new one.",
        InvoiceStatus.Invalid => "This invoice is not valid any more. Go back to the store for a new one.",
        _ when expires <= now => "This invoice has expired. Go back to the store for a new one.",
        _ when due <= 0 => "Nothing is left to pay on this invoice.",
        _ => null,
    };

    sealed record Found(InvoiceEntity Invoice, PaymentPrompt Prompt, ChainDefinition Chain, PaymentMethodDefinition Method, string Reference);

    /// <summary>The invoice's prompt for one of this plugin's methods, if it carries a reference: the page exists for those alone.</summary>
    async Task<Found?> Find(string invoiceId, PaymentMethodId paymentMethodId)
    {
        if (!context.TryGetPaymentMethod(paymentMethodId, out var chain, out var method))
            return null;
        var invoice = await invoices.GetInvoice(invoiceId);
        var prompt = invoice?.GetPaymentPrompt(paymentMethodId);
        if (invoice is null || invoice.Archived || prompt is null || string.IsNullOrEmpty(prompt.Destination)
            || ChainPaymentLinkExtension.ReadFormat(prompt.Details) != PaymentLinkFormat.Standard
            || ChainPaymentLinkExtension.ReadReference(prompt.Details) is not { } reference)
            return null;
        return new Found(invoice, prompt, chain, method, reference);
    }
}

/// <summary>
/// What the whole server may spend on the pay page in a window of time, above whatever each caller is allowed.
/// Keyless on purpose: a limit per invoice would let anyone holding one invoice's address hold up that invoice.
/// </summary>
internal sealed class PayPageCeiling(TimeSpan window, int perWindow)
{
    readonly object gate = new();
    DateTimeOffset windowStart;
    int entered;

    public bool TryEnter(DateTimeOffset now)
    {
        lock (gate)
        {
            if (now - windowStart >= window)
            {
                windowStart = now;
                entered = 0;
            }
            if (entered >= perWindow)
                return false;
            entered++;
            return true;
        }
    }
}
