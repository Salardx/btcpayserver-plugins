using System.Numerics;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Arc;
using BTCPayServer.Plugins.Avalanche;
using BTCPayServer.Plugins.BNBChain;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.Chains.Solana;
using BTCPayServer.Plugins.Flare;
using BTCPayServer.Plugins.NEAR;
using BTCPayServer.Plugins.Neo;
using BTCPayServer.Plugins.Solana;
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
/// Runs BTCPay Server in-process with all seven plugins loaded, a fresh PostgreSQL database and fake nodes, and takes
/// invoices from creation to settlement on Neo N3, Neo X, BNB Smart Chain (native BNB and a BEP-20 token), NEAR (NEAR
/// and USDC), Arc (USDC, the chain's coin), Avalanche (USDC), Flare (FLR priced by the FTSO) and Solana (SOL and
/// USDC, by Solana Pay reference and by reserved address).
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
    static readonly string[] NearPool = ["shop.testnet", "pay2.shop.testnet"];
    const string NearPayer = "alice.testnet";
    static readonly BigInteger OneNear = BigInteger.Pow(10, 24);
    static readonly string[] ArcPool = ["0x4444444444444444444444444444444444444444", "0x5555555555555555555555555555555555555555"];
    static readonly string[] AvalanchePool = ["0x6666666666666666666666666666666666666666", "0x7777777777777777777777777777777777777777"];
    static readonly string[] FlarePool = ["0x8888888888888888888888888888888888888888", "0x9999999999999999999999999999999999999999"];
    /// <summary>A smart account's entry point: a contract that moves the payer's USDC on Arc, so the transaction carries no value of its own.</summary>
    const string EntryPoint = "0x5ff137d4b0fdcd49dca30c7cf57e578a026d2789";
    const string Ftso = "0x7bde3df0624114edb3a67dfe6753e62f4e7c1d20";
    static readonly string SolanaShop = FakeSolanaNode.Address("host-shop");
    static readonly string SolanaSpare = FakeSolanaNode.Address("host-spare");
    static readonly string SolanaPayer = FakeSolanaNode.Address("host-payer");

    [Fact]
    public async Task Invoices_settle_from_a_transfer_on_each_chain_and_survive_a_restart()
    {
        var connectionString = Environment.GetEnvironmentVariable("NEO_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Skip("Set NEO_TEST_POSTGRES to a PostgreSQL connection string to run the host test.");

        await using var neo = new FakeNeoNode();
        await using var neox = new FakeEvmNode(chainId: 12227332);
        await using var bsc = new FakeEvmNode(chainId: 97);
        await using var near = new FakeNearNode();
        foreach (var account in NearPool)
            near.Fund(account, OneNear);
        near.Fund(NearPayer, 100 * OneNear);
        near.SetTokenBalance(FakeNearNode.Usdc, NearPayer, 1_000_000_000);
        await using var arc = new FakeEvmNode(chainId: 5042002) { AnswersLogRanges = true };
        await using var avalanche = new FakeEvmNode(chainId: 43113);
        // Flare's node also answers the oracle: the registry names the FtsoV2 contract, which quotes every feed.
        await using var flare = new FakeEvmNode(chainId: 114);
        var feeds = new Dictionary<string, BigInteger>
        {
            ["FLR"] = 500_000,          // 0.005 USD with 8 decimals
            ["XRP"] = 100_000_000,
            ["BTC"] = 6_700_000_000_000,
            ["ETH"] = 190_000_000_000,
            ["USDT"] = 100_000_000,
            ["USDC"] = 100_000_000,
        };
        flare.Call = (to, data) =>
        {
            if (to == FtsoRateProvider.ContractRegistry && data == FtsoRateProvider.ContractAddressByNameCall("FtsoV2"))
                return "0x" + new string('0', 24) + Ftso[2..];
            var feed = feeds.Keys.FirstOrDefault(symbol => data == FtsoRateProvider.GetFeedByIdCall(symbol));
            return to != Ftso || feed is null
                ? null
                : "0x" + feeds[feed].ToString("x64")[^64..] + 8.ToString("x64") + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x64");
        };
        await using var solana = new FakeSolanaNode();
        solana.AddWallet(SolanaShop);
        var solanaShopUsdc = solana.AddTokenAccount(SolanaShop, FakeSolanaNode.Usdc);
        solana.AddWallet(SolanaSpare);
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
            ["DEBUG_PLUGINS"] = string.Join(";", typeof(NeoPlugin).Assembly.Location, typeof(BNBChainPlugin).Assembly.Location, typeof(NEARPlugin).Assembly.Location,
                typeof(ArcPlugin).Assembly.Location, typeof(AvalanchePlugin).Assembly.Location, typeof(FlarePlugin).Assembly.Location, typeof(SolanaPlugin).Assembly.Location),
            ["NEO_JSONRPC_URI"] = neo.Url,
            ["NEOX_JSONRPC_URI"] = neox.Url,
            ["BSC_JSONRPC_URI"] = bsc.Url,
            ["BSC_USDT_CONTRACT"] = Usdt,
            ["NEAR_JSONRPC_URI"] = near.Url,
            ["ARC_JSONRPC_URI"] = arc.Url,
            ["AVAXC_JSONRPC_URI"] = avalanche.Url,
            ["FLARE_JSONRPC_URI"] = flare.Url,
            ["SOLANA_JSONRPC_URI"] = solana.Url,
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
        string nearInvoiceId;
        string arcInvoiceId;
        string solanaInvoiceId;
        try
        {
            var handlers = host.Services.GetRequiredService<PaymentMethodHandlerDictionary>();
            foreach (var pmi in new[]
                     {
                         "GAS-NEO", "GAS-NEOX", "BNB-BSC", "USDT-BSC", "NEAR-NEAR", "USDC-NEAR", "USDC-ARC", "EURC-ARC", "AVAX-AVAXC", "USDC-AVAXC",
                         "FLR-FLARE", "FXRP-FLARE", "SOL-SOLANA", "USDC-SOLANA",
                     })
                Assert.True(handlers.TryGetValue(new PaymentMethodId(pmi), out _), $"{pmi} handler not registered");
            Assert.False(handlers.TryGetValue(new PaymentMethodId("USDC-BSC"), out _), "USDC-BSC has no testnet contract and must not be offered");
            Assert.False(handlers.TryGetValue(new PaymentMethodId("USDT-NEAR"), out _), "USDT-NEAR has no testnet contract and must not be offered");
            foreach (var pmi in new[] { "USDT-AVAXC", "USDT0-FLARE", "USDT-SOLANA" })
                Assert.False(handlers.TryGetValue(new PaymentMethodId(pmi), out _), $"{pmi} has no testnet contract and must not be offered");

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
            // NEAR: an account must be well formed and, if named, exist on the chain.
            foreach (var invalid in new[] { "not an account!", "ghost.testnet" })
                await Assert.ThrowsAsync<GreenfieldValidationException>(() => api.UpdateStorePaymentMethod(store.Id, "NEAR-NEAR",
                    new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(invalid) } }));
            await api.UpdateStorePaymentMethod(store.Id, "NEAR-NEAR",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(NearPool) } });
            await api.UpdateStorePaymentMethod(store.Id, "USDC-NEAR",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(NearPool) } });

            await api.UpdateStorePaymentMethod(store.Id, "USDC-ARC",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(ArcPool) } });
            await api.UpdateStorePaymentMethod(store.Id, "USDC-AVAXC",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(AvalanchePool) } });
            await api.UpdateStorePaymentMethod(store.Id, "FLR-FLARE",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(FlarePool) } });
            // Solana: a receiving address is a wallet, not one of its token accounts, and for a token its token account must exist.
            foreach (var invalid in new[] { "0x1234567890abcdef1234567890abcdef12345678", solanaShopUsdc, SolanaSpare })
                await Assert.ThrowsAsync<GreenfieldValidationException>(() => api.UpdateStorePaymentMethod(store.Id, "USDC-SOLANA",
                    new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(invalid) } }));
            await api.UpdateStorePaymentMethod(store.Id, "SOL-SOLANA",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(SolanaShop) } });
            await api.UpdateStorePaymentMethod(store.Id, "USDC-SOLANA",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(SolanaShop) } });

            host.Services.GetRequiredService<ChainActivationService<ArcPlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<AvalanchePlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<FlarePlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<SolanaPlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<NeoPlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<BNBChainPlugin>>().Invalidate();
            host.Services.GetRequiredService<ChainActivationService<NEARPlugin>>().Invalidate();
            var neoRpc = host.Services.GetRequiredService<ChainRpcProvider<NeoPlugin>>();
            var bnbRpc = host.Services.GetRequiredService<ChainRpcProvider<BNBChainPlugin>>();
            var nearRpc = host.Services.GetRequiredService<ChainRpcProvider<NEARPlugin>>();
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
            await Eventually(async () =>
            {
                await nearRpc.UpdateSummary("NEAR", CancellationToken.None);
                return nearRpc.IsAvailable("NEAR");
            }, "NEAR never became available");
            var arcRpc = host.Services.GetRequiredService<ChainRpcProvider<ArcPlugin>>();
            var avalancheRpc = host.Services.GetRequiredService<ChainRpcProvider<AvalanchePlugin>>();
            var flareRpc = host.Services.GetRequiredService<ChainRpcProvider<FlarePlugin>>();
            var solanaRpc = host.Services.GetRequiredService<ChainRpcProvider<SolanaPlugin>>();
            await Eventually(async () =>
            {
                await arcRpc.UpdateSummary("ARC", CancellationToken.None);
                return arcRpc.IsAvailable("ARC");
            }, "Arc never became available");
            await Eventually(async () =>
            {
                await avalancheRpc.UpdateSummary("AVAXC", CancellationToken.None);
                return avalancheRpc.IsAvailable("AVAXC");
            }, "Avalanche never became available");
            await Eventually(async () =>
            {
                await flareRpc.UpdateSummary("FLARE", CancellationToken.None);
                return flareRpc.IsAvailable("FLARE");
            }, "Flare never became available");
            await Eventually(async () =>
            {
                await solanaRpc.UpdateSummary("SOLANA", CancellationToken.None);
                return solanaRpc.IsAvailable("SOLANA");
            }, "Solana never became available");

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

            // NEAR: the checkout links to the pay page; the payment is found from the account's balance and named by its transaction.
            var nearInvoice = await CreateInvoice(api, store.Id, "NEAR-NEAR", "NEAR", 1.5m);
            var nearPrompt = Assert.Single(await api.GetInvoicePaymentMethods(nearInvoice.Id));
            Assert.Contains(nearPrompt.Destination, NearPool);
            Assert.Contains($"/plugins/near/pay/{nearInvoice.Id}/NEAR-NEAR", nearPrompt.PaymentLink);
            var payPage = await http.GetAsync(nearPrompt.PaymentLink);
            payPage.EnsureSuccessStatusCode();
            var payHtml = await payPage.Content.ReadAsStringAsync();
            Assert.Contains(nearPrompt.Destination, payHtml);
            Assert.Contains("1.5", payHtml);
            var nearCheckout = await http.GetAsync(nearInvoice.CheckoutLink);
            nearCheckout.EnsureSuccessStatusCode();
            Assert.Contains("/plugins/near/pay/", await nearCheckout.Content.ReadAsStringAsync());
            near.AddNativeTransfer(out var nearTx, NearPayer, nearPrompt.Destination, 3 * OneNear / 2, crossShard: true);
            near.AddBlocks(2);
            await Eventually(async () => (await api.GetInvoice(nearInvoice.Id)).Status == InvoiceStatus.Settled, "the NEAR invoice did not settle");
            var nearPayment = Assert.Single((await api.GetInvoicePaymentMethods(nearInvoice.Id)).Single().Payments);
            Assert.Equal(1.5m, nearPayment.Value);
            Assert.StartsWith(nearTx, nearPayment.Id);
            nearInvoiceId = nearInvoice.Id;

            // ... and USDC on NEAR, a NEP-141 transfer call naming the account, with the invoice id as memo.
            var usdcInvoice = await CreateInvoice(api, store.Id, "USDC-NEAR", "USDC", 10m);
            var usdcPrompt = Assert.Single(await api.GetInvoicePaymentMethods(usdcInvoice.Id));
            Assert.Contains(usdcPrompt.Destination, NearPool);
            near.AddTokenTransfer(out var usdcTx, FakeNearNode.Usdc, NearPayer, usdcPrompt.Destination, 10_000_000, memo: usdcInvoice.Id);
            near.AddBlocks(2);
            await Eventually(async () => (await api.GetInvoice(usdcInvoice.Id)).Status == InvoiceStatus.Settled, "the USDC on NEAR invoice did not settle");
            var usdcPayment = Assert.Single((await api.GetInvoicePaymentMethods(usdcInvoice.Id)).Single().Payments);
            Assert.Equal(10m, usdcPayment.Value);
            Assert.StartsWith(usdcTx, usdcPayment.Id);

            // Arc: USDC is the chain's coin. A smart account pays, so the transaction itself carries no value and makes no
            // ERC-20 call: the payment exists only as the coin's transfer log, which is what the listener reads, a span at a time.
            var arcInvoice = await CreateInvoice(api, store.Id, "USDC-ARC", "USDC", 12.5m);
            var arcPrompt = Assert.Single(await api.GetInvoicePaymentMethods(arcInvoice.Id));
            Assert.Contains(arcPrompt.Destination, ArcPool);
            Assert.Equal($"ethereum:{arcPrompt.Destination}@5042002?value=12500000000000000000", arcPrompt.PaymentLink);
            arc.AddLogs(out var arcTx, EvmPayer, EntryPoint, BigInteger.Zero,
                (ArcChains.UsdcTransferLog, EvmPayer, arcPrompt.Destination, BigInteger.Parse("12500000000000000000")));
            arc.AddBlocks(2);
            await Eventually(async () => (await api.GetInvoice(arcInvoice.Id)).Status == InvoiceStatus.Settled, "the Arc invoice did not settle");
            var arcPayment = Assert.Single((await api.GetInvoicePaymentMethods(arcInvoice.Id)).Single().Payments);
            Assert.Equal(12.5m, arcPayment.Value);
            Assert.StartsWith(arcTx, arcPayment.Id);
            arcInvoiceId = arcInvoice.Id;

            // Avalanche: Circle's USDC, a token with 6 decimals, final two blocks later.
            var avalancheInvoice = await CreateInvoice(api, store.Id, "USDC-AVAXC", "USDC", 10m);
            var avalanchePrompt = Assert.Single(await api.GetInvoicePaymentMethods(avalancheInvoice.Id));
            Assert.Contains(avalanchePrompt.Destination, AvalanchePool);
            Assert.Equal($"ethereum:{AvalancheChains.FujiUsdc}@43113/transfer?address={avalanchePrompt.Destination}&uint256=10000000", avalanchePrompt.PaymentLink);
            avalanche.AddTokenTransfer(out var avalancheTx, AvalancheChains.FujiUsdc, EvmPayer, avalanchePrompt.Destination, 10_000_000);
            avalanche.AddBlocks(2);
            await Eventually(async () => (await api.GetInvoice(avalancheInvoice.Id)).Status == InvoiceStatus.Settled, "the Avalanche invoice did not settle");
            var avalanchePayment = Assert.Single((await api.GetInvoicePaymentMethods(avalancheInvoice.Id)).Single().Payments);
            Assert.Equal(10m, avalanchePayment.Value);
            Assert.StartsWith(avalancheTx, avalanchePayment.Id);

            // Flare: an invoice in dollars, paid in FLR at the price Flare's own oracle gives: 10 USD at 0.005 USD is 2000 FLR.
            var flareInvoice = await CreateInvoice(api, store.Id, "FLR-FLARE", "USD", 10m);
            var flarePrompt = Assert.Single(await api.GetInvoicePaymentMethods(flareInvoice.Id));
            Assert.Contains(flarePrompt.Destination, FlarePool);
            Assert.Equal(0.005m, flarePrompt.Rate);
            Assert.Equal(2000m, flarePrompt.Due);
            Assert.Equal($"ethereum:{flarePrompt.Destination}@114?value=2000000000000000000000", flarePrompt.PaymentLink);
            flare.AddBlock(out var flareTx, EvmPayer, flarePrompt.Destination, BigInteger.Parse("2000000000000000000000"));
            flare.AddBlocks(2);
            await Eventually(async () => (await api.GetInvoice(flareInvoice.Id)).Status == InvoiceStatus.Settled, "the Flare invoice did not settle");
            var flarePayment = Assert.Single((await api.GetInvoicePaymentMethods(flareInvoice.Id)).Single().Payments);
            Assert.Equal(2000m, flarePayment.Value);
            Assert.StartsWith(flareTx!, flarePayment.Id);

            // Solana: two open invoices share the store's one address; each carries its own Solana Pay reference.
            var solInvoice = await CreateInvoice(api, store.Id, "SOL-SOLANA", "SOL", 1.5m);
            var solSecond = await CreateInvoice(api, store.Id, "SOL-SOLANA", "SOL", 1.5m);
            var solPrompt = Assert.Single(await api.GetInvoicePaymentMethods(solInvoice.Id));
            var solSecondPrompt = Assert.Single(await api.GetInvoicePaymentMethods(solSecond.Id));
            Assert.Equal(SolanaShop, solPrompt.Destination);
            Assert.Equal(SolanaShop, solSecondPrompt.Destination);
            var solReference = Reference(solPrompt.PaymentLink, $"solana:{SolanaShop}?amount=1.5");
            var solSecondReference = Reference(solSecondPrompt.PaymentLink, $"solana:{SolanaShop}?amount=1.5");
            Assert.NotEqual(solReference, solSecondReference);
            var solCheckout = await http.GetAsync(solInvoice.CheckoutLink);
            solCheckout.EnsureSuccessStatusCode();
            Assert.Contains("SOL", await solCheckout.Content.ReadAsStringAsync());

            // A transfer typed by hand names no invoice and settles none; the wallet's transfer with the second reference settles the second.
            solana.AddSolTransfer(out _, SolanaPayer, SolanaShop, 1_500_000_000);
            solana.AddSolTransfer(out var solTx, SolanaPayer, SolanaShop, 1_500_000_000, [solSecondReference]);
            solana.AddSlots(10);
            await Eventually(async () => (await api.GetInvoice(solSecond.Id)).Status == InvoiceStatus.Settled, "the second Solana invoice did not settle");
            var solPayment = Assert.Single((await api.GetInvoicePaymentMethods(solSecond.Id)).Single().Payments);
            Assert.Equal(1.5m, solPayment.Value);
            Assert.StartsWith(solTx, solPayment.Id);
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(solInvoice.Id)).Status);
            solanaInvoiceId = solSecond.Id;

            // USDC on Solana arrives at the wallet's token account; the wallet itself is not named by the transaction.
            var solUsdcInvoice = await CreateInvoice(api, store.Id, "USDC-SOLANA", "USDC", 10m);
            var solUsdcPrompt = Assert.Single(await api.GetInvoicePaymentMethods(solUsdcInvoice.Id));
            Assert.Equal(SolanaShop, solUsdcPrompt.Destination);
            var solUsdcReference = Reference(solUsdcPrompt.PaymentLink, $"solana:{SolanaShop}?amount=10");
            Assert.Contains($"&spl-token={FakeSolanaNode.Usdc}&reference=", solUsdcPrompt.PaymentLink);
            solana.AddTokenTransfer(out var solUsdcTx, FakeSolanaNode.Usdc, SolanaPayer, SolanaShop, 10_000_000, [solUsdcReference]);
            solana.AddSlots(10);
            await Eventually(async () => (await api.GetInvoice(solUsdcInvoice.Id)).Status == InvoiceStatus.Settled, "the USDC on Solana invoice did not settle");
            var solUsdcPayment = Assert.Single((await api.GetInvoicePaymentMethods(solUsdcInvoice.Id)).Single().Payments);
            Assert.Equal(10m, solUsdcPayment.Value);
            Assert.StartsWith(solUsdcTx, solUsdcPayment.Id);

            // With the address-only link no wallet can add a reference, so the invoice reserves an address and any transfer to it counts.
            await api.UpdateStorePaymentMethod(store.Id, "SOL-SOLANA",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(SolanaSpare), ["paymentLinkFormat"] = 2 } });
            var solPlainInvoice = await CreateInvoice(api, store.Id, "SOL-SOLANA", "SOL", 0.25m);
            var solPlainPrompt = Assert.Single(await api.GetInvoicePaymentMethods(solPlainInvoice.Id));
            Assert.Equal(SolanaSpare, solPlainPrompt.Destination);
            Assert.Equal(SolanaSpare, solPlainPrompt.PaymentLink);
            await Assert.ThrowsAsync<GreenfieldAPIException>(() => CreateInvoice(api, store.Id, "SOL-SOLANA", "SOL", 0.25m));   // the one address is reserved
            solana.AddSolTransfer(out var solPlainTx, SolanaPayer, SolanaSpare, 250_000_000);
            solana.AddSlots(10);
            await Eventually(async () => (await api.GetInvoice(solPlainInvoice.Id)).Status == InvoiceStatus.Settled, "the address-only Solana invoice did not settle");
            Assert.StartsWith(solPlainTx, Assert.Single((await api.GetInvoicePaymentMethods(solPlainInvoice.Id)).Single().Payments).Id);
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(solInvoice.Id)).Status);

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
            Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(nearInvoiceId)).Status);
            Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(arcInvoiceId)).Status);
            Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(solanaInvoiceId)).Status);
            var states = host.Services.GetRequiredService<ChainStateStore>();
            var neoxState = await states.Get("Chains.NEOX.Listener");
            Assert.NotNull(neoxState);
            Assert.True(neoxState.LastBlockIndex >= neox.Tip - 1);
            var bscState = await states.Get("Chains.BSC.Listener");
            Assert.NotNull(bscState);
            Assert.True(bscState.LastBlockIndex >= bsc.Tip - 7, $"BSC cursor {bscState.LastBlockIndex} is too far behind tip {bsc.Tip}");
            var nearState = await states.Get("Chains.NEAR.Listener");
            Assert.NotNull(nearState);
            Assert.True(nearState.LastBlockIndex >= near.Tip - 1, $"NEAR cursor {nearState.LastBlockIndex} is behind tip {near.Tip}");
            var arcState = await states.Get("Chains.ARC.Listener");
            Assert.NotNull(arcState);
            Assert.True(arcState.LastBlockIndex >= arc.Tip - 2, $"Arc cursor {arcState.LastBlockIndex} is too far behind tip {arc.Tip}");
            var solanaState = await states.Get("Chains.SOLANA.Listener");
            Assert.NotNull(solanaState);
            Assert.True(solanaState.LastBlockIndex >= (long)solana.Slot - 20, $"Solana cursor {solanaState.LastBlockIndex} is too far behind slot {solana.Slot}");
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

    /// <summary>The Solana Pay reference at the end of a checkout link that starts as expected.</summary>
    static string Reference(string? link, string expectedStart)
    {
        const string Marker = "&reference=";
        Assert.NotNull(link);
        Assert.StartsWith(expectedStart, link);
        var cut = link.LastIndexOf(Marker, StringComparison.Ordinal);
        Assert.True(cut > 0, $"'{link}' carries no reference");
        var reference = link[(cut + Marker.Length)..];
        Assert.True(SolanaAddress.TryNormalize(reference, out _), $"'{reference}' is not a Solana Pay reference");
        return reference;
    }

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
