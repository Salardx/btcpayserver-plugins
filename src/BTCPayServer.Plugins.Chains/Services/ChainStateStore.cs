using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>Where the listener is on a chain. Kept in BTCPay's settings table so every host sees the same cursor.</summary>
public sealed class ListenerState
{
    public long LastBlockIndex { get; set; }
    public long? LastBlockTime { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ChainStateStore(ApplicationDbContextFactory factory)
{
    public static string ListenerStateKey(ChainDefinition chain) => $"Chains.{chain.Id}.Listener";

    public async Task<ListenerState?> Get(string key)
    {
        await using var context = factory.CreateContext();
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        return row is null ? null : JsonConvert.DeserializeObject<ListenerState>(row.Value);
    }

    public async Task Save(string key, ListenerState state)
    {
        await using var context = factory.CreateContext();
        var value = JsonConvert.SerializeObject(state);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Settings" ("Id", "Value") VALUES ({key}, CAST({value} AS jsonb))
            ON CONFLICT ("Id") DO UPDATE SET "Value" = EXCLUDED."Value"
            """);
    }
}
