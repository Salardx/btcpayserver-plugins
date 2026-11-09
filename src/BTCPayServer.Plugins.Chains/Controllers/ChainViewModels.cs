using BTCPayServer.Plugins.Chains.Payments;

namespace BTCPayServer.Plugins.Chains.Controllers;

public class EditChainPaymentMethodViewModel
{
    public required string PluginNavKey { get; init; }
    public required string PluginDisplayName { get; init; }
    public required string DisplayName { get; init; }
    public required string ChainDisplayName { get; init; }
    public required string AddressPlaceholder { get; init; }
    public required string Currency { get; init; }
    public required bool IsEvm { get; init; }
    public bool IsNear { get; init; }
    public string? Address { get; init; }
    public bool Enabled { get; init; }
    public PaymentLinkFormat PaymentLinkFormat { get; init; } = PaymentLinkFormat.Standard;
    public bool NodeAvailable { get; init; }
    public string? NodeError { get; init; }
    public long? LatestBlockOnNode { get; init; }
    public long? LatestBlockScanned { get; init; }
    public AddressViewModel[] Addresses { get; init; } = [];

    public class AddressViewModel
    {
        public required string Value { get; init; }
        public bool Available { get; init; }
        /// <summary>What the chain says about the address, when there is something to say (an unregistered token receiver, for instance).</summary>
        public string? Note { get; init; }
    }
}

/// <summary>What the store settings form posts back.</summary>
public class EditChainPaymentMethodInputModel
{
    public string? Address { get; set; }
    public bool Enabled { get; set; }
    public PaymentLinkFormat PaymentLinkFormat { get; set; } = PaymentLinkFormat.Standard;
}

public class ChainServerConfigViewModel
{
    public string? PluginId { get; set; }
    public string? ChainId { get; set; }
    public string? DisplayName { get; set; }
    public string? DefaultJsonRpcUris { get; set; }
    public string? JsonRpcUri { get; set; }
}
