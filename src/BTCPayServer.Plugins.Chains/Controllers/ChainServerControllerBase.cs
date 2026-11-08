using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Chains.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Chains.Controllers;

/// <summary>The server page of one chain: the JSON-RPC endpoint override. Derived per plugin like <see cref="ChainStoreControllerBase{TPlugin}"/>.</summary>
public abstract class ChainServerControllerBase<TPlugin> : Controller where TPlugin : class
{
    T Get<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    [HttpGet]
    public async Task<IActionResult> GetServerConfig(string chainId)
    {
        var context = Get<ChainPluginContext<TPlugin>>();
        if (!context.TryGetChain(chainId, out var chain))
            return NotFound();
        var defaults = context.Default(chain.Id);
        var saved = await Get<ISettingsRepository>().GetSettingAsync<ChainServerSettings>(ChainDefaults.ServerSettingsKey(chain));
        return View(new ChainServerConfigViewModel
        {
            PluginId = context.PluginId,
            ChainId = chain.Id,
            DisplayName = chain.DisplayName,
            DefaultJsonRpcUris = string.Join(", ", defaults.JsonRpcUris),
            JsonRpcUri = saved?.JsonRpcUri,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GetServerConfig(ChainServerConfigViewModel model, string chainId)
    {
        var context = Get<ChainPluginContext<TPlugin>>();
        if (!context.TryGetChain(chainId, out var chain))
            return NotFound();
        var defaults = context.Default(chain.Id);
        var uri = model.JsonRpcUri?.Trim();
        if (!string.IsNullOrEmpty(uri) && !(Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
            ModelState.AddModelError(nameof(model.JsonRpcUri), "Enter an absolute http or https URL, or leave the field empty for the default nodes.");
        if (!ModelState.IsValid)
        {
            model.PluginId = context.PluginId;
            model.ChainId = chain.Id;
            model.DisplayName = chain.DisplayName;
            model.DefaultJsonRpcUris = string.Join(", ", defaults.JsonRpcUris);
            return View(model);
        }
        var settings = Get<ISettingsRepository>();
        await settings.UpdateSetting(new ChainServerSettings { JsonRpcUri = string.IsNullOrEmpty(uri) ? null : uri }, ChainDefaults.ServerSettingsKey(chain));
        context.Replace(await ChainDefaults.OverrideWithServerSettingsAsync(defaults, settings));
        Get<EventAggregator>().Publish(new ChainSettingsChanged { PluginId = context.PluginId });
        return RedirectToAction(nameof(GetServerConfig), new { chainId });
    }
}
