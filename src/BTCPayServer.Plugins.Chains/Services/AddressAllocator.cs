using System.Collections.Concurrent;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains.Solana;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>
/// Hands each invoice one of the store's addresses. On most chains the address is reserved while the invoice can
/// still be paid, so two open invoices never share one. On a chain that matches payments by reference (Solana) the
/// invoice takes the store's address without reserving it and is found by its reference, so any number of open
/// invoices share the address. Claims live in BTCPay's settings table, per payment method; allocation runs under
/// one advisory lock per payment method.
/// </summary>
public sealed class AddressAllocator(ApplicationDbContextFactory factory)
{
    internal sealed record Claim(string StoreId, string InvoiceId, string Address, string Key,
        DateTimeOffset? CreatedAt = null, DateTimeOffset? MonitoringExpiration = null, string? Reference = null);

    /// <summary>An expired invoice keeps its address a while longer, so a transfer made just before the deadline is still matched before the address is handed on.</summary>
    internal static readonly TimeSpan ReuseGrace = TimeSpan.FromMinutes(15);

    /// <summary>How long after an invoice can no longer be paid its rows are kept, and how often they are swept.</summary>
    internal static readonly TimeSpan SweepAfter = TimeSpan.FromHours(1);
    static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    readonly ConcurrentDictionary<string, DateTimeOffset> lastSweep = new(StringComparer.Ordinal);

    static string AddressKey(PaymentMethodId pmi, string key) => $"Chains.{pmi}.Address.{key}";
    static string InvoiceKey(PaymentMethodId pmi, string invoiceId) => $"Chains.{pmi}.Invoice.{invoiceId}";
    static string ReferenceKey(PaymentMethodId pmi, string reference) => $"Chains.{pmi}.Reference.{reference}";
    static string Prefix(PaymentMethodId pmi) => $"Chains.{pmi}.Address.";
    static string InvoicePrefix(PaymentMethodId pmi) => $"Chains.{pmi}.Invoice.";
    static string ReferencePrefix(PaymentMethodId pmi) => $"Chains.{pmi}.Reference.";

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
            await Sweep(context, pmi, now);
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

    /// <summary>
    /// For a chain that matches payments by reference: the invoice takes the store's first address without reserving
    /// it and gets a reference of its own, under which the payment is found. Null when the store has no valid address.
    /// Asking again for the same invoice returns what it was given.
    /// </summary>
    public async Task<(string Address, string Reference)?> AllocateShared(ChainDefinition chain, PaymentMethodId pmi, string storeId, string invoiceId,
        IEnumerable<string> addresses, Func<string> newReference, DateTimeOffset? createdAt, DateTimeOffset? monitoringExpiration)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        string? shared = null;
        foreach (var address in addresses)
        {
            if (AddressRules.TryNormalize(chain, address, out var canonical, out _))
            {
                shared = canonical;
                break;
            }
        }
        if (shared is null)
            return null;

        await using var strategyContext = factory.CreateContext();
        var given = await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = factory.CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var lockKey = $"Chains.Reservations.{pmi}";
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))");

            var invoiceKey = InvoiceKey(pmi, invoiceId);
            var saved = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == invoiceKey);
            if (saved is not null)
            {
                var earlier = JsonConvert.DeserializeObject<Claim>(saved.Value)!;
                if (earlier.StoreId != storeId)
                    throw new InvalidOperationException("The invoice address belongs to a different store.");
                if (earlier.Reference is null)
                    throw new InvalidOperationException("The invoice reserved an address of its own before; it cannot share one now.");
                await transaction.CommitAsync();
                return earlier;
            }

            var now = DateTimeOffset.UtcNow;
            await Sweep(context, pmi, now);
            var reference = newReference();
            var claim = new Claim(storeId, invoiceId, shared, SolanaAddress.SharedKey(shared, reference), createdAt, monitoringExpiration ?? now.AddDays(1), reference);
            var json = JsonConvert.SerializeObject(claim);
            var referenceKey = ReferenceKey(pmi, reference);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Settings" ("Id", "Value") VALUES ({referenceKey}, CAST({json} AS jsonb))
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Settings" ("Id", "Value") VALUES ({invoiceKey}, CAST({json} AS jsonb))
                """);
            await transaction.CommitAsync();
            return claim;
        });
        return (given.Address, given.Reference!);
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

    /// <summary>
    /// The claims of this payment method whose invoice can still be paid, reserved addresses and references alike:
    /// what the listener watches. A claim's row outlives its invoice (a reserved address keeps its row until the
    /// next invoice takes it), so the rows alone would keep a chain being read for good.
    /// </summary>
    internal async Task<Claim[]> OpenClaims(PaymentMethodId pmi)
    {
        await using var context = factory.CreateContext();
        var prefix = Prefix(pmi);
        var references = ReferencePrefix(pmi);
        var rows = await context.Settings.AsNoTracking().Where(s => s.Id.StartsWith(prefix) || s.Id.StartsWith(references)).ToArrayAsync();
        if (rows.Length == 0)
            return [];
        var claims = rows.Select(row => JsonConvert.DeserializeObject<Claim>(row.Value)!).ToArray();
        var ids = claims.Select(c => c.InvoiceId).Distinct().ToArray();
        // The status column and the time stored with the claim decide; the invoice itself is not loaded.
        var statuses = await context.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.Status }).ToDictionaryAsync(i => i.Id, i => i.Status);
        var now = DateTimeOffset.UtcNow;
        var open = new List<Claim>();
        foreach (var claim in claims)
        {
            var saved = statuses.TryGetValue(claim.InvoiceId, out var status);
            if (IsOpen(claim.MonitoringExpiration, saved, status, now) ?? await IsReserved(context, claim, now))
                open.Add(claim);
        }
        return open.ToArray();
    }

    /// <summary>
    /// Whether an invoice can still be paid, told from its status column and the end of its payment window as the
    /// claim remembers it. Null when those two do not say (a claim from before the time was stored, a status this
    /// version does not know): then the invoice itself is asked. An invoice that is not saved yet holds its claim
    /// until its window would have closed.
    /// </summary>
    internal static bool? IsOpen(DateTimeOffset? monitoringExpiration, bool saved, string? status, DateTimeOffset now)
    {
        if (!saved)
            return monitoringExpiration is null || monitoringExpiration > now;
        if (monitoringExpiration is { } expiration && Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out var known) && Enum.IsDefined(known))
            return ShouldReserve(known, expiration + ReuseGrace, now);
        return null;
    }

    internal async Task<Claim?> CurrentClaim(PaymentMethodId pmi, string key)
    {
        await using var context = factory.CreateContext();
        var id = SolanaAddress.TrySplitSharedKey(key, out _, out var reference) ? ReferenceKey(pmi, reference) : AddressKey(pmi, key);
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
        return row is null ? null : JsonConvert.DeserializeObject<Claim>(row.Value);
    }

    /// <summary>
    /// Removes the rows of invoices that can no longer be paid: one row per invoice, and one per reference. A reserved
    /// address needs no sweep, because its row is overwritten when the next invoice takes the address.
    /// </summary>
    async Task Sweep(ApplicationDbContext context, PaymentMethodId pmi, DateTimeOffset now)
    {
        var id = pmi.ToString();
        if (lastSweep.TryGetValue(id, out var last) && now - last < SweepInterval)
            return;
        lastSweep[id] = now;
        var invoices = InvoicePrefix(pmi);
        var references = ReferencePrefix(pmi);
        var cutoff = now - ReuseGrace - SweepAfter;
        // CASE fixes the order of evaluation: the time is read only from rows that are ours and carry one.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "Settings"
            WHERE CASE
                WHEN (starts_with("Id", {invoices}) OR starts_with("Id", {references})) AND ("Value"->>'MonitoringExpiration') IS NOT NULL
                THEN ("Value"->>'MonitoringExpiration')::timestamptz < {cutoff}
                ELSE FALSE
            END
            """);
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
