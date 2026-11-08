using System.Globalization;
using System.Numerics;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Services.Invoices;
using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>
/// Turns a transfer the listener saw into a BTCPay payment. The token names the payment method; the invoice is the
/// one holding the address at the block's time. The listener only hands over final blocks, so a payment is settled
/// the moment it is recorded: there is no confirmation count to grow and no reorganisation to undo.
/// </summary>
public sealed class PaymentRecorder(
    AddressAllocator allocator,
    InvoiceRepository invoices,
    PaymentMethodHandlerDictionary handlers,
    PaymentService payments,
    EventAggregator events,
    ILogger<PaymentRecorder> logger)
{
    public async Task RecordAsync(ChainDefinition chain, TokenTransfer transfer, CancellationToken cancellationToken)
    {
        var method = chain.FindByToken(transfer.TokenHash);
        if (method is null)
            return;
        var pmi = chain.PaymentMethodId(method);
        if (!handlers.TryGetValue(pmi, out var handler))
            return;
        var when = DateTimeOffset.FromUnixTimeMilliseconds(transfer.BlockTime);
        if (!AddressRules.TryNormalize(chain, transfer.To, out var address, out var key))
            return;
        var invoice = await ResolveInvoice(pmi, address, key, when);
        if (invoice is null)
        {
            logger.LogInformation("{Chain}: transfer {Tx} to {Address} matches no open invoice; ignored", pmi, transfer.TxId, address);
            return;
        }

        var id = PaymentId(pmi, transfer);
        if (invoice.GetPayments(false).Any(p => p.Id == id))
            return;

        var details = new ChainPaymentData
        {
            TransactionId = transfer.TxId,
            NotifyIndex = transfer.NotifyIndex,
            BlockIndex = transfer.BlockIndex,
            From = transfer.From,
            To = address,
            ConfirmationCount = chain.FinalityBlocks + 1,
        };
        var data = new PaymentData
        {
            Id = id,
            InvoiceDataId = invoice.Id,
            Amount = ToDecimal(transfer.Amount, method.Decimals),
            Currency = method.Currency,
            Created = when,
            Status = PaymentStatus.Settled,
        }.Set(invoice, handler, details);

        var payment = await payments.AddPayment(data, [transfer.TxId]);
        if (payment is null)
            return;
        logger.LogInformation("{Chain}: {Amount} {Currency} for invoice {Invoice} in block {Block} ({Tx})",
            pmi, data.Amount, method.Currency, invoice.Id, transfer.BlockIndex, transfer.TxId);
        events.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        events.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }

    /// <summary>The invoice holding this address, if the block falls inside its payment window.</summary>
    async Task<InvoiceEntity?> ResolveInvoice(PaymentMethodId pmi, string address, string key, DateTimeOffset when)
    {
        var claim = await allocator.CurrentClaim(pmi, key);
        if (claim is null)
            return null;
        var invoice = await invoices.GetInvoice(claim.InvoiceId);
        if (invoice is null)
            return null;
        var prompt = invoice.GetPaymentPrompt(pmi);
        if (prompt?.Destination is null || !string.Equals(prompt.Destination, address, StringComparison.Ordinal))
            return null;
        if (!AddressAllocator.ShouldReserve(invoice.Status, invoice.MonitoringExpiration + AddressAllocator.ReuseGrace, when))
            return null;
        return when.ToUnixTimeSeconds() >= invoice.InvoiceTime.ToUnixTimeSeconds() ? invoice : null;
    }

    /// <summary>Unique per transfer: the transaction, the notification or log index within it, and the payment method.</summary>
    public static string PaymentId(PaymentMethodId pmi, TokenTransfer transfer) =>
        $"{transfer.TxId}-{transfer.NotifyIndex.ToString(CultureInfo.InvariantCulture)}-{pmi}";

    public static decimal ToDecimal(BigInteger units, int decimals) =>
        decimal.Parse(TokenAmount.Format(units, decimals), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
