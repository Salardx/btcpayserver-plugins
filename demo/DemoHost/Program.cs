// Runs BTCPay Server in-process with the three plugins loaded, on a fresh PostgreSQL database, with BTCPay's network set to
// regtest (the plugins read that as "testnet": NEAR testnet, Neo N3 testnet, Neo X testnet, BNB Smart Chain testnet).
// An admin user is created and its credentials are printed and written to demo-host.json, for the recorder or for a
// person who wants to click through the plugins. No Bitcoin node, no NBXplorer: the chains come from public nodes.
//
//   DEMO_POSTGRES=Host=127.0.0.1;Port=5432;Username=postgres   (a user that may create databases; required)
//   DEMO_PORT=14142                                              (optional)
//   NEAR_JSONRPC_URI, NEO_JSONRPC_URI, ...                       (optional node overrides, as in the README)
//
// Stop it with Ctrl+C; the database it created is left behind for inspection.
using System.Text.Json;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.BNBChain;
using BTCPayServer.Plugins.NEAR;
using BTCPayServer.Plugins.Neo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("DEMO_POSTGRES");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set DEMO_POSTGRES to a PostgreSQL connection string whose user may create databases.");
    return 2;
}
var port = int.TryParse(Environment.GetEnvironmentVariable("DEMO_PORT"), out var p) ? p : 14142;
var url = $"http://127.0.0.1:{port}";

var root = RepositoryRoot();
var database = "watcher_demo_" + Guid.NewGuid().ToString("N")[..12];
var builder = new NpgsqlConnectionStringBuilder(connectionString);
await using (var admin = new NpgsqlConnection(builder.ConnectionString))
{
    await admin.OpenAsync();
    await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
    await create.ExecuteNonQueryAsync();
}
builder.Database = database;

var data = Path.Combine(root, "artifacts", "demo-host", database);
var plugins = Path.Combine(data, "plugins");
Directory.CreateDirectory(plugins);
var config = new Dictionary<string, string?>
{
    ["network"] = "regtest",
    ["chains"] = "none",
    ["nodefaultchain"] = "true",
    ["datadir"] = data,
    ["plugindir"] = plugins,
    ["postgres"] = builder.ConnectionString,
    ["disable-registration"] = "false",
    ["DEBUG_PLUGINS"] = string.Join(";", typeof(NeoPlugin).Assembly.Location, typeof(BNBChainPlugin).Assembly.Location, typeof(NEARPlugin).Assembly.Location),
};
foreach (var key in new[] { "NEAR_JSONRPC_URI", "NEO_JSONRPC_URI", "NEOX_JSONRPC_URI", "BSC_JSONRPC_URI", "NEAR_USDT_CONTRACT", "BSC_USDT_CONTRACT", "BSC_USDC_CONTRACT" })
    if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
        config[key] = value;

var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
    .UseEnvironment("Development")
    .UseContentRoot(Path.Combine(root, "submodules", "btcpayserver", "BTCPayServer"))
    .ConfigureAppConfiguration(b => b.AddInMemoryCollection(config))
    .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning)
        .AddFilter("BTCPayServer.Plugins.Chains", LogLevel.Information)
        .AddFilter("NeoPaymentWatcher", LogLevel.Information))
    .ConfigureWebHostDefaults(web => web.UseKestrel().UseUrls(url).UseStartup<Startup>())
    .Build();

await host.StartWithTasksAsync();
var (email, password) = await CreateAdmin(host);
var credentials = new { url, email, password, database };
Console.WriteLine($"BTCPAY_URL={url}");
Console.WriteLine($"BTCPAY_EMAIL={email}");
Console.WriteLine($"BTCPAY_PASSWORD={password}");
await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "demo-host.json"), JsonSerializer.Serialize(credentials));
Console.WriteLine("BTCPay Server is up with the Neo, BNB Chain and NEAR plugins. Press Ctrl+C to stop.");
await host.WaitForShutdownAsync();
return 0;

static async Task<(string Email, string Password)> CreateAdmin(IHost host)
{
    var email = $"demo-{Guid.NewGuid():N}@example.test";
    var password = Guid.NewGuid().ToString("N") + "!Aa9";
    using var scope = host.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.ServerAdmin))
        Ensure((await roles.CreateAsync(new IdentityRole(Roles.ServerAdmin))).Succeeded, "creating the admin role");
    var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
    user.SetBlob(new UserBlob { AllowGreenfieldBasicAuth = true });
    Ensure((await users.CreateAsync(user, password)).Succeeded, "creating the admin user");
    Ensure((await users.AddToRoleAsync(user, Roles.ServerAdmin)).Succeeded, "making the user an admin");
    return (email, password);
}

static void Ensure(bool ok, string what)
{
    if (!ok)
        throw new InvalidOperationException($"BTCPay Server refused {what}.");
}

static string RepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BTCPayServer.Plugins.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Cannot locate the plugin checkout.");
}
