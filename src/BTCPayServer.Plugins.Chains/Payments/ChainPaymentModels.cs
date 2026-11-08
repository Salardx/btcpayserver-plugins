namespace BTCPayServer.Plugins.Chains.Payments;

public enum PaymentLinkFormat
{
    /// <summary>NEP-9 on Neo N3, EIP-681 on EVM chains: wallets that understand it prefill the amount.</summary>
    Standard = 0,
    /// <summary>The bare address; the customer types the amount.</summary>
    AddressOnly = 2,
}

/// <summary>Per-store settings of one payment method, kept in the store's payment method config.</summary>
public class ChainPaymentMethodConfig
{
    public string[] Addresses { get; set; } = [];
    public bool Activated { get; set; }
    public PaymentLinkFormat PaymentLinkFormat { get; set; } = PaymentLinkFormat.Standard;

    public void MarkActivated() => Activated = true;

    public void PreserveActivationFrom(ChainPaymentMethodConfig? previous)
    {
        if (WasConfiguredOrActivated() || previous?.WasConfiguredOrActivated() is true)
            MarkActivated();
    }

    public bool ActivatesChain(bool excluded) => WasConfiguredOrActivated() || !excluded;

    bool WasConfiguredOrActivated() => Activated || Addresses is { Length: > 0 };
}

/// <summary>What an invoice's payment prompt remembers.</summary>
public class ChainPaymentPromptDetails
{
    public PaymentLinkFormat PaymentLinkFormat { get; set; } = PaymentLinkFormat.Standard;
}

/// <summary>What a recorded payment remembers. Payments are recorded from final blocks only, so the count never needs to grow.</summary>
public class ChainPaymentData
{
    public required string TransactionId { get; init; }
    public int NotifyIndex { get; init; }
    public uint BlockIndex { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public int ConfirmationCount { get; set; } = 1;
}
