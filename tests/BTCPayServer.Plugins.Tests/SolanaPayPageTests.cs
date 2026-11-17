using System.Numerics;
using BTCPayServer.Client.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.BNBChain;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Solana;
using BTCPayServer.Plugins.NEAR;
using BTCPayServer.Plugins.Solana;
using BTCPayServer.Plugins.Solana.Controllers;
using BTCPayServer.Plugins.Tests.Host;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NeoPaymentWatcher.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests;

/// <summary>
/// The Solana pay page: the unsigned transfer the server builds for a wallet in the browser, what it refuses to
/// build and why, and which link the checkout puts where.
/// </summary>
public class SolanaPayPageTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();
    static readonly ChainDefinition Devnet = ChainDefaults.ApplyConfiguration(SolanaChains.Cluster(mainnet: false), Empty);
    static readonly PaymentMethodDefinition Sol = Devnet.PaymentMethods.Single(m => m.Currency == "SOL");
    static readonly PaymentMethodDefinition Usdc = Devnet.PaymentMethods.Single(m => m.Currency == "USDC");

    static readonly string Shop = FakeSolanaNode.Address("pay-shop");
    static readonly string Customer = FakeSolanaNode.Address("pay-customer");
    static readonly string Stranger = FakeSolanaNode.Address("pay-stranger");
    static readonly string Blockhash = FakeSolanaNode.Address("pay-blockhash");
    const ulong OneSol = 1_000_000_000;

    static SolanaChainClient Client(FakeSolanaNode node) =>
        new((Devnet with { JsonRpcUris = [node.Url] }).ToChainProfile(), NullLogger<SolanaChainClient>.Instance);

    [Fact]
    public void A_sol_payment_is_one_system_transfer_with_the_reference_as_a_read_only_account()
    {
        var reference = SolanaAddress.NewReference();

        var bytes = SolanaPayTransaction.Transfer(Customer, Shop, 50_000_000, reference, Blockhash);
        var wire = SolanaWire.Parse(bytes);

        // One signature slot, left empty for the wallet; the paying wallet is the only signer and pays the fee.
        Assert.Equal(248, bytes.Length);
        Assert.Equal(new byte[64], Assert.Single(wire.Signatures));
        Assert.Equal((1, 0, 2), (wire.Signers, wire.ReadOnlySigned, wire.ReadOnlyUnsigned));
        Assert.Equal([Customer, Shop, reference, SolanaAddress.SystemProgram], wire.Accounts);
        Assert.Equal(Blockhash, wire.Blockhash);

        var transfer = Assert.Single(wire.Instructions);
        Assert.Equal(SolanaAddress.SystemProgram, transfer.Program);
        Assert.Equal([Customer, Shop, reference], transfer.Accounts);
        Assert.Equal(50_000_000ul, SolanaWire.SystemTransferLamports(transfer.Data));
        Assert.Equal("0200000080f0fa0200000000", Convert.ToHexStringLower(transfer.Data));

        Assert.True(wire.IsSigner(wire.IndexOf(Customer)) && wire.IsWritable(wire.IndexOf(Customer)));
        Assert.True(!wire.IsSigner(wire.IndexOf(Shop)) && wire.IsWritable(wire.IndexOf(Shop)));
        Assert.True(!wire.IsSigner(wire.IndexOf(reference)) && !wire.IsWritable(wire.IndexOf(reference)));
        Assert.False(wire.IsWritable(wire.IndexOf(SolanaAddress.SystemProgram)));
    }

    [Fact]
    public void A_token_payment_is_a_checked_transfer_between_the_two_wallets_token_accounts()
    {
        var reference = SolanaAddress.NewReference();
        var mint = SolanaChains.DevnetUsdc;
        var source = SolanaAddress.AssociatedTokenAddress(Customer, mint, SolanaAddress.TokenProgram);
        var destination = SolanaAddress.AssociatedTokenAddress(Shop, mint, SolanaAddress.TokenProgram);

        var bytes = SolanaPayTransaction.TokenTransfer(Customer, source, mint, destination, SolanaAddress.TokenProgram, 12_500_000, 6, reference, Blockhash);
        var wire = SolanaWire.Parse(bytes);

        Assert.Equal(312, bytes.Length);
        Assert.Equal(new byte[64], Assert.Single(wire.Signatures));
        Assert.Equal((1, 0, 3), (wire.Signers, wire.ReadOnlySigned, wire.ReadOnlyUnsigned));
        Assert.Equal([Customer, source, destination, mint, reference, SolanaAddress.TokenProgram], wire.Accounts);
        Assert.Equal(Blockhash, wire.Blockhash);

        // The program wants: source, mint, destination, owner; the reference follows them.
        var transfer = Assert.Single(wire.Instructions);
        Assert.Equal(SolanaAddress.TokenProgram, transfer.Program);
        Assert.Equal([source, mint, destination, Customer, reference], transfer.Accounts);
        Assert.Equal((12_500_000ul, (byte)6), SolanaWire.TransferChecked(transfer.Data));

        Assert.True(wire.IsSigner(wire.IndexOf(Customer)));
        Assert.True(wire.IsWritable(wire.IndexOf(source)) && wire.IsWritable(wire.IndexOf(destination)));
        Assert.All(new[] { source, destination, mint, reference, SolanaAddress.TokenProgram }, account => Assert.False(wire.IsSigner(wire.IndexOf(account))));
        Assert.All(new[] { mint, reference, SolanaAddress.TokenProgram }, account => Assert.False(wire.IsWritable(wire.IndexOf(account))));
        // The wallets themselves are not named: a token moves between token accounts.
        Assert.DoesNotContain(Shop, wire.Accounts);
    }

    [Fact]
    public void Amounts_use_all_64_bits_and_the_accounts_of_a_transfer_must_be_distinct_addresses()
    {
        var reference = SolanaAddress.NewReference();

        var largest = SolanaWire.Parse(SolanaPayTransaction.Transfer(Customer, Shop, ulong.MaxValue, reference, Blockhash));
        Assert.Equal(ulong.MaxValue, SolanaWire.SystemTransferLamports(largest.Instructions[0].Data));
        var smallest = SolanaWire.Parse(SolanaPayTransaction.Transfer(Customer, Shop, 1, reference, Blockhash));
        Assert.Equal(1ul, SolanaWire.SystemTransferLamports(smallest.Instructions[0].Data));

        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.Transfer(Shop, Shop, 1, reference, Blockhash));
        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.Transfer(Customer, Shop, 1, Customer, Blockhash));
        Assert.Throws<FormatException>(() => SolanaPayTransaction.Transfer("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", Shop, 1, reference, Blockhash));
        Assert.Throws<FormatException>(() => SolanaPayTransaction.Transfer(Customer, Shop, 1, reference, "not-a-blockhash"));
    }

    [Fact]
    public async Task The_server_prepares_a_sol_payment_for_the_wallet_that_connected()
    {
        await using var node = new FakeSolanaNode();
        node.AddWallet(Shop);
        node.AddWallet(Customer, 2 * OneSol);
        var client = Client(node);
        var reference = SolanaAddress.NewReference();

        var prepared = await client.PreparePaymentAsync(Customer, Shop, Sol, 50_000_000, reference, CancellationToken.None);

        Assert.Null(prepared.Refusal);
        var wire = SolanaWire.Parse(prepared.Transaction!);
        Assert.Equal(node.Blockhash, wire.Blockhash);
        Assert.Equal([Customer, Shop, reference], wire.Instructions[0].Accounts);
        Assert.Equal(50_000_000ul, SolanaWire.SystemTransferLamports(wire.Instructions[0].Data));

        // A wallet may spend everything it holds: nothing left is fine, a little left is not.
        var all = FakeSolanaNode.Address("pays-everything");
        node.AddWallet(all, 50_000_000 + SolanaChainClient.SignatureFee);
        Assert.NotNull((await client.PreparePaymentAsync(all, Shop, Sol, 50_000_000, reference, CancellationToken.None)).Transaction);
        // The first payment to a wallet that has never held SOL works when it is large enough for the account to exist.
        var fresh = FakeSolanaNode.Address("fresh-shop");
        Assert.NotNull((await client.PreparePaymentAsync(Customer, fresh, Sol, FakeSolanaNode.RentExemptMinimum, reference, CancellationToken.None)).Transaction);
    }

    [Fact]
    public async Task A_sol_payment_that_cannot_work_is_refused_in_words_instead_of_built()
    {
        await using var node = new FakeSolanaNode();
        node.AddWallet(Shop);
        node.AddWallet(Customer, 2 * OneSol);
        var client = Client(node);
        var reference = SolanaAddress.NewReference();
        async Task<string> Refusal(string? payer, string recipient, BigInteger units)
        {
            var prepared = await client.PreparePaymentAsync(payer, recipient, Sol, units, reference, CancellationToken.None);
            Assert.Null(prepared.Transaction);
            return prepared.Refusal!;
        }

        Assert.Contains("did not name a Solana account", await Refusal("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", Shop, 50_000_000));
        Assert.Contains("did not name a Solana account", await Refusal(null, Shop, 50_000_000));
        Assert.Contains("store's own wallet", await Refusal(Shop, Shop, 50_000_000));
        Assert.Contains("Nothing is left to pay", await Refusal(Customer, Shop, 0));
        Assert.Contains("Nothing is left to pay", await Refusal(Customer, Shop, (BigInteger)ulong.MaxValue + 1));

        Assert.Equal("This wallet holds no SOL on Solana devnet.", await Refusal(FakeSolanaNode.Address("empty-wallet"), Shop, 50_000_000));

        var poor = FakeSolanaNode.Address("poor-wallet");
        node.AddWallet(poor, 40_000_000);
        Assert.Equal("This wallet holds 0.04 SOL. The invoice is for 0.05 SOL, plus a small network fee.", await Refusal(poor, Shop, 50_000_000));
        // Enough for the amount but not for the fee on top of it.
        var exact = FakeSolanaNode.Address("exact-wallet");
        node.AddWallet(exact, 50_000_000);
        Assert.Contains("plus a small network fee", await Refusal(exact, Shop, 50_000_000));

        // What would be left is below the least an account may hold: the network would refuse the transfer.
        var tight = FakeSolanaNode.Address("tight-wallet");
        node.AddWallet(tight, 50_000_000 + SolanaChainClient.SignatureFee + 100_000);
        Assert.Equal("After this payment the wallet would keep 0.0001 SOL, and an account may hold nothing or at least 0.00065024 SOL. Add a little SOL to the wallet, or pay from another one.",
            await Refusal(tight, Shop, 50_000_000));

        // A wallet that has never held SOL cannot start with less than an account must hold.
        Assert.Equal("The store's wallet has never held SOL, and the network takes no first payment below 0.00065024 SOL. Tell the store.",
            await Refusal(Customer, FakeSolanaNode.Address("fresh-shop"), 500_000));

        var program = FakeSolanaNode.Address("a-program-account");
        node.AddProgramAccount(program, SolanaAddress.TokenProgram);
        Assert.Contains("belongs to a program", await Refusal(program, Shop, 50_000_000));
    }

    [Fact]
    public async Task The_server_prepares_a_token_payment_between_the_token_accounts_or_says_what_is_missing()
    {
        await using var node = new FakeSolanaNode();
        node.AddWallet(Shop);
        var shopUsdc = node.AddTokenAccount(Shop, FakeSolanaNode.Usdc);
        node.AddWallet(Customer, OneSol);
        var customerUsdc = node.AddTokenAccount(Customer, FakeSolanaNode.Usdc, amount: 20_000_000);
        var client = Client(node);
        var reference = SolanaAddress.NewReference();
        async Task<string> Refusal(string payer, string recipient, PaymentMethodDefinition method, BigInteger units)
        {
            var prepared = await client.PreparePaymentAsync(payer, recipient, method, units, reference, CancellationToken.None);
            Assert.Null(prepared.Transaction);
            return prepared.Refusal!;
        }

        var prepared = await client.PreparePaymentAsync(Customer, Shop, Usdc, 12_500_000, reference, CancellationToken.None);
        Assert.Null(prepared.Refusal);
        var wire = SolanaWire.Parse(prepared.Transaction!);
        Assert.Equal(node.Blockhash, wire.Blockhash);
        var transfer = Assert.Single(wire.Instructions);
        Assert.Equal(SolanaAddress.TokenProgram, transfer.Program);
        Assert.Equal([customerUsdc, FakeSolanaNode.Usdc, shopUsdc, Customer, reference], transfer.Accounts);
        Assert.Equal((12_500_000ul, (byte)6), SolanaWire.TransferChecked(transfer.Data));
        // All of it may go.
        Assert.NotNull((await client.PreparePaymentAsync(Customer, Shop, Usdc, 20_000_000, reference, CancellationToken.None)).Transaction);

        Assert.Equal("This wallet holds 20 USDC. The invoice is for 20.000001 USDC.", await Refusal(Customer, Shop, Usdc, 20_000_001));

        var noFee = FakeSolanaNode.Address("token-wallet-without-sol");
        node.AddWallet(noFee, 1_000);
        node.AddTokenAccount(noFee, FakeSolanaNode.Usdc, amount: 20_000_000);
        Assert.Equal("This wallet has no SOL for the network fee, about 0.000005 SOL.", await Refusal(noFee, Shop, Usdc, 12_500_000));
        Assert.Contains("no SOL for the network fee", await Refusal(FakeSolanaNode.Address("no-such-wallet"), Shop, Usdc, 12_500_000));

        // The fee is paid in SOL, and what it leaves must be nothing or enough for the account to go on existing.
        var feeOnly = FakeSolanaNode.Address("token-wallet-with-the-fee-only");
        node.AddWallet(feeOnly, SolanaChainClient.SignatureFee);
        node.AddTokenAccount(feeOnly, FakeSolanaNode.Usdc, amount: 20_000_000);
        Assert.NotNull((await client.PreparePaymentAsync(feeOnly, Shop, Usdc, 12_500_000, reference, CancellationToken.None)).Transaction);
        var thin = FakeSolanaNode.Address("token-wallet-with-little-sol");
        node.AddWallet(thin, SolanaChainClient.SignatureFee + 100_000);
        node.AddTokenAccount(thin, FakeSolanaNode.Usdc, amount: 20_000_000);
        Assert.Equal("After this payment the wallet would keep 0.0001 SOL, and an account may hold nothing or at least 0.00065024 SOL. Add a little SOL to the wallet, or pay from another one.",
            await Refusal(thin, Shop, Usdc, 12_500_000));

        var noUsdc = FakeSolanaNode.Address("wallet-without-usdc");
        node.AddWallet(noUsdc, OneSol);
        Assert.Equal("This wallet holds no USDC on Solana devnet.", await Refusal(noUsdc, Shop, Usdc, 12_500_000));

        var frozen = FakeSolanaNode.Address("wallet-with-frozen-usdc");
        node.AddWallet(frozen, OneSol);
        node.AddTokenAccount(frozen, FakeSolanaNode.Usdc, frozen: true, amount: 20_000_000);
        Assert.Contains("frozen by the token's issuer", await Refusal(frozen, Shop, Usdc, 12_500_000));

        // The store's side: its token account is gone, or frozen.
        var closedShop = FakeSolanaNode.Address("shop-without-usdc-account");
        node.AddWallet(closedShop);
        Assert.Equal("The store's wallet cannot receive USDC right now. Tell the store.", await Refusal(Customer, closedShop, Usdc, 12_500_000));
        var frozenShop = FakeSolanaNode.Address("shop-with-frozen-usdc");
        node.AddWallet(frozenShop);
        node.AddTokenAccount(frozenShop, FakeSolanaNode.Usdc, frozen: true);
        Assert.Contains("cannot receive USDC", await Refusal(Customer, frozenShop, Usdc, 12_500_000));

        // A token set up with other decimals than the chain says would be paid a thousand times off: nothing is built.
        Assert.Contains("set up with 9 decimals but has 6", await Refusal(Customer, Shop, Usdc with { Decimals = 9 }, 12_500_000));
    }

    [Fact]
    public async Task A_payment_on_its_way_is_one_that_really_paid_the_store_and_is_not_scanned_past()
    {
        await using var node = new FakeSolanaNode();
        node.AddWallet(Shop);
        node.AddTokenAccount(Shop, FakeSolanaNode.Usdc);
        var client = Client(node);
        var reference = SolanaAddress.NewReference();
        var other = SolanaAddress.NewReference();
        var none = new HashSet<string>(StringComparer.Ordinal);
        Task<string?> Pending(string tokenHash, uint scannedThrough, IReadOnlySet<string>? recorded = null) =>
            client.PendingPaymentAsync(Shop, reference, tokenHash, scannedThrough, recorded ?? none, CancellationToken.None);

        Assert.Null(await Pending(TokenRegistry.Native, (uint)node.Slot));

        // None of these is a payment of this invoice in SOL.
        node.AddSolTransfer(out _, Customer, Shop, 50_000_000, [reference], failed: true);   // it failed: nothing moved
        node.AddSolTransfer(out _, Customer, Shop, 50_000_000, [other]);                     // another invoice's reference
        node.AddSolTransfer(out _, Customer, Stranger, 50_000_000, [reference]);             // the money went elsewhere
        node.AddTokenTransfer(out var inUsdc, FakeSolanaNode.Usdc, Customer, Shop, 1_000_000, [reference]);   // another currency
        Assert.Null(await Pending(TokenRegistry.Native, (uint)node.Slot));
        // That last one is this invoice's payment when the invoice is the USDC one.
        Assert.Equal(inUsdc, await Pending(FakeSolanaNode.Usdc, (uint)node.Slot));

        // A transfer that merely names the reference is not one either: anyone who saw the checkout knows it.
        node.AddSolTransfer(out _, Customer, Stranger, 1, [reference]);
        Assert.Null(await Pending(TokenRegistry.Native, (uint)node.Slot));

        node.AddSolTransfer(out var sent, Customer, Shop, 50_000_000, [reference]);
        var at = (uint)node.Slot;
        Assert.Equal(sent, await Pending(TokenRegistry.Native, at));

        // What the listener has scanned past it will never record, so it is not on its way any more. The window is
        // the listener's own cursor and the span it reads again each turn, not a clock.
        Assert.Equal(sent, await Pending(TokenRegistry.Native, at + SolanaChainClient.Overlap));
        Assert.Null(await Pending(TokenRegistry.Native, at + SolanaChainClient.Overlap + 1));

        // Nor is one the listener has already recorded: a partly paid invoice still takes the rest.
        Assert.Null(await Pending(TokenRegistry.Native, at, new HashSet<string>(StringComparer.Ordinal) { sent }));
    }

    [Fact]
    public async Task A_sent_transaction_is_on_its_way_until_the_chain_has_it_or_the_height_it_named_has_passed()
    {
        await using var node = new FakeSolanaNode();
        node.AddWallet(Shop);
        var client = Client(node);
        var reference = SolanaAddress.NewReference();
        var height = node.Slot + 150;

        node.AddSolTransfer(out var paid, Customer, Shop, 50_000_000, [reference]);
        node.AddSolTransfer(out var failed, Customer, Shop, 50_000_000, [reference], failed: true);
        var never = FakeSolanaNode.Address("a-signature-nobody-sent");

        Assert.Equal(SolanaSignatureState.Paid, await client.SignatureStateAsync(paid, height, CancellationToken.None));
        Assert.Equal(SolanaSignatureState.Failed, await client.SignatureStateAsync(failed, height, CancellationToken.None));
        // Unknown to the chain, and the blockhash it named is still good: it may yet arrive.
        Assert.Equal(SolanaSignatureState.OnItsWay, await client.SignatureStateAsync(never, node.Slot + 150, CancellationToken.None));
        // Unknown, and the network no longer takes it.
        Assert.Equal(SolanaSignatureState.Gone, await client.SignatureStateAsync(never, 1, CancellationToken.None));
        // A page that learned of a payment from the server, not from a wallet, knows no height: it waits instead of
        // telling a customer that a payment the server has just seen on the chain is not coming.
        Assert.Equal(SolanaSignatureState.OnItsWay, await client.SignatureStateAsync(never, 0, CancellationToken.None));
    }

    [Fact]
    public void An_invoice_that_is_paid_expired_or_invalid_takes_no_payment_from_the_page()
    {
        var now = DateTimeOffset.UtcNow;
        var later = now.AddMinutes(10);

        Assert.Null(UISolanaPayController.NotPayable(InvoiceStatus.New, later, now, 0.05m));
        Assert.Equal("This invoice is paid already.", UISolanaPayController.NotPayable(InvoiceStatus.Settled, later, now, 0m));
        Assert.Equal("This invoice is paid already.", UISolanaPayController.NotPayable(InvoiceStatus.Processing, later, now, 0m));
        Assert.StartsWith("This invoice has expired.", UISolanaPayController.NotPayable(InvoiceStatus.Expired, later, now, 0.05m));
        Assert.StartsWith("This invoice has expired.", UISolanaPayController.NotPayable(InvoiceStatus.New, now, now, 0.05m));
        Assert.StartsWith("This invoice is not valid any more.", UISolanaPayController.NotPayable(InvoiceStatus.Invalid, later, now, 0.05m));
        Assert.Equal("Nothing is left to pay on this invoice.", UISolanaPayController.NotPayable(InvoiceStatus.New, later, now, 0m));
    }

    [Fact]
    public void The_ceiling_caps_what_the_whole_server_prepares_in_a_window()
    {
        var start = DateTimeOffset.UtcNow;
        var ceiling = new PayPageCeiling(window: TimeSpan.FromSeconds(10), perWindow: 3);

        Assert.True(ceiling.TryEnter(start));
        Assert.True(ceiling.TryEnter(start.AddSeconds(1)));
        Assert.True(ceiling.TryEnter(start.AddSeconds(2)));
        Assert.False(ceiling.TryEnter(start.AddSeconds(3)));                // this window is spent
        Assert.False(ceiling.TryEnter(start.AddSeconds(9)));
        Assert.True(ceiling.TryEnter(start.AddSeconds(10)));                // a new window
    }

    [Fact]
    public void An_account_that_is_not_32_bytes_of_base58_is_refused_without_decoding_it()
    {
        // Decoding Base58 costs time squared in the length, and this runs on what a stranger's browser posted.
        // Nothing of 32 bytes is shorter than 32 characters or longer than 44, so the rest never reaches the decoder.
        var huge = new string('z', 200_000);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(SolanaAddress.TryNormalize(huge, out _));
        Assert.False(SolanaAddress.TryNormalize(new string('1', 45), out _));
        Assert.False(SolanaAddress.TryNormalize(new string('1', 31), out _));
        Assert.False(SolanaAddress.TryNormalize("", out _));
        Assert.False(SolanaAddress.TryNormalize(null, out _));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"refusing took {clock.Elapsed.TotalSeconds:0.0} s, so the length is not checked first");

        // The two ends of the range are addresses, and still are.
        Assert.True(SolanaAddress.TryNormalize(new string('1', 32), out var zeros));           // 32 zero bytes
        Assert.Equal(new string('1', 32), zeros);
        Assert.True(SolanaAddress.TryNormalize("JEKNVnkbo3jma5nREBBJCDoXFVeKkD56V3xKrvRmWxFG", out var largest));   // 32 bytes of 0xff
        Assert.Equal(44, largest.Length);
        Assert.True(SolanaAddress.TryNormalize(Shop, out _));
    }

    [Fact]
    public void A_transfer_whose_accounts_are_not_all_different_is_not_built()
    {
        var reference = SolanaAddress.NewReference();
        var mint = SolanaChains.DevnetUsdc;
        var source = SolanaAddress.AssociatedTokenAddress(Customer, mint, SolanaAddress.TokenProgram);
        var destination = SolanaAddress.AssociatedTokenAddress(Shop, mint, SolanaAddress.TokenProgram);

        // One account cannot hold two places in a transfer: the format gives each its own role, and a wallet asked
        // to sign such a transaction would be signing something nobody can read back.
        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.Transfer(Customer, Customer, 1, reference, Blockhash));
        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.Transfer(Customer, SolanaAddress.SystemProgram, 1, reference, Blockhash));
        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.TokenTransfer(Customer, source, mint, destination, SolanaAddress.TokenProgram, 1, 6, source, Blockhash));
        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.TokenTransfer(Customer, source, mint, source, SolanaAddress.TokenProgram, 1, 6, reference, Blockhash));
        Assert.Throws<ArgumentException>(() => SolanaPayTransaction.TokenTransfer(Customer, source, mint, destination, SolanaAddress.TokenProgram, 1, 6, mint, Blockhash));
    }

    [Fact]
    public void The_checkout_keeps_the_solana_pay_link_in_its_code_and_opens_the_pay_page_from_its_button()
    {
        var reference = SolanaAddress.NewReference();
        var pmi = Devnet.PaymentMethodId(Sol);
        var link = new ChainPaymentLinkExtension(pmi, Devnet, Sol, new PayPageRoute("UISolanaPay", "Pay", ButtonOnly: true));
        var url = new FakeUrlHelper();
        var solanaPay = $"solana:{Shop}?amount=0.05&reference={reference}";
        var page = "https://shop.example/UISolanaPay/Pay/INV1/SOL-SOLANA";

        var prompt = Prompt(Devnet, Sol, 0.05m, new JObject { ["paymentLinkFormat"] = 0, ["reference"] = reference });
        Assert.Equal(solanaPay, link.GetPaymentLink(prompt, url));        // what the Greenfield API returns and wallets scan
        Assert.Equal(solanaPay, link.GetPaymentLink(prompt, null));
        Assert.Equal(page, link.GetPayPageLink(prompt, url));
        Assert.Null(link.GetPayPageLink(prompt, null));
        var model = Checkout(link, prompt, url);
        Assert.Equal(solanaPay, model.InvoiceBitcoinUrlQR);
        Assert.Equal(page, model.InvoiceBitcoinUrl);
        Assert.True(model.ShowPayInWalletButton);

        // With the address-only link there is no reference for the page to put into a transfer: no page, and no button.
        var plain = Prompt(Devnet, Sol, 0.05m, new JObject { ["paymentLinkFormat"] = 2 });
        Assert.Equal(Shop, link.GetPaymentLink(plain, url));
        Assert.Null(link.GetPayPageLink(plain, url));
        model = Checkout(link, plain, url);
        Assert.Equal((Shop, Shop, false), (model.InvoiceBitcoinUrlQR, model.InvoiceBitcoinUrl, model.ShowPayInWalletButton));

        // An invoice from before references existed: the wallet link without one, and the button opens that link as before.
        var old = Prompt(Devnet, Sol, 0.05m, new JObject { ["paymentLinkFormat"] = 0 });
        Assert.Null(link.GetPayPageLink(old, url));
        model = Checkout(link, old, url);
        Assert.Equal(($"solana:{Shop}?amount=0.05", $"solana:{Shop}?amount=0.05", true), (model.InvoiceBitcoinUrlQR, model.InvoiceBitcoinUrl, model.ShowPayInWalletButton));

        // A Solana plugin without a pay page behaves as it did: the Solana Pay link in the code and behind the button.
        var without = new ChainPaymentLinkExtension(pmi, Devnet, Sol);
        model = Checkout(without, prompt, url);
        Assert.Equal((solanaPay, solanaPay, true), (model.InvoiceBitcoinUrlQR, model.InvoiceBitcoinUrl, model.ShowPayInWalletButton));
    }

    [Fact]
    public void The_checkouts_of_the_other_chains_are_as_they_were()
    {
        var url = new FakeUrlHelper();

        // NEAR has no wallet link: its pay page is the payment link, in the code and behind the button.
        var near = NEARChains.Protocol(mainnet: false);
        var nearMethod = near.PaymentMethods.Single(m => m.Currency == "NEAR");
        var nearLink = new ChainPaymentLinkExtension(near.PaymentMethodId(nearMethod), near, nearMethod, NEARChains.Definition(ChainName.Regtest, Empty).PayPage);
        var nearPrompt = Prompt(near, nearMethod, 1.5m, new JObject { ["paymentLinkFormat"] = 0 }, "shop.testnet");
        var nearPage = "https://shop.example/UINEARPay/Pay/INV1/NEAR-NEAR";
        Assert.Equal(nearPage, nearLink.GetPaymentLink(nearPrompt, url));
        Assert.Equal("shop.testnet", nearLink.GetPaymentLink(nearPrompt, null));
        var model = Checkout(nearLink, nearPrompt, url);
        Assert.Equal((nearPage, nearPage, true), (model.InvoiceBitcoinUrlQR, model.InvoiceBitcoinUrl, model.ShowPayInWalletButton));
        var nearPlain = Prompt(near, nearMethod, 1.5m, new JObject { ["paymentLinkFormat"] = 2 }, "shop.testnet");
        model = Checkout(nearLink, nearPlain, url);
        Assert.Equal(("shop.testnet", "shop.testnet", false), (model.InvoiceBitcoinUrlQR, model.InvoiceBitcoinUrl, model.ShowPayInWalletButton));

        // An EVM chain has no pay page: the EIP-681 link in the code, and no button, since a browser cannot open it.
        var bsc = BNBChains.Definition(ChainName.Mainnet, Empty).Chains.Single();
        var bnb = bsc.PaymentMethods.Single(m => m.Currency == "BNB");
        var bscLink = new ChainPaymentLinkExtension(bsc.PaymentMethodId(bnb), bsc, bnb);
        const string Address = "0x2222222222222222222222222222222222222222";
        var bscPrompt = Prompt(bsc, bnb, 0.5m, new JObject { ["paymentLinkFormat"] = 0 }, Address);
        var eip681 = $"ethereum:{Address}@56?value=500000000000000000";
        Assert.Null(bscLink.GetPayPageLink(bscPrompt, url));
        model = Checkout(bscLink, bscPrompt, url);
        Assert.Equal((eip681, eip681, false), (model.InvoiceBitcoinUrlQR, model.InvoiceBitcoinUrl, model.ShowPayInWalletButton));
    }

    [Fact]
    public void The_plugin_serves_its_pay_page_from_the_checkouts_button_only_and_embeds_the_pages_script()
    {
        Assert.Equal(new PayPageRoute("UISolanaPay", "Pay", ButtonOnly: true), SolanaChains.Definition(ChainName.Mainnet, Empty).PayPage);
        Assert.Equal("solana:devnet", Devnet.ToChainProfile().NetworkKey);   // the chain as wallets name it

        // BTCPay serves a plugin's embedded files by this name at /Resources/solanapay/app.js.
        using var script = typeof(SolanaPlugin).Assembly.GetManifestResourceStream("BTCPayServer.Plugins.Solana.Resources.solanapay.app.js");
        Assert.NotNull(script);
        var text = new StreamReader(script).ReadToEnd();
        Assert.Contains("wallet-standard:app-ready", text);
        Assert.Contains("solana:signAndSendTransaction", text);
    }

    /// <summary>An invoice with one prompt, priced in the prompt's own currency, with nothing paid yet.</summary>
    static PaymentPrompt Prompt(ChainDefinition chain, PaymentMethodDefinition method, decimal due, JObject details, string? destination = null)
    {
        var invoice = new InvoiceEntity { Id = "INV1", Currency = method.Currency, Price = due };
        invoice.AddRate(new CurrencyPair(method.Currency, method.Currency), 1m);
        invoice.UpdateTotals();
        return new PaymentPrompt
        {
            ParentEntity = invoice,
            PaymentMethodId = chain.PaymentMethodId(method),
            Currency = method.Currency,
            Divisibility = method.Divisibility,
            Destination = destination ?? Shop,
            Details = details,
        };
    }

    static CheckoutModel Checkout(ChainPaymentLinkExtension link, PaymentPrompt prompt, IUrlHelper url)
    {
        var model = new CheckoutModel();
        var extension = new ChainCheckoutModelExtension(link.PaymentMethodId, "image", prompt.Currency, [link]);
        extension.ModifyCheckoutModel(new CheckoutModelContext(model, null!, null!, prompt.ParentEntity, url, prompt, new HandlerStub(link.PaymentMethodId)));
        return model;
    }

    /// <summary>The checkout only asks a handler which payment method it is.</summary>
    sealed class HandlerStub(PaymentMethodId paymentMethodId) : IPaymentMethodHandler
    {
        public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;
        public JsonSerializer Serializer => throw new NotSupportedException();
        public Task ConfigurePrompt(PaymentMethodContext context) => throw new NotSupportedException();
        public Task BeforeFetchingRates(PaymentMethodContext context) => throw new NotSupportedException();
        public object ParsePaymentPromptDetails(JToken details) => throw new NotSupportedException();
        public object ParsePaymentMethodConfig(JToken config) => throw new NotSupportedException();
        public object ParsePaymentDetails(JToken details) => throw new NotSupportedException();
    }

    /// <summary>Addresses as "scheme://host/controller/action/invoice/method", enough to tell which page a link names.</summary>
    sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new()
        {
            HttpContext = new DefaultHttpContext { Request = { Scheme = "https", Host = new HostString("shop.example") } },
        };

        public string? Action(UrlActionContext actionContext)
        {
            var values = new RouteValueDictionary(actionContext.Values);
            return $"{actionContext.Protocol}://{actionContext.Host}/{actionContext.Controller}/{actionContext.Action}/{values["invoiceId"]}/{values["paymentMethodId"]}";
        }

        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
