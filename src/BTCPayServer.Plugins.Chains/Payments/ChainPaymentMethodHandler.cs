using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Services.Rates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Chains.Payments;

/// <summary>What the views need from any handler of this family, whichever plugin registered it.</summary>
public interface IChainPaymentMethodHandler : IPaymentMethodHandler
{
    string PluginId { get; }
    ChainDefinition Chain { get; }
    PaymentMethodDefinition Method { get; }
    new ChainPaymentData ParsePaymentDetails(JToken details);
}

public class ChainPaymentMethodHandler<TPlugin>(
    ChainPluginContext<TPlugin> pluginContext,
    ChainDefinition chain,
    PaymentMethodDefinition method,
    ChainRpcProvider<TPlugin> rpc,
    AddressAllocator allocator) : IChainPaymentMethodHandler where TPlugin : class
{
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public PaymentMethodId PaymentMethodId { get; } = chain.PaymentMethodId(method);

    public string PluginId { get; } = pluginContext.PluginId;

    public ChainDefinition Chain { get; } = chain;

    public PaymentMethodDefinition Method { get; } = method;

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = Method.Currency;
        context.Prompt.Divisibility = Method.Divisibility;
        context.Prompt.RateDivisibility = null;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (!rpc.IsAvailable(Chain.Id))
            throw new PaymentMethodUnavailableException($"The {Chain.DisplayName} node is not available or the listener is behind.");
        var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
        var address = await allocator.Allocate(Chain, PaymentMethodId, context.Store.Id, context.InvoiceEntity.Id, config.Addresses,
                          context.InvoiceEntity.InvoiceTime, context.InvoiceEntity.MonitoringExpiration)
                      ?? throw new PaymentMethodUnavailableException(
                          $"Every {Method.DisplayName} address of this store is reserved by an open invoice. Add addresses, or wait for one to free up.");
        context.Prompt.Destination = address;
        context.Prompt.PaymentMethodFee = 0;
        context.Prompt.Details = JObject.FromObject(new ChainPaymentPromptDetails { PaymentLinkFormat = config.PaymentLinkFormat }, Serializer);
    }

    /// <summary>Every address must be well formed and, where the chain can say so, able to receive this method.</summary>
    public async Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var config = ParsePaymentMethodConfig(validationContext.Config);
        if (!AddressRules.TryNormalizeAll(Chain, config.Addresses, out var normalized, out var error))
        {
            validationContext.ModelState.AddModelError(nameof(config.Addresses), error!);
        }
        else
        {
            config.Addresses = normalized;
            if (await DestinationChecks.FirstProblemAsync(rpc.GetClient(Chain.Id), normalized, Method, CancellationToken.None) is { } problem)
                validationContext.ModelState.AddModelError(nameof(config.Addresses), problem);
        }
        if (!Enum.IsDefined(config.PaymentLinkFormat))
            validationContext.ModelState.AddModelError(nameof(config.PaymentLinkFormat), "Unknown payment link format.");
        var previous = validationContext.PreviousConfig is null ? null : ParsePaymentMethodConfig(validationContext.PreviousConfig);
        config.PreserveActivationFrom(previous);
        validationContext.Config = JToken.FromObject(config, Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);

    public ChainPaymentMethodConfig ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<ChainPaymentMethodConfig>(Serializer) ?? throw new FormatException($"Invalid {nameof(ChainPaymentMethodConfig)}");

    public ChainPaymentPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<ChainPaymentPromptDetails>(Serializer) ?? new ChainPaymentPromptDetails();

    public ChainPaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<ChainPaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(ChainPaymentData)}");
}
