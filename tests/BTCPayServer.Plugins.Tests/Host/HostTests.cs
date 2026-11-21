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
using BTCPayServer.Plugins.IoTeX;
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
/// Runs BTCPay Server in-process with all eight plugins loaded, a fresh PostgreSQL database and fake nodes, and takes
/// invoices from creation to settlement on Neo N3, Neo X, BNB Smart Chain (native BNB and a BEP-20 token), NEAR (NEAR
/// and USDC), Arc (USDC, the chain's coin), Avalanche (USDC), Flare (FLR priced by the FTSO), IoTeX (IOTX) and Solana (SOL and
/// USDC, by Solana Pay reference and by reserved address; its pay page hands out the unsigned transfer a browser wallet signs).
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
    static readonly string[] IoTeXPool = ["0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"];
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
        await using var iotex = new FakeEvmNode(chainId: 4690);
        await using var solana = new FakeSolanaNode();
        solana.AddWallet(SolanaShop);
        var solanaShopUsdc = solana.AddTokenAccount(SolanaShop, FakeSolanaNode.Usdc);
        solana.AddWallet(SolanaSpare);
        // The customer's wallet, as the pay page looks at it before it builds a payment: 10 SOL and 100 USDC.
        solana.AddWallet(SolanaPayer, 10_000_000_000);
        var solanaPayerUsdc = solana.AddTokenAccount(SolanaPayer, FakeSolanaNode.Usdc, amount: 100_000_000);
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
                typeof(ArcPlugin).Assembly.Location, typeof(AvalanchePlugin).Assembly.Location, typeof(FlarePlugin).Assembly.Location, typeof(SolanaPlugin).Assembly.Location,
                typeof(IoTeXPlugin).Assembly.Location),
            ["NEO_JSONRPC_URI"] = neo.Url,
            ["NEOX_JSONRPC_URI"] = neox.Url,
            ["BSC_JSONRPC_URI"] = bsc.Url,
            ["BSC_USDT_CONTRACT"] = Usdt,
            ["NEAR_JSONRPC_URI"] = near.Url,
            ["ARC_JSONRPC_URI"] = arc.Url,
            ["AVAXC_JSONRPC_URI"] = avalanche.Url,
            ["FLARE_JSONRPC_URI"] = flare.Url,
            ["IOTEX_JSONRPC_URI"] = iotex.Url,
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
                         "FLR-FLARE", "FXRP-FLARE", "IOTX-IOTEX", "SOL-SOLANA", "USDC-SOLANA",
                     })
                Assert.True(handlers.TryGetValue(new PaymentMethodId(pmi), out _), $"{pmi} handler not registered");
            Assert.False(handlers.TryGetValue(new PaymentMethodId("USDC-BSC"), out _), "USDC-BSC has no testnet contract and must not be offered");
            Assert.False(handlers.TryGetValue(new PaymentMethodId("USDT-NEAR"), out _), "USDT-NEAR has no testnet contract and must not be offered");
            foreach (var pmi in new[] { "USDT-AVAXC", "USDT0-FLARE", "USDT-IOTEX", "USDT-SOLANA" })
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
            await api.UpdateStorePaymentMethod(store.Id, "IOTX-IOTEX",
                new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(IoTeXPool) } });
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
            host.Services.GetRequiredService<ChainActivationService<IoTeXPlugin>>().Invalidate();
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
            var iotexRpc = host.Services.GetRequiredService<ChainRpcProvider<IoTeXPlugin>>();
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
                await iotexRpc.UpdateSummary("IOTEX", CancellationToken.None);
                return iotexRpc.IsAvailable("IOTEX");
            }, "IoTeX never became available");
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

            // IoTeX: the chain's coin, a plain transfer as IoTeX's own wallets send it, read from the block and its receipt,
            // final two blocks later.
            var iotexInvoice = await CreateInvoice(api, store.Id, "IOTX-IOTEX", "IOTX", 250m);
            var iotexPrompt = Assert.Single(await api.GetInvoicePaymentMethods(iotexInvoice.Id));
            Assert.Contains(iotexPrompt.Destination, IoTeXPool);
            Assert.Equal($"ethereum:{iotexPrompt.Destination}@4690?value=250000000000000000000", iotexPrompt.PaymentLink);
            iotex.AddBlock(out var iotexTx, EvmPayer, iotexPrompt.Destination, BigInteger.Parse("250000000000000000000"));
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(iotexInvoice.Id)).Status);   // not final yet
            iotex.AddBlocks(2);
            await Eventually(async () => (await api.GetInvoice(iotexInvoice.Id)).Status == InvoiceStatus.Settled, "the IoTeX invoice did not settle");
            var iotexPayment = Assert.Single((await api.GetInvoicePaymentMethods(iotexInvoice.Id)).Single().Payments);
            Assert.Equal(250m, iotexPayment.Value);
            Assert.StartsWith(iotexTx!, iotexPayment.Id);

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

            // The checkout keeps the Solana Pay link in its QR code, for a wallet app to scan, and its button opens the
            // plugin's pay page, for a wallet in the browser, which cannot scan.
            var solPagePath = $"/plugins/solana/pay/{solInvoice.Id}/SOL-SOLANA";
            var solModel = JObject.Parse(await http.GetStringAsync($"/i/{solInvoice.Id}/status?paymentMethodId=SOL-SOLANA"));
            Assert.Equal(solPrompt.PaymentLink, solModel["invoiceBitcoinUrlQR"]!.Value<string>());
            Assert.EndsWith(solPagePath, solModel["invoiceBitcoinUrl"]!.Value<string>());
            Assert.True(solModel["showPayInWalletButton"]!.Value<bool>());
            var solPage = await http.GetAsync(solPagePath);
            solPage.EnsureSuccessStatusCode();
            var solPageHtml = await solPage.Content.ReadAsStringAsync();
            Assert.Contains(SolanaShop, solPageHtml);
            Assert.Contains(solReference, solPageHtml);
            Assert.Contains("Pay 1.5 SOL", solPageHtml);
            Assert.Contains("data-chain=\"solana:devnet\"", solPageHtml);
            Assert.Contains($"data-transaction-url=\"{solPagePath}/transaction\"", solPageHtml);
            // The page's script is served from the plugin's assembly, at the address the page names.
            var scriptAddress = System.Text.RegularExpressions.Regex.Match(solPageHtml, "<script[^>]*\\ssrc=\"(/Resources/solanapay/app\\.js[^\"]*)\"").Groups[1].Value;
            Assert.StartsWith("/Resources/solanapay/app.js", scriptAddress);
            Assert.Contains("wallet-standard:app-ready", await http.GetStringAsync(System.Net.WebUtility.HtmlDecode(scriptAddress)));

            // The page's script posts the account of the wallet that connected and gets the invoice's transfer for it:
            // unsigned, from that account to the store's wallet, the amount due, the invoice's reference in it.
            async Task<(System.Net.HttpStatusCode Status, JObject Body)> Prepare(string invoiceId, string method, string? account)
            {
                var answer = await http.PostAsync($"/plugins/solana/pay/{invoiceId}/{method}/transaction",
                    new StringContent(new JObject { ["account"] = account }.ToString(), System.Text.Encoding.UTF8, "application/json"));
                var text = await answer.Content.ReadAsStringAsync();
                return (answer.StatusCode, text.TrimStart().StartsWith('{') ? JObject.Parse(text) : new JObject());
            }
            var refused = await Prepare(solInvoice.Id, "SOL-SOLANA", "not-an-account");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.Status);
            Assert.Contains("did not name a Solana account", refused.Body["message"]!.Value<string>());
            // An account far too long for 32 bytes of Base58 is refused on its length, and a body far too long for
            // an account never reaches the action at all. Decoding either would cost time squared in the length.
            var longAccount = await Prepare(solInvoice.Id, "SOL-SOLANA", new string('z', 900));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, longAccount.Status);
            var hugeBody = await http.PostAsync($"/plugins/solana/pay/{solInvoice.Id}/SOL-SOLANA/transaction",
                new StringContent(new JObject { ["account"] = new string('z', 200_000) }.ToString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, hugeBody.StatusCode);
            var prepared = await Prepare(solInvoice.Id, "SOL-SOLANA", SolanaPayer);
            Assert.Equal(System.Net.HttpStatusCode.OK, prepared.Status);
            var solWire = SolanaWire.Parse(Convert.FromBase64String(prepared.Body["transaction"]!.Value<string>()!));
            Assert.Equal(new byte[64], Assert.Single(solWire.Signatures));
            Assert.Equal(solana.Blockhash, solWire.Blockhash);
            var solTransfer = Assert.Single(solWire.Instructions);
            Assert.Equal(SolanaAddress.SystemProgram, solTransfer.Program);
            Assert.Equal([SolanaPayer, SolanaShop, solReference], solTransfer.Accounts);
            Assert.Equal(1_500_000_000ul, SolanaWire.SystemTransferLamports(solTransfer.Data));
            // A wallet that cannot pay is told why, in words; nothing is built.
            var empty = await Prepare(solSecond.Id, "SOL-SOLANA", FakeSolanaNode.Address("host-empty-wallet"));
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, empty.Status);
            Assert.Equal("This wallet holds no SOL on Solana devnet.", empty.Body["message"]!.Value<string>());
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Prepare("no-such-invoice", "SOL-SOLANA", SolanaPayer)).Status);

            // A transfer typed by hand names no invoice and settles none; the wallet's transfer with the second reference settles the
            // second. It comes in the transaction format wallets also send since September 2026, version 1.
            solana.AddSolTransfer(out _, SolanaPayer, SolanaShop, 1_500_000_000);
            solana.AddSolTransfer(out var solTx, SolanaPayer, SolanaShop, 1_500_000_000, [solSecondReference], version: 1);
            solana.AddSlots(10);
            await Eventually(async () => (await api.GetInvoice(solSecond.Id)).Status == InvoiceStatus.Settled, "the second Solana invoice did not settle");
            var solPayment = Assert.Single((await api.GetInvoicePaymentMethods(solSecond.Id)).Single().Payments);
            Assert.Equal(1.5m, solPayment.Value);
            Assert.StartsWith(solTx, solPayment.Id);
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(solInvoice.Id)).Status);
            solanaInvoiceId = solSecond.Id;
            // A paid invoice takes no further payment from the page.
            var late = await Prepare(solSecond.Id, "SOL-SOLANA", SolanaPayer);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, late.Status);
            Assert.Equal("This invoice is paid already.", late.Body["message"]!.Value<string>());
            // ... and its pay page leads back to the checkout, which shows it as paid.
            var paidPage = await http.GetAsync($"/plugins/solana/pay/{solSecond.Id}/SOL-SOLANA");
            paidPage.EnsureSuccessStatusCode();
            Assert.StartsWith($"/i/{solSecond.Id}", paidPage.RequestMessage!.RequestUri!.AbsolutePath);

            // USDC on Solana arrives at the wallet's token account; the wallet itself is not named by the transaction.
            var solUsdcInvoice = await CreateInvoice(api, store.Id, "USDC-SOLANA", "USDC", 10m);
            var solUsdcPrompt = Assert.Single(await api.GetInvoicePaymentMethods(solUsdcInvoice.Id));
            Assert.Equal(SolanaShop, solUsdcPrompt.Destination);
            var solUsdcReference = Reference(solUsdcPrompt.PaymentLink, $"solana:{SolanaShop}?amount=10");
            Assert.Contains($"&spl-token={FakeSolanaNode.Usdc}&reference=", solUsdcPrompt.PaymentLink);
            // From the pay page a token is paid with a checked transfer between the two wallets' token accounts.
            var preparedUsdc = await Prepare(solUsdcInvoice.Id, "USDC-SOLANA", SolanaPayer);
            Assert.Equal(System.Net.HttpStatusCode.OK, preparedUsdc.Status);
            var usdcTransfer = Assert.Single(SolanaWire.Parse(Convert.FromBase64String(preparedUsdc.Body["transaction"]!.Value<string>()!)).Instructions);
            Assert.Equal(SolanaAddress.TokenProgram, usdcTransfer.Program);
            Assert.Equal([solanaPayerUsdc, FakeSolanaNode.Usdc, solanaShopUsdc, SolanaPayer, solUsdcReference], usdcTransfer.Accounts);
            Assert.Equal((10_000_000ul, (byte)6), SolanaWire.TransferChecked(usdcTransfer.Data));
            // The node now keeps its newest slots out of the finalized view, which is the view the listener reads:
            // the payment below is on the chain, and the listener cannot see it yet. The page must prepare no second
            // one, and must be able to say what became of the first.
            solana.FinalizedLag = 40;
            solana.AddTokenTransfer(out var solUsdcTx, FakeSolanaNode.Usdc, SolanaPayer, SolanaShop, 10_000_000, [solUsdcReference]);
            var pending = await Prepare(solUsdcInvoice.Id, "USDC-SOLANA", SolanaPayer);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, pending.Status);
            Assert.Equal(solUsdcTx, pending.Body["signature"]!.Value<string>());
            var sent = await http.GetStringAsync($"/plugins/solana/pay/{solUsdcInvoice.Id}/USDC-SOLANA/sent?signature={solUsdcTx}&lastValidBlockHeight={solana.Slot + 150}");
            Assert.Equal("paid", JObject.Parse(sent)["state"]!.Value<string>());
            // A signature nobody sent, whose blockhash the network would no longer take, is gone for good.
            var gone = await http.GetStringAsync($"/plugins/solana/pay/{solUsdcInvoice.Id}/USDC-SOLANA/sent?signature={FakeSolanaNode.Signature("never sent")}&lastValidBlockHeight=1");
            Assert.Equal("gone", JObject.Parse(gone)["state"]!.Value<string>());
            Assert.Equal(System.Net.HttpStatusCode.BadRequest,
                (await http.GetAsync($"/plugins/solana/pay/{solUsdcInvoice.Id}/USDC-SOLANA/sent?signature=not-a-signature")).StatusCode);
            solana.FinalizedLag = 0;
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
            // Without a reference there is nothing for the pay page to put into a transfer: no page, and no button to it.
            var solPlainModel = JObject.Parse(await http.GetStringAsync($"/i/{solPlainInvoice.Id}/status?paymentMethodId=SOL-SOLANA"));
            Assert.Equal(SolanaSpare, solPlainModel["invoiceBitcoinUrl"]!.Value<string>());
            Assert.False(solPlainModel["showPayInWalletButton"]!.Value<bool>());
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync($"/plugins/solana/pay/{solPlainInvoice.Id}/SOL-SOLANA")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Prepare(solPlainInvoice.Id, "SOL-SOLANA", SolanaPayer)).Status);
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

            // Rows of invoices that can no longer be paid are swept by the end of the payment window stored with each claim.
            // No row of this run is old enough to be swept, so read the stored value here the way the sweep does: every
            // claim must carry a time PostgreSQL understands, or the query fails.
            await using (var probe = new NpgsqlConnection(builder.ConnectionString))
            {
                await probe.OpenAsync();
                await using var windows = new NpgsqlCommand(
                    """SELECT count(*) FROM "Settings" WHERE starts_with("Id", 'Chains.') AND ("Value"->>'MonitoringExpiration')::timestamptz > now()""", probe);
                var open = (long)(await windows.ExecuteScalarAsync())!;
                Assert.True(open >= 20, $"only {open} claims carry the end of their payment window");
            }

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
