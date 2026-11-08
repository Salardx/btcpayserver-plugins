using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>
/// Hands each invoice one of the store's addresses and keeps it reserved while the invoice can still be paid, so
/// two open invoices never share an address. Reservations live in BTCPay's settings table, per payment method;
/// allocation and release run under one advisory lock per payment method.
/// </summary>
public sealed class AddressAllocator(ApplicationDbContextFactory factory)
{
    internal sealed record Claim(string StoreId, string InvoiceId, string Address, string Key,
        DateTimeOffset? CreatedAt = null, DateTimeOffset? MonitoringExpiration = null);

    /// <summary>An expired invoice keeps its address a while longer, so a transfer made just before the deadline is still matched before the address is handed on.</summary>
    internal static readonly TimeSpan ReuseGrace = TimeSpan.FromMinutes(15);

    static string AddressKey(PaymentMethodId pmi, string key) => $"Chains.{pmi}.Address.{key}";
    static string InvoiceKey(PaymentMethodId pmi, string invoiceId) => $"Chains.{pmi}.Invoice.{invoiceId}";
    static string Prefix(PaymentMethodId pmi) => $"Chains.{pmi}.Address.";

    public async Task<string?> Allocate(ChainDefinition chain, PaymentMethodId pmi, string storeId, string invoiceId,
        IEnumerable<string> addresses, DateTimeOffset? createdAt, DateTimeOffset? monitoringExpiration)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        var candidates = new List<(string Address, string Key)>();
        foreach (var address in addresses)
            if (AddressRules.TryNormalize(chain, address, out var canonical, out var key) && candidates.All(c => c.Key != key))
                candidates.Add((canonical, key));

        await using var strategyContext = factory.CreateContext();
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = factory.CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var lockKey = $"Chains.Reservations.{pmi}";
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))");

            var invoiceKey = InvoiceKey(pmi, invoiceId);
            var saved = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == invoiceKey);
            if (saved is not null)
            {
                var claim = JsonConvert.DeserializeObject<Claim>(saved.Value)!;
                if (claim.StoreId != storeId)
                    throw new InvalidOperationException("The invoice address belongs to a different store.");
                await transaction.CommitAsync();
                return claim.Address;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var (address, key) in candidates)
            {
                var addressKey = AddressKey(pmi, key);
                var existing = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == addressKey);
                if (existing is not null && await IsReserved(context, JsonConvert.DeserializeObject<Claim>(existing.Value)!, now))
                    continue;
                var json = JsonConvert.SerializeObject(new Claim(storeId, invoiceId, address, key, createdAt, monitoringExpiration));
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Settings" ("Id", "Value") VALUES ({addressKey}, CAST({json} AS jsonb))
                    ON CONFLICT ("Id") DO UPDATE SET "Value" = EXCLUDED."Value"
                    """);
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Settings" ("Id", "Value") VALUES ({invoiceKey}, CAST({json} AS jsonb))
                    """);
                await transaction.CommitAsync();
                return address;
            }
            await transaction.CommitAsync();
            return null;
        });
    }

    /// <summary>The addresses of this store's pool that are reserved right now.</summary>
    public async Task<HashSet<string>> GetUsedAddresses(ChainDefinition chain, PaymentMethodId pmi, IEnumerable<string> addresses)
    {
        var keys = new Dictionary<string, string>();
        foreach (var address in addresses)
            if (AddressRules.TryNormalize(chain, address, out var canonical, out var key))
                keys[AddressKey(pmi, key)] = canonical;
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0)
            return used;
        await using var context = factory.CreateContext();
        var ids = keys.Keys.ToArray();
        var rows = await context.Settings.AsNoTracking().Where(s => ids.Contains(s.Id)).ToArrayAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var claim = JsonConvert.DeserializeObject<Claim>(row.Value)!;
            if (await IsReserved(context, claim, now))
                used.Add(claim.Address);
        }
        return used;
    }

    /// <summary>Every address with a claim for this payment method, by matching key. The listener watches these.</summary>
    internal async Task<Claim[]> CurrentClaims(PaymentMethodId pmi)
    {
        await using var context = factory.CreateContext();
        var prefix = Prefix(pmi);
        var rows = await context.Settings.AsNoTracking().Where(s => s.Id.StartsWith(prefix)).ToArrayAsync();
        return rows.Select(row => JsonConvert.DeserializeObject<Claim>(row.Value)!).ToArray();
    }

    internal async Task<Claim?> CurrentClaim(PaymentMethodId pmi, string key)
    {
        await using var context = factory.CreateContext();
        var id = AddressKey(pmi, key);
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
        return row is null ? null : JsonConvert.DeserializeObject<Claim>(row.Value);
    }

    static async Task<bool> IsReserved(ApplicationDbContext context, Claim claim, DateTimeOffset now)
    {
        var invoice = await context.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == claim.InvoiceId);
        if (invoice is null)
            return claim.MonitoringExpiration is null || claim.MonitoringExpiration > now;
        var entity = invoice.GetBlob();
        return ShouldReserve(entity.Status, entity.MonitoringExpiration + ReuseGrace, now);
    }

    internal static bool ShouldReserve(InvoiceStatus status, DateTimeOffset monitoringExpiration, DateTimeOffset now) =>
        status is InvoiceStatus.New or InvoiceStatus.Processing ||
        status == InvoiceStatus.Expired && monitoringExpiration > now;
}
