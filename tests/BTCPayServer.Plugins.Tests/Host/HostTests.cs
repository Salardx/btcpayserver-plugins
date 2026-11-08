using System.Numerics;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.BNBChain;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.Neo;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>
/// Runs BTCPay Server in-process with both plugins loaded, a fresh PostgreSQL database and fake nodes, and takes
/// invoices from creation to settlement on Neo N3, Neo X and BNB Smart Chain (native BNB and a BEP-20 token).
/// Needs <c>NEO_TEST_POSTGRES</c> (a connection string whose user may create databases); without it the test is skipped.
/// </summary>
public class HostTests
{
    const string Payer = "NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe";
    static readonly string[] NeoPool = ["NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x", "NiD9ny9tc3e7BQMtbwexKbGK7caxMQ7v3p"];
    static readonly string[] NeoXPool = ["0xd2dd7b597fd2435b6db61ddf48544fd931e6869f", "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed"];
    static readonly string[] BscPool = ["0x2222222222222222222222222222222222222222", "0x3333333333333333333333333333333333333333"];
    const string EvmPayer = "0x62b6e27213824c6a2b983c018377d9bd0fdc5e17";
    const string Usdt = "0x55d398326f99059ff775485246999027b3197955";

    [Fact]
    public async Task Invoices_settle_from_a_transfer_on_each_chain_and_survive_a_restart()
    {
        var connectionString = Environment.GetEnvironmentVariable("NEO_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Skip("Set NEO_TEST_POSTGRES to a PostgreSQL connection string to run the host test.");

        await using var neo = new FakeNeoNode();
        await using var neox = new FakeEvmNode(chainId: 12227332);
        await using var bsc = new FakeEvmNode(chainId: 97);
        var database = "neo_host_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }
        builder.Database = database;

        var root = RepositoryRoot();
        var data = Path.Combine(root, "artifacts", "host-tests", Guid.NewGuid().ToString("N"));
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
            ["TEST_RUNNER_ENABLED"] = "true",
            ["disable-registration"] = "false",
            ["DEBUG_PLUGINS"] = typeof(NeoPlugin).Assembly.Location + ";" + typeof(BNBChainPlugin).Assembly.Location,
            ["NEO_JSONRPC_URI"] = neo.Url,
            ["NEOX_JSONRPC_URI"] = neox.Url,
            ["BSC_JSONRPC_URI"] = bsc.Url,
            ["BSC_USDT_CONTRACT"] = Usdt,
        };
        IHost BuildHost() => Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseEnvironment("Development")
            .UseContentRoot(Path.Combine(root, "submodules", "btcpayserver", "BTCPayServer"))
            .ConfigureAppConfiguration(b => b.AddInMemoryCollection(config))
            .ConfigureLogging(b => b.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Warning))
            .ConfigureWebHostDefaults(web => web.UseKestrel().UseUrls("http://127.0.0.1:0").UseStartup<Startup>())
            .Build();

        var host = BuildHost();
        await host.StartWithTasksAsync().WaitAsync(TimeSpan.FromMinutes(3));
        string invoiceId;
        string neoxInvoiceId;
        string usdtInvoiceId;
        try
        {
            var handlers = host.Services.GetRequiredService<PaymentMethodHandlerDictionary>();
            foreach (var pmi in new[] { "GAS-NEO", "GAS-NEOX", "BNB-BSC", "USDT-BSC" })
                Assert.True(handlers.TryGetValue(new PaymentMethodId(pmi), out _), $"{pmi} handler not registered");
            Assert.False(handlers.TryGetValue(new PaymentMethodId("USDC-BSC"), out _), "USDC-BSC has no testnet contract and must not be offered");

            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var (email, password) = await CreateAdmin(host);
            var api = new BTCPayServerClient(new Uri(address), email, password);
            var store = await api.CreateStore(new CreateStoreRequest { Name = "Payment watcher host test" });

            foreach (var invalid in new[] { "not-an-address", "0x1234567890abcdef1234567890abcdef12345678" })
                await Assert.ThrowsAsync<GreenfieldValidationException>(() => api.UpdateStorePaymentMethod(store.Id, "GAS-NEO",
                    new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(invalid) } }));
            await api.UpdateStorePaymentMethod(store.Id, "GAS-NEO",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(NeoPool) } });
            await api.UpdateStorePaymentMethod(store.Id, "GAS-NEOX",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(NeoXPool) } });
            await api.UpdateStorePaymentMethod(store.Id, "BNB-BSC",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(BscPool) } });
            await api.UpdateStorePaymentMethod(store.Id, "USDT-BSC",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(BscPool) } });

            host.Services.GetRequiredService<ChainActivationService<NeoPlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<BNBChainPlugin>>().Invalidate();
            var neoRpc = host.Services.GetRequiredService<ChainRpcProvider<NeoPlugin>>();
            var bnbRpc = host.Services.GetRequiredService<ChainRpcProvider<BNBChainPlugin>>();
            foreach (var chainId in new[] { "NEO", "NEOX" })
                await Eventually(async () =>
                {
                    await neoRpc.UpdateSummary(chainId, CancellationToken.None);
                    return neoRpc.IsAvailable(chainId);
                }, $"{chainId} never became available");
            await Eventually(async () =>
            {
                await bnbRpc.UpdateSummary("BSC", CancellationToken.None);
                return bnbRpc.IsAvailable("BSC");
            }, "BSC never became available");

            // Neo N3: two invoices take two different addresses, a transfer settles the first.
            var invoice = await CreateInvoice(api, store.Id, "GAS-NEO", "GAS", 1.5m);
            var second = await CreateInvoice(api, store.Id, "GAS-NEO", "GAS", 1.5m);
            var prompt = Assert.Single(await api.GetInvoicePaymentMethods(invoice.Id));
            var secondPrompt = Assert.Single(await api.GetInvoicePaymentMethods(second.Id));
            Assert.Contains(prompt.Destination, NeoPool);
            Assert.Contains(secondPrompt.Destination, NeoPool);
            Assert.NotEqual(prompt.Destination, secondPrompt.Destination);
            Assert.StartsWith($"neo:{prompt.Destination}?asset=gas&amount=1.5", prompt.PaymentLink);
            await Assert.ThrowsAsync<GreenfieldAPIException>(() => CreateInvoice(api, store.Id, "GAS-NEO", "GAS", 1.5m));   // the pool is exhausted

            var checkout = await http.GetAsync(invoice.CheckoutLink);
            checkout.EnsureSuccessStatusCode();
            Assert.Contains("GAS", await checkout.Content.ReadAsStringAsync());

            neo.AddBlock(out var neoTx, Payer, prompt.Destination, 150_000_000);
            await Eventually(async () => (await api.GetInvoice(invoice.Id)).Status == InvoiceStatus.Settled, "the Neo N3 invoice did not settle");
            var paid = Assert.Single(await api.GetInvoicePaymentMethods(invoice.Id));
            var payment = Assert.Single(paid.Payments);
            Assert.Equal(1.5m, payment.Value);
            Assert.Equal(InvoicePaymentMethodDataModel.Payment.PaymentStatus.Settled, payment.Status);
            Assert.StartsWith(neoTx!, payment.Id);
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(second.Id)).Status);
            invoiceId = invoice.Id;

            // Neo X: a native transfer of exactly the due amount, read from the block and its receipt.
            var neoxInvoice = await CreateInvoice(api, store.Id, "GAS-NEOX", "GAS", 1.5m);
            var neoxPrompt = Assert.Single(await api.GetInvoicePaymentMethods(neoxInvoice.Id));
            Assert.Contains(neoxPrompt.Destination, NeoXPool.Select(a => a.ToLowerInvariant()));
            Assert.Equal($"ethereum:{neoxPrompt.Destination}@12227332?value=1500000000000000000", neoxPrompt.PaymentLink);
            neox.AddBlock(out var neoxTx, EvmPayer, neoxPrompt.Destination, BigInteger.Parse("1500000000000000000"));
            await Eventually(async () => (await api.GetInvoice(neoxInvoice.Id)).Status == InvoiceStatus.Settled, "the Neo X invoice did not settle");
            var neoxPayment = Assert.Single((await api.GetInvoicePaymentMethods(neoxInvoice.Id)).Single().Payments);
            Assert.Equal(1.5m, neoxPayment.Value);
            Assert.StartsWith(neoxTx!, neoxPayment.Id);
            neoxInvoiceId = neoxInvoice.Id;

            // BNB Smart Chain: a BEP-20 transfer is a Transfer log of the token contract, recorded once six more blocks exist.
            var usdtInvoice = await CreateInvoice(api, store.Id, "USDT-BSC", "USDT", 10m);
            var usdtPrompt = Assert.Single(await api.GetInvoicePaymentMethods(usdtInvoice.Id));
            Assert.Contains(usdtPrompt.Destination, BscPool);
            Assert.Equal($"ethereum:{Usdt}@97/transfer?address={usdtPrompt.Destination}&uint256=10000000000000000000", usdtPrompt.PaymentLink);
            bsc.AddTokenTransfer(out var usdtTx, Usdt, EvmPayer, usdtPrompt.Destination, BigInteger.Parse("10000000000000000000"));
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(usdtInvoice.Id)).Status);   // not final yet
            bsc.AddBlocks(6);
            await Eventually(async () => (await api.GetInvoice(usdtInvoice.Id)).Status == InvoiceStatus.Settled, "the USDT invoice did not settle");
            var usdtPayment = Assert.Single((await api.GetInvoicePaymentMethods(usdtInvoice.Id)).Single().Payments);
            Assert.Equal(10m, usdtPayment.Value);
            Assert.StartsWith(usdtTx, usdtPayment.Id);
            usdtInvoiceId = usdtInvoice.Id;

            // ... and the chain's own coin, from the block's transactions and the receipt.
            var bnbInvoice = await CreateInvoice(api, store.Id, "BNB-BSC", "BNB", 0.5m);
            var bnbPrompt = Assert.Single(await api.GetInvoicePaymentMethods(bnbInvoice.Id));
            Assert.Contains(bnbPrompt.Destination, BscPool);
            Assert.Equal($"ethereum:{bnbPrompt.Destination}@97?value=500000000000000000", bnbPrompt.PaymentLink);
            bsc.AddBlock(out var bnbTx, EvmPayer, bnbPrompt.Destination, BigInteger.Parse("500000000000000000"));
            bsc.AddBlocks(6);
            await Eventually(async () => (await api.GetInvoice(bnbInvoice.Id)).Status == InvoiceStatus.Settled, "the BNB invoice did not settle");
            var bnbPayment = Assert.Single((await api.GetInvoicePaymentMethods(bnbInvoice.Id)).Single().Payments);
            Assert.Equal(0.5m, bnbPayment.Value);
            Assert.StartsWith(bnbTx!, bnbPayment.Id);

            // A failed transaction and a transfer to an address nobody reserved change nothing.
            neo.AddBlock(out _, Payer, secondPrompt.Destination, 150_000_000, halt: false);
            neox.AddBlock(out _, EvmPayer, "0x1111111111111111111111111111111111111111", BigInteger.Parse("1500000000000000000"));
            var states = host.Services.GetRequiredService<ChainStateStore>();
            await Eventually(async () => (await states.Get("Chains.NEO.Listener"))?.LastBlockIndex >= neo.Tip, "the Neo N3 listener did not reach the tip");
            await Eventually(async () => (await states.Get("Chains.NEOX.Listener"))?.LastBlockIndex >= neox.Tip, "the Neo X listener did not reach the tip");
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(second.Id)).Status);

            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            host.Dispose();
        }

        // After a restart the payments are still there and the listeners resume from their cursors.
        host = BuildHost();
        await host.StartWithTasksAsync().WaitAsync(TimeSpan.FromMinutes(3));
        try
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var (email, password) = await CreateAdmin(host);
            var api = new BTCPayServerClient(new Uri(address), email, password);
            Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(invoiceId)).Status);
            Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(neoxInvoiceId)).Status);
            Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(usdtInvoiceId)).Status);
            var states = host.Services.GetRequiredService<ChainStateStore>();
            var neoxState = await states.Get("Chains.NEOX.Listener");
            Assert.NotNull(neoxState);
            Assert.True(neoxState.LastBlockIndex >= neox.Tip - 1);
            var bscState = await states.Get("Chains.BSC.Listener");
            Assert.NotNull(bscState);
            Assert.True(bscState.LastBlockIndex >= bsc.Tip - 7, $"BSC cursor {bscState.LastBlockIndex} is too far behind tip {bsc.Tip}");
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            host.Dispose();
        }
    }

    static Task<Client.Models.InvoiceData> CreateInvoice(BTCPayServerClient api, string storeId, string paymentMethodId, string currency, decimal amount) =>
        api.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Currency = currency,
            Amount = amount,
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = [paymentMethodId],
                LazyPaymentMethods = false,
                SpeedPolicy = SpeedPolicy.HighSpeed,
                Expiration = TimeSpan.FromHours(1),
            },
        });

    static async Task<(string Email, string Password)> CreateAdmin(IHost host)
    {
        var email = $"watcher-{Guid.NewGuid():N}@example.test";
        var password = Guid.NewGuid().ToString("N") + "!Aa9";
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.ServerAdmin))
            Assert.True((await roles.CreateAsync(new IdentityRole(Roles.ServerAdmin))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        user.SetBlob(new UserBlob { AllowGreenfieldBasicAuth = true });
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, Roles.ServerAdmin)).Succeeded);
        return (email, password);
    }

    static async Task Eventually(Func<Task<bool>> condition, string failure, int timeoutSeconds = 90)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(500);
        }
        Assert.Fail(failure);
    }

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BTCPayServer.Plugins.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot locate the plugin checkout.");
    }
}
