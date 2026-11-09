using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Chains.Controllers;

/// <summary>
/// The store page of one payment method: the address pool, the enable toggle and the link format. A plugin
/// derives an empty controller from this, adds its route and authorization attributes, and supplies the view
/// under its own controller name.
/// </summary>
public abstract class ChainStoreControllerBase<TPlugin> : Controller where TPlugin : class
{
    T Get<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    StoreData Store => HttpContext.GetStoreData();

    [HttpGet("{paymentMethodId}")]
    public async Task<IActionResult> GetStorePaymentMethod(PaymentMethodId paymentMethodId)
    {
        var context = Get<ChainPluginContext<TPlugin>>();
        if (!context.TryGetPaymentMethod(paymentMethodId, out var chain, out var method))
            return NotFound();
        var handlers = Get<PaymentMethodHandlerDictionary>();
        var excluded = Store.GetStoreBlob().GetExcludedPaymentMethods();
        var saved = Store.GetPaymentMethodConfig<ChainPaymentMethodConfig>(paymentMethodId, handlers);
        var config = saved ?? new ChainPaymentMethodConfig();
        var reserved = await Get<AddressAllocator>().GetUsedAddresses(chain, paymentMethodId, config.Addresses);
        var rpc = Get<ChainRpcProvider<TPlugin>>();
        rpc.Summaries.TryGetValue(chain.Id, out var summary);
        var client = rpc.GetClient(chain.Id);
        var addresses = new List<EditChainPaymentMethodViewModel.AddressViewModel>();
        foreach (var address in config.Addresses)
        {
            addresses.Add(new EditChainPaymentMethodViewModel.AddressViewModel
            {
                Value = address,
                Available = !reserved.Contains(address),
                Note = await DestinationChecks.NoteAsync(client, address, method, HttpContext.RequestAborted),
            });
        }
        return View(new EditChainPaymentMethodViewModel
        {
            PluginNavKey = context.Definition.NavKey,
            PluginDisplayName = context.Definition.DisplayName,
            DisplayName = method.DisplayName,
            ChainDisplayName = chain.DisplayName,
            AddressPlaceholder = chain.AddressPlaceholder,
            Currency = method.Currency,
            IsEvm = chain.IsEvm,
            IsNear = chain.IsNear,
            Address = "",
            Enabled = saved is not null && !excluded.Match(paymentMethodId),
            PaymentLinkFormat = config.PaymentLinkFormat,
            NodeAvailable = summary?.Available is true,
            NodeError = summary?.Error,
            LatestBlockOnNode = summary?.LatestBlockOnNode,
            LatestBlockScanned = summary?.LatestBlockScanned,
            Addresses = addresses.ToArray(),
        });
    }

    [HttpPost("{paymentMethodId}/addresses/{address}/delete")]
    public async Task<IActionResult> DeleteAddress(string storeId, PaymentMethodId paymentMethodId, string address)
    {
        var context = Get<ChainPluginContext<TPlugin>>();
        if (!context.TryGetPaymentMethod(paymentMethodId, out _, out _))
            return NotFound();
        var handlers = Get<PaymentMethodHandlerDictionary>();
        var store = Store;
        var config = store.GetPaymentMethodConfig<ChainPaymentMethodConfig>(paymentMethodId, handlers);
        if (config is null)
            return NotFound();
        config.MarkActivated();
        config.Addresses = config.Addresses.Where(a => !string.Equals(a, address, StringComparison.Ordinal)).ToArray();
        store.SetPaymentMethodConfig(handlers[paymentMethodId], config);
        await Get<StoreRepository>().UpdateStore(store);
        Get<EventAggregator>().Publish(new ChainSettingsChanged { PluginId = context.PluginId });
        TempData.SetStatusMessageModel(new StatusMessageModel { Message = $"The address {address} was removed.", Severity = StatusMessageModel.StatusSeverity.Success });
        return RedirectToAction(nameof(GetStorePaymentMethod), new { storeId, paymentMethodId });
    }

    [HttpPost("{paymentMethodId}")]
    public async Task<IActionResult> GetStorePaymentMethod(EditChainPaymentMethodInputModel input, PaymentMethodId paymentMethodId, string? command = null)
    {
        var context = Get<ChainPluginContext<TPlugin>>();
        if (!context.TryGetPaymentMethod(paymentMethodId, out var chain, out var method))
            return NotFound();
        var handlers = Get<PaymentMethodHandlerDictionary>();
        var store = Store;
        var blob = store.GetStoreBlob();
        var config = store.GetPaymentMethodConfig<ChainPaymentMethodConfig>(paymentMethodId, handlers) ?? new ChainPaymentMethodConfig();

        if (command == "add-addresses" || !string.IsNullOrWhiteSpace(input.Address))
        {
            ModelState.Remove(nameof(input.Enabled));
            ModelState.Remove(nameof(input.PaymentLinkFormat));
            var submitted = (input.Address ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (!AddressRules.TryNormalizeAll(chain, submitted, out var normalized, out var error))
            {
                ModelState.AddModelError(nameof(input.Address), error!);
                return await GetStorePaymentMethod(paymentMethodId);
            }
            var added = normalized.Except(config.Addresses, StringComparer.Ordinal).ToArray();
            if (added.Length == 0)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "No new addresses were added. Enter at least one address that is not in the list yet.",
                    Severity = StatusMessageModel.StatusSeverity.Error,
                });
                return RedirectToAction(nameof(GetStorePaymentMethod), new { storeId = store.Id, paymentMethodId });
            }
            var client = Get<ChainRpcProvider<TPlugin>>().GetClient(chain.Id);
            if (await DestinationChecks.FirstProblemAsync(client, added, method, HttpContext.RequestAborted) is { } problem)
            {
                ModelState.AddModelError(nameof(input.Address), problem);
                return await GetStorePaymentMethod(paymentMethodId);
            }
            config.Addresses = [.. config.Addresses, .. added];
            config.MarkActivated();
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = added.Length == 1 ? $"{added[0]} is now watched for {method.DisplayName}." : $"{added.Length} addresses are now watched for {method.DisplayName}.",
                Severity = StatusMessageModel.StatusSeverity.Success,
            });
        }
        else
        {
            if (!Enum.IsDefined(input.PaymentLinkFormat))
                ModelState.AddModelError(nameof(input.PaymentLinkFormat), "Unknown payment link format.");
            if (!ModelState.IsValid)
                return await GetStorePaymentMethod(paymentMethodId);
            var messages = new List<string>();
            if (input.Enabled)
                config.MarkActivated();
            if (input.Enabled == blob.IsExcluded(paymentMethodId))
            {
                blob.SetExcluded(paymentMethodId, !input.Enabled);
                messages.Add($"{method.DisplayName} is now {(input.Enabled ? "enabled" : "disabled")}");
            }
            if (config.PaymentLinkFormat != input.PaymentLinkFormat)
            {
                config.PaymentLinkFormat = input.PaymentLinkFormat;
                messages.Add("Payment link format updated");
            }
            if (messages.Count > 0)
                TempData.SetStatusMessageModel(new StatusMessageModel { Message = string.Join(". ", messages) + ".", Severity = StatusMessageModel.StatusSeverity.Success });
        }

        store.SetPaymentMethodConfig(handlers[paymentMethodId], config);
        store.SetStoreBlob(blob);
        await Get<StoreRepository>().UpdateStore(store);
        Get<EventAggregator>().Publish(new ChainSettingsChanged { PluginId = context.PluginId });
        return RedirectToAction(nameof(GetStorePaymentMethod), new { storeId = store.Id, paymentMethodId });
    }
}
