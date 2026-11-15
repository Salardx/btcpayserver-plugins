using System.Numerics;
using System.Text.Json;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.Chains.Solana;
using BTCPayServer.Plugins.Solana;
using BTCPayServer.Plugins.Tests.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests;

public class SolanaTests
{
    static readonly IConfiguration Empty = new ConfigurationBuilder().Build();
    /// <summary>Devnet as the plugin registers it: the methods without a mint there (USDT) are dropped.</summary>
    static readonly ChainDefinition Devnet = ChainDefaults.ApplyConfiguration(SolanaChains.Cluster(mainnet: false), Empty);

    /// <summary>The wallet of the Solana Pay specification's examples, and its USDC token account as the chain has it.</summary>
    const string SpecWallet = "mvines9iiHiQTysrwkJjGf2gb9Ex9jXJX8ns3qwf2kN";
    const string SpecWalletUsdc = "5ZGPSxMzV9xV5s3Wep73r8k5MsPAtLYs11dGDdknznM5";
    const string Pyusd = "2b1kV6DkPAnxd5ixfnxCpjxmKwqjjaYmCZfHsFu24GXo";

    static readonly string Shop = FakeSolanaNode.Address("shop");
    static readonly string Payer = FakeSolanaNode.Address("payer");
    static readonly string Stranger = FakeSolanaNode.Address("stranger");

    static PaymentMethodDefinition Method(ChainDefinition chain, string currency) => chain.PaymentMethods.Single(m => m.Currency == currency);

    static SolanaChainClient Client(FakeSolanaNode node) =>
        new((Devnet with { JsonRpcUris = [node.Url] }).ToChainProfile(), NullLogger<SolanaChainClient>.Instance);

    static JsonElement Json(JToken token) => JsonDocument.Parse(token.ToString()).RootElement.Clone();

    [Fact]
    public void Mainnet_offers_sol_usdc_and_usdt()
    {
        var definition = SolanaChains.Definition(ChainName.Mainnet, Empty);

        Assert.Equal("Solana", definition.Id);
        Assert.Equal(SolanaPlugin.PluginNavKey, definition.NavKey);
        Assert.Null(definition.PayPage);
        var chain = Assert.Single(definition.Chains);
        Assert.Equal("SOLANA", chain.Id);
        Assert.True(chain.IsSolana);
        Assert.True(chain.MatchesByReference);
        Assert.False(chain.IsEvm);
        Assert.Equal("solana:mainnet", chain.ToChainProfile().NetworkKey);
        Assert.Equal(["SOL-SOLANA", "USDC-SOLANA", "USDT-SOLANA"], chain.PaymentMethods.Select(m => chain.PaymentMethodId(m).ToString()));
        Assert.Equal([9, 6, 6], chain.PaymentMethods.Select(m => m.Decimals));
        Assert.Equal(SolanaChains.MainnetUsdc, Method(chain, "USDC").TokenHash);
        Assert.Equal(SolanaChains.MainnetUsdt, Method(chain, "USDT").TokenHash);
        Assert.Equal("https://explorer.solana.com/tx/5abc", string.Format(chain.BlockExplorerLink, "5abc"));
        Assert.StartsWith("data:image/svg+xml;base64,", chain.CryptoImagePath);
    }

    [Fact]
    public void Devnet_offers_sol_and_circles_test_usdc_and_usdt_only_when_a_mint_is_configured()
    {
        var bare = Assert.Single(SolanaChains.Definition(ChainName.Regtest, Empty).Chains);
        Assert.Equal("devnet", bare.NetworkLabel);
        Assert.Equal(["SOL-SOLANA", "USDC-SOLANA"], bare.PaymentMethods.Select(m => bare.PaymentMethodId(m).ToString()));
        Assert.Equal(SolanaChains.DevnetUsdc, Method(bare, "USDC").TokenHash);
        Assert.Equal("https://explorer.solana.com/tx/5abc?cluster=devnet", string.Format(bare.BlockExplorerLink, "5abc"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SOLANA_USDT_CONTRACT"] = Pyusd, ["SOLANA_JSONRPC_URI"] = "https://node.example/rpc" })
            .Build();
        var configured = Assert.Single(SolanaChains.Definition(ChainName.Testnet, configuration).Chains);
        Assert.Equal(["SOL-SOLANA", "USDC-SOLANA", "USDT-SOLANA"], configured.PaymentMethods.Select(m => configured.PaymentMethodId(m).ToString()));
        Assert.Equal(Pyusd, Method(configured, "USDT").TokenHash);   // a mint keeps its capitals: Base58 is case-sensitive
        Assert.Equal(["https://node.example/rpc"], configured.JsonRpcUris);

        var broken = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SOLANA_USDT_CONTRACT"] = "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed" }).Build();
        Assert.Throws<InvalidOperationException>(() => SolanaChains.Definition(ChainName.Testnet, broken));
    }

    [Fact]
    public void Sol_is_priced_through_its_dollar_rate()
    {
        Assert.Equal(75m, RateRuleCheck.Evaluate(SolanaChains.SolRateRules, "SOL_USD", ("coinbasepro", "SOL_USD", 75m)));
        Assert.Equal(67.5m, RateRuleCheck.Evaluate(SolanaChains.SolRateRules, "SOL_EUR", ("coinbasepro", "SOL_USD", 75m), ("kraken", "USD_EUR", 0.9m)));
    }

    [Theory]
    [InlineData(SpecWallet, true)]
    [InlineData("  mvines9iiHiQTysrwkJjGf2gb9Ex9jXJX8ns3qwf2kN ", true)]
    [InlineData("11111111111111111111111111111111", true)]                                   // 32 zero bytes
    [InlineData("mvines9iiHiQTysrwkJjGf2gb9Ex9j", false)]                                    // too short for 32 bytes
    [InlineData("mvines9iiHiQTysrwkJjGf2gb9Ex9jXJX8ns3qwf2kNN", false)]                      // too long
    [InlineData("mvines9iiHiQTysrwkJjGf2gb9Ex9jXJX8ns3qwf2k0", false)]                       // 0 is not a Base58 character
    [InlineData("1mvines9iiHiQTysrwkJjGf2gb9Ex9jXJX8ns3qwf2kN", false)]                      // a leading zero byte makes it 33 bytes
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", false)]
    [InlineData("", false)]
    public void Addresses_are_32_bytes_of_base58(string input, bool valid)
    {
        Assert.Equal(valid, AddressRules.TryNormalize(Devnet, input, out var canonical, out var key));
        if (valid)
        {
            Assert.Equal(input.Trim(), canonical);
            Assert.Equal(canonical, key);
        }
    }

    [Fact]
    public void An_address_list_names_the_solana_rule_when_an_entry_is_wrong()
    {
        Assert.False(AddressRules.TryNormalizeAll(Devnet, [SpecWallet, "not-an-address"], out _, out var error));
        Assert.Contains("Address 2 is not a Solana address", error);
        Assert.False(AddressRules.TryNormalizeAll(Devnet, [SpecWallet, $" {SpecWallet}"], out _, out error));
        Assert.Contains("Duplicate", error);
    }

    // Derived with an independent implementation; bumps below 255 are the cases where the first hashes were curve points.
    [Theory]
    [InlineData("4qsw9jQhRSD4G5UtcNSwVLEmSc8swu7E4K2Wkd9LPmDu", "8dzxAsLv8FBaYBGVXiwwLTVgXtDyiLi5WsZvSpG2fx3S", 254, true)]
    [InlineData("2Rjso3783QRZQ3uyLNReYZ5oceXkaFcUxxwxXGDeTtsf", "81hmENez1xzn9fqKmb5qyBRSfVUpcnnsUKFuzsYFjUPk", 255, false)]
    [InlineData("C6J35TskncfvHyZ4XwK73LeiX41pyvGNMp76DRo5qmHW", "GPWoMTVFx7XDzGFqAB1JinYyKd9Xq9qAzRnge694QSJf", 252, true)]
    [InlineData("EGzEqk2SrvyFeBLDFjnpx2mBCwGCeutQVQH5W1q15A5y", "CAjhYhLKd66supmzFFeuJP3ZVuxfWrUk6K5esiRBmf5D", 255, false)]
    [InlineData("B6ad5JujvY6Tv98DZGtCQNDDvjictARVbcdbUJqXRSYv", "BpPdUgVwsbrkMYPwJBJG997qSA7xEWa615c3ufv2FNgd", 254, true)]
    [InlineData("A6Dx8AGob9Efhkj1iGjmEKPkpjjioqgq7jb8jA9s8PGg", "FcQb1MyLYCWfFxPrAum5DR4jnF8RPa121ct7RKjCVobQ", 253, false)]
    public void A_token_account_address_is_derived_from_wallet_token_program_and_mint(string owner, string expected, int bump, bool ownerOnCurve)
    {
        Assert.True(Base58.TryDecode(owner, out var ownerKey));
        Assert.Equal(ownerOnCurve, SolanaAddress.IsOnCurve(ownerKey));
        Assert.True(Base58.TryDecode(SolanaChains.MainnetUsdc, out var mint));
        Assert.True(Base58.TryDecode(SolanaAddress.TokenProgram, out var program));
        Assert.True(Base58.TryDecode(SolanaAddress.AssociatedTokenProgram, out var associated));

        Assert.Equal(expected, SolanaAddress.FindProgramAddress([ownerKey, program, mint], associated, out var found));
        Assert.Equal(bump, found);
        Assert.Equal(expected, SolanaAddress.AssociatedTokenAddress(owner, SolanaChains.MainnetUsdc, SolanaAddress.TokenProgram));
        Assert.True(Base58.TryDecode(expected, out var derived));
        Assert.False(SolanaAddress.IsOnCurve(derived));
    }

    [Fact]
    public void The_derivation_gives_the_token_account_the_chain_has_and_another_for_a_token_2022_mint()
    {
        Assert.Equal(SpecWalletUsdc, SolanaAddress.AssociatedTokenAddress(SpecWallet, SolanaChains.MainnetUsdc, SolanaAddress.TokenProgram));
        Assert.Equal("EB1L5vnvdxQVBmFg1kx3SB188t22wY4MT8BPXusiyq8", SolanaAddress.AssociatedTokenAddress(SpecWallet, Pyusd, SolanaAddress.Token2022Program));
    }

    [Fact]
    public void A_reference_is_a_fresh_address_and_travels_with_the_wallet_in_a_shared_key()
    {
        var first = SolanaAddress.NewReference();
        var second = SolanaAddress.NewReference();
        Assert.NotEqual(first, second);
        Assert.True(SolanaAddress.TryNormalize(first, out _));

        var key = SolanaAddress.SharedKey(Shop, first);
        Assert.True(SolanaAddress.TrySplitSharedKey(key, out var wallet, out var reference));
        Assert.Equal((Shop, first), (wallet, reference));
        Assert.False(SolanaAddress.TrySplitSharedKey(Shop, out _, out _));
        Assert.False(SolanaAddress.TrySplitSharedKey($"{Shop}#not-base58!", out _, out _));

        // The recorder reads a shared key as the wallet it pays and the claim it belongs to; a store's address list never accepts one.
        Assert.True(AddressRules.TryParseReceiver(Devnet, key, out var canonical, out var claim));
        Assert.Equal((Shop, key), (canonical, claim));
        Assert.True(AddressRules.TryParseReceiver(Devnet, Shop, out canonical, out claim));
        Assert.Equal((Shop, Shop), (canonical, claim));
        Assert.False(AddressRules.TryNormalize(Devnet, key, out _, out _));
        Assert.False(AddressRules.TryParseReceiver(SolanaChains.Cluster(true) with { Kind = ChainProfile.Evm }, key, out _, out _));
    }

    [Fact]
    public void The_checkout_link_is_a_solana_pay_transfer_request_with_the_invoices_reference()
    {
        var reference = SolanaAddress.NewReference();
        var mainnet = SolanaChains.Cluster(mainnet: true);

        Assert.Equal($"solana:{Shop}?amount=0.05&reference={reference}",
            ChainPaymentLinkExtension.Build(mainnet, Method(mainnet, "SOL"), Shop, 0.05m, PaymentLinkFormat.Standard, reference));
        Assert.Equal($"solana:{Shop}?amount=12.50&spl-token={SolanaChains.MainnetUsdc}&reference={reference}",
            ChainPaymentLinkExtension.Build(mainnet, Method(mainnet, "USDC"), Shop, 12.50m, PaymentLinkFormat.Standard, reference));
        Assert.Equal($"solana:{Shop}?amount=3",
            ChainPaymentLinkExtension.Build(mainnet, Method(mainnet, "SOL"), Shop, 3m, PaymentLinkFormat.Standard));
        Assert.Equal(Shop, ChainPaymentLinkExtension.Build(mainnet, Method(mainnet, "USDC"), Shop, 12.5m, PaymentLinkFormat.AddressOnly, reference));

        Assert.Equal(reference, ChainPaymentLinkExtension.ReadReference(new JObject { ["paymentLinkFormat"] = 0, ["reference"] = reference }));
        Assert.Null(ChainPaymentLinkExtension.ReadReference(new JObject { ["paymentLinkFormat"] = 0 }));
        Assert.Null(ChainPaymentLinkExtension.ReadReference(null));
    }

    [Fact]
    public void Invoices_share_an_address_only_with_the_link_that_carries_a_reference()
    {
        Assert.True(ChainPaymentMethodHandler<SolanaPlugin>.SharesAddresses(Devnet, PaymentLinkFormat.Standard));
        Assert.False(ChainPaymentMethodHandler<SolanaPlugin>.SharesAddresses(Devnet, PaymentLinkFormat.AddressOnly));
        Assert.False(ChainPaymentMethodHandler<SolanaPlugin>.SharesAddresses(Devnet with { Kind = ChainProfile.Evm }, PaymentLinkFormat.Standard));
    }

    [Fact]
    public async Task A_transaction_is_measured_by_what_arrived_at_the_wallet()
    {
        await using var node = new FakeSolanaNode();
        var tokens = Devnet.TokenHashes();
        var reference = SolanaAddress.NewReference();

        node.AddSolTransfer(out var sol, Payer, Shop, 250_000_000, [reference]);
        var payment = SolanaTransfers.Parse(Json(node.Transaction(sol)), Shop, tokens);
        var incoming = Assert.Single(payment.Incoming);
        Assert.Equal((TokenRegistry.Native, new BigInteger(250_000_000), Payer), (incoming.TokenHash, incoming.Amount, incoming.From));
        Assert.Contains(reference, payment.Accounts);
        Assert.NotNull(payment.BlockTime);

        // A token arrives at the wallet's token account; the wallet is not among the transaction's accounts at all.
        node.AddTokenTransfer(out var usdc, FakeSolanaNode.Usdc, Payer, Shop, 12_500_000, [reference]);
        payment = SolanaTransfers.Parse(Json(node.Transaction(usdc)), Shop, tokens);
        incoming = Assert.Single(payment.Incoming);
        Assert.Equal((FakeSolanaNode.Usdc, new BigInteger(12_500_000), Payer), (incoming.TokenHash, incoming.Amount, incoming.From));
        Assert.DoesNotContain(Shop, payment.Accounts);

        // Amounts as the host test pays them: 1.5 SOL and 10 USDC.
        node.AddSolTransfer(out var large, Payer, Shop, 1_500_000_000, [reference]);
        Assert.Equal(new BigInteger(1_500_000_000), Assert.Single(SolanaTransfers.Parse(Json(node.Transaction(large)), Shop, tokens).Incoming).Amount);
        node.AddTokenTransfer(out var ten, FakeSolanaNode.Usdc, Payer, Shop, 10_000_000, [reference]);
        Assert.Equal(new BigInteger(10_000_000), Assert.Single(SolanaTransfers.Parse(Json(node.Transaction(ten)), Shop, tokens).Incoming).Amount);

        // A token that keeps a fee: what counts is what arrived.
        node.AddTokenTransfer(out var withFee, FakeSolanaNode.Usdc, Payer, Shop, 10_000_000, received: 9_990_000);
        Assert.Equal(new BigInteger(9_990_000), Assert.Single(SolanaTransfers.Parse(Json(node.Transaction(withFee)), Shop, tokens).Incoming).Amount);

        // References that came through a lookup table are accounts of the transaction like any other.
        node.AddTokenTransfer(out var looked, FakeSolanaNode.Usdc, Payer, Shop, 1_000_000, [reference], referencesFromLookupTable: true);
        Assert.Contains(reference, SolanaTransfers.Parse(Json(node.Transaction(looked)), Shop, tokens).Accounts);

        // Nothing for: a failed transaction, somebody else's wallet, a token the store does not accept, a transaction the wallet signed itself.
        node.AddSolTransfer(out var failed, Payer, Shop, 250_000_000, failed: true);
        Assert.Empty(SolanaTransfers.Parse(Json(node.Transaction(failed)), Shop, tokens).Incoming);
        Assert.Empty(SolanaTransfers.Parse(Json(node.Transaction(sol)), Stranger, tokens).Incoming);
        Assert.Empty(SolanaTransfers.Parse(Json(node.Transaction(usdc)), Shop, new HashSet<string> { TokenRegistry.Native }).Incoming);
        node.AddSolTransfer(out var own, Payer, Shop, 250_000_000, signedAlsoBy: Shop);
        Assert.Empty(SolanaTransfers.Parse(Json(node.Transaction(own)), Shop, tokens).Incoming);
        Assert.Empty(SolanaTransfers.Parse(Json(new JObject()), Shop, tokens).Incoming);
    }

    [Fact]
    public async Task Payments_are_matched_by_reference_while_invoices_share_the_wallet()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);
        node.AddWallet(Shop);
        node.AddTokenAccount(Shop, FakeSolanaNode.Usdc);
        var (first, second, expired) = (SolanaAddress.NewReference(), SolanaAddress.NewReference(), SolanaAddress.NewReference());
        var watched = new HashSet<string> { SolanaAddress.SharedKey(Shop, first), SolanaAddress.SharedKey(Shop, second) };
        var from = (uint)node.Slot + 1;

        var solSlot = node.AddSolTransfer(out var sol, Payer, Shop, 250_000_000, [first]);
        var usdcSlot = node.AddTokenTransfer(out var usdc, FakeSolanaNode.Usdc, Payer, Shop, 12_500_000, [second]);
        node.AddSolTransfer(out _, Payer, Shop, 100_000_000);                                   // typed by hand: no reference
        node.AddSolTransfer(out _, Payer, Shop, 100_000_000, [expired]);                        // the reference of an invoice nobody watches any more
        node.AddTokenTransfer(out _, FakeSolanaNode.Usdc, Payer, Shop, 5_000_000, [first], failed: true);
        node.AddSolTransfer(out _, Payer, Stranger, 900_000_000, [first]);                      // names the reference, pays somebody else
        node.AddSlots(3);
        var to = (uint)node.Slot;

        var range = await client.ScanRangeAsync(from, to, watched, Devnet.TokenHashes(), CancellationToken.None);

        Assert.Equal(to, range.To);
        Assert.Equal(
            [
                // the index is the receiving account's place in the transaction: the wallet for SOL, its token account for USDC
                (sol, 1, (uint)solSlot, TokenRegistry.Native, SolanaAddress.SharedKey(Shop, first), new BigInteger(250_000_000)),
                (usdc, 2, (uint)usdcSlot, FakeSolanaNode.Usdc, SolanaAddress.SharedKey(Shop, second), new BigInteger(12_500_000)),
            ],
            range.Transfers.Select(t => (t.TxId, t.NotifyIndex, t.BlockIndex, t.TokenHash, t.To, t.Amount)));
        Assert.All(range.Transfers, t => Assert.Equal(Payer, t.From));
        Assert.All(range.Transfers, t => Assert.True(t.BlockTime > 0));

        // The next turn reads the last half minute again: the same payments under the same ids, nothing new, and no
        // transaction is fetched a second time. An earlier span does not see later payments.
        node.AddSlots(2);
        var before = node.Requests;
        var again = await client.ScanRangeAsync(to + 1, to + 2, watched, Devnet.TokenHashes(), CancellationToken.None);
        Assert.Equal(range.Transfers, again.Transfers);
        Assert.Equal(2, node.Requests - before);   // the wallet's history and its USDC account's
        Assert.Empty((await client.ScanRangeAsync(from - 10, from - 1, watched, Devnet.TokenHashes(), CancellationToken.None)).Transfers);
    }

    [Fact]
    public async Task Version_1_transactions_are_read_and_a_newer_version_is_left_out()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);
        node.AddWallet(Shop);
        node.AddTokenAccount(Shop, FakeSolanaNode.Usdc);
        var (first, second, third) = (SolanaAddress.NewReference(), SolanaAddress.NewReference(), SolanaAddress.NewReference());
        var watched = new HashSet<string> { SolanaAddress.SharedKey(Shop, first), SolanaAddress.SharedKey(Shop, second), SolanaAddress.SharedKey(Shop, third) };
        var from = (uint)node.Slot + 1;

        // Since September 2026 wallets also send the newer transaction format, version 1. A node returns such a
        // transaction only to a client that says it reads version 1: one that says less is refused.
        node.AddSolTransfer(out var sol, Payer, Shop, 250_000_000, [first], version: 1);
        node.AddTokenTransfer(out var usdc, FakeSolanaNode.Usdc, Payer, Shop, 12_500_000, [second], version: 1);
        Assert.Equal(1, node.Transaction(sol)["version"]!.Value<int>());
        Assert.NotNull(node.Transaction(sol)["transaction"]!["message"]!["transactionConfig"]);
        // A format newer than the plugin knows is refused by the node and left out; what follows it is read as usual.
        node.AddSolTransfer(out _, Payer, Shop, 100_000_000, [third], version: 2);
        node.AddSolTransfer(out var later, Payer, Shop, 300_000_000, [third]);

        var range = await client.ScanRangeAsync(from, (uint)node.Slot, watched, Devnet.TokenHashes(), CancellationToken.None);

        Assert.Equal(
            [
                (sol, SolanaAddress.SharedKey(Shop, first), new BigInteger(250_000_000)),
                (usdc, SolanaAddress.SharedKey(Shop, second), new BigInteger(12_500_000)),
                (later, SolanaAddress.SharedKey(Shop, third), new BigInteger(300_000_000)),
            ],
            range.Transfers.Select(t => (t.TxId, t.To, t.Amount)));
    }

    [Fact]
    public async Task A_reserved_address_takes_every_transfer_and_a_reference_still_wins()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);
        node.AddWallet(Shop);
        node.AddTokenAccount(Shop, FakeSolanaNode.Usdc);
        var reference = SolanaAddress.NewReference();
        var from = (uint)node.Slot + 1;
        node.AddSolTransfer(out var plain, Payer, Shop, 100_000_000);
        node.AddTokenTransfer(out var token, FakeSolanaNode.Usdc, Payer, Shop, 5_000_000);
        node.AddSolTransfer(out var tagged, Payer, Shop, 300_000_000, [reference]);

        // With the address-only link the invoice holds the address itself.
        var reserved = await client.ScanRangeAsync(from, (uint)node.Slot, new HashSet<string> { Shop }, Devnet.TokenHashes(), CancellationToken.None);
        Assert.Equal([(plain, Shop), (token, Shop), (tagged, Shop)], reserved.Transfers.Select(t => (t.TxId, t.To)));

        // One invoice holds the address, another shares it: the reference decides, the rest goes to the holder.
        var mixed = await client.ScanRangeAsync(from, (uint)node.Slot, new HashSet<string> { Shop, SolanaAddress.SharedKey(Shop, reference) }, Devnet.TokenHashes(), CancellationToken.None);
        Assert.Equal([(plain, Shop), (token, Shop), (tagged, SolanaAddress.SharedKey(Shop, reference))], mixed.Transfers.Select(t => (t.TxId, t.To)));

        // The store accepts SOL only: the token account is not even asked about.
        var before = node.Requests;
        var solOnly = await client.ScanRangeAsync(from, (uint)node.Slot, new HashSet<string> { Shop }, new HashSet<string> { TokenRegistry.Native }, CancellationToken.None);
        Assert.Equal([plain, tagged], solOnly.Transfers.Select(t => t.TxId));
        Assert.Equal(1, node.Requests - before);   // the wallet's history alone: its transactions were read before
    }

    [Fact]
    public async Task A_node_that_is_behind_makes_the_span_fail_instead_of_losing_a_payment()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);
        node.AddWallet(Shop);
        var reference = SolanaAddress.NewReference();
        var watched = new HashSet<string> { SolanaAddress.SharedKey(Shop, reference) };
        var native = new HashSet<string> { TokenRegistry.Native };
        var from = (uint)node.Slot + 1;
        node.AddSolTransfer(out var sol, Payer, Shop, 250_000_000, [reference]);

        // The node has not finalized the end of the span yet.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ScanRangeAsync(from, (uint)node.Slot + 50, watched, native, CancellationToken.None));

        // The node lists the signature but cannot show the transaction yet.
        node.HidesTransactions = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ScanRangeAsync(from, (uint)node.Slot, watched, native, CancellationToken.None));

        node.HidesTransactions = false;
        Assert.Equal(sol, Assert.Single((await client.ScanRangeAsync(from, (uint)node.Slot, watched, native, CancellationToken.None)).Transfers).TxId);
    }

    [Fact]
    public async Task A_signature_the_node_lists_late_is_found_by_a_later_turn()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);
        node.AddWallet(Shop);
        var reference = SolanaAddress.NewReference();
        var watched = new HashSet<string> { SolanaAddress.SharedKey(Shop, reference) };
        var native = new HashSet<string> { TokenRegistry.Native };
        var from = (uint)node.Slot + 1;
        var paid = (uint)node.AddSolTransfer(out var sol, Payer, Shop, 250_000_000, [reference]);

        // The node has finalized the slot but does not list the transaction under the wallet yet.
        node.HiddenSignatures = 1;
        Assert.Empty((await client.ScanRangeAsync(from, paid, watched, native, CancellationToken.None)).Transfers);

        // The next turn starts after that slot and still finds the payment, because it reads the last half minute again.
        node.HiddenSignatures = 0;
        node.AddSlots(20);
        var transfer = Assert.Single((await client.ScanRangeAsync(paid + 1, (uint)node.Slot, watched, native, CancellationToken.None)).Transfers);
        Assert.Equal((sol, paid), (transfer.TxId, transfer.BlockIndex));

        // One slot alone is read without the look back.
        Assert.Empty((await client.ScanBlockAsync((uint)node.Slot, watched, native, CancellationToken.None)).Transfers);
    }

    [Fact]
    public async Task A_transaction_the_node_never_returns_is_given_up_so_the_listener_goes_on()
    {
        await using var node = new FakeSolanaNode();
        var client = new SolanaChainClient((Devnet with { JsonRpcUris = [node.Url] }).ToChainProfile(), NullLogger<SolanaChainClient>.Instance) { FetchAttempts = 2 };
        node.AddWallet(Shop);
        var reference = SolanaAddress.NewReference();
        var watched = new HashSet<string> { SolanaAddress.SharedKey(Shop, reference) };
        var native = new HashSet<string> { TokenRegistry.Native };
        var from = (uint)node.Slot + 1;
        node.AddSolTransfer(out _, Payer, Shop, 250_000_000, [reference]);
        node.HidesTransactions = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ScanRangeAsync(from, (uint)node.Slot, watched, native, CancellationToken.None));
        Assert.Empty((await client.ScanRangeAsync(from, (uint)node.Slot, watched, native, CancellationToken.None)).Transfers);

        // A later transaction is read as usual: the one that was given up does not hold the chain.
        node.HidesTransactions = false;
        node.AddSolTransfer(out var next, Payer, Shop, 100_000_000, [reference]);
        Assert.Equal(next, Assert.Single((await client.ScanRangeAsync(from, (uint)node.Slot, watched, native, CancellationToken.None)).Transfers).TxId);
    }

    [Fact]
    public void Two_wallets_paid_by_one_transaction_get_payments_of_their_own()
    {
        var other = FakeSolanaNode.Address("second-shop");
        var transaction = new JObject
        {
            ["blockTime"] = 1_794_000_000,
            ["meta"] = new JObject
            {
                ["err"] = JValue.CreateNull(),
                ["preBalances"] = new JArray(900_000_000, 1_000_000, 1_000_000, 1),
                ["postBalances"] = new JArray(399_995_000, 201_000_000, 301_000_000, 1),
                ["preTokenBalances"] = new JArray(),
                ["postTokenBalances"] = new JArray(),
            },
            ["transaction"] = new JObject
            {
                ["message"] = new JObject
                {
                    ["accountKeys"] = new JArray(Payer, Shop, other, SolanaAddress.SystemProgram),
                    ["header"] = new JObject { ["numRequiredSignatures"] = 1 },
                },
            },
        };
        var native = new HashSet<string> { TokenRegistry.Native };

        var first = Assert.Single(SolanaTransfers.Parse(Json(transaction), Shop, native).Incoming);
        var second = Assert.Single(SolanaTransfers.Parse(Json(transaction), other, native).Incoming);

        Assert.Equal((new BigInteger(200_000_000), 1), (first.Amount, first.AccountIndex));
        Assert.Equal((new BigInteger(300_000_000), 2), (second.Amount, second.AccountIndex));
        // BTCPay keys a payment by its id and method, so the two must not share an id though the transaction is one.
        var pmi = new PaymentMethodId("SOL-SOLANA");
        Assert.NotEqual(
            PaymentRecorder.PaymentId(pmi, new TokenTransfer("signature", first.AccountIndex, 1, 0, TokenRegistry.Native, Payer, Shop, first.Amount)),
            PaymentRecorder.PaymentId(pmi, new TokenTransfer("signature", second.AccountIndex, 1, 0, TokenRegistry.Native, Payer, other, second.Amount)));
    }

    [Fact]
    public void A_payment_by_reference_counts_even_when_the_chains_clock_is_behind_the_servers()
    {
        var created = DateTimeOffset.FromUnixTimeSeconds(1_794_000_000);
        var closes = created.AddHours(25);
        var early = created.AddSeconds(-8);

        // A reserved address may have served an earlier invoice: a transfer from before this invoice is not its payment.
        Assert.False(PaymentRecorder.InPaymentWindow(InvoiceStatus.New, created, closes, early, byReference: false));
        Assert.True(PaymentRecorder.InPaymentWindow(InvoiceStatus.New, created, closes, created, byReference: false));
        // A reference belongs to one invoice: the same transfer is its payment.
        Assert.True(PaymentRecorder.InPaymentWindow(InvoiceStatus.New, created, closes, early, byReference: true));

        // Either way: paid late while the invoice is still watched counts, after that it does not, and a settled invoice takes nothing more.
        Assert.True(PaymentRecorder.InPaymentWindow(InvoiceStatus.Expired, created, closes, closes.AddMinutes(10), byReference: true));
        Assert.False(PaymentRecorder.InPaymentWindow(InvoiceStatus.Expired, created, closes, closes.AddMinutes(20), byReference: true));
        Assert.False(PaymentRecorder.InPaymentWindow(InvoiceStatus.Settled, created, closes, created.AddMinutes(1), byReference: true));
        Assert.False(PaymentRecorder.InPaymentWindow(InvoiceStatus.Expired, created, closes, closes.AddMinutes(20), byReference: false));
    }

    [Fact]
    public async Task The_node_is_checked_for_its_cluster_and_asked_for_final_data()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);

        await client.VerifyNetworkAsync(CancellationToken.None);
        Assert.Equal((uint)node.Slot, await client.GetTipAsync(CancellationToken.None));
        Assert.Equal(("SOL", 9), ((await client.GetTokenInfoAsync(TokenRegistry.Native, CancellationToken.None)).Symbol, (await client.GetTokenInfoAsync(TokenRegistry.Native, CancellationToken.None)).Decimals));
        Assert.Equal(6, (await client.GetTokenInfoAsync(FakeSolanaNode.Usdc, CancellationToken.None)).Decimals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTokenInfoAsync(Stranger, CancellationToken.None));
        Assert.True(client.TryCanonicalizeAddress($" {Shop} ", out var canonical, out var key));
        Assert.Equal((Shop, Shop), (canonical, key));

        node.Genesis = SolanaChainClient.MainnetGenesis;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.VerifyNetworkAsync(CancellationToken.None));
        Assert.Contains("another Solana cluster", error.Message);
    }

    [Fact]
    public async Task A_receiving_address_must_be_a_wallet_that_can_take_the_currency()
    {
        await using var node = new FakeSolanaNode();
        var client = Client(node);
        var sol = Method(Devnet, "SOL");
        var usdc = Method(Devnet, "USDC");
        node.AddWallet(Shop);

        // A wallet that exists takes SOL; for USDC it needs its token account first.
        Assert.Equal(DestinationCheck.Fine, await client.CheckAsync(Shop, sol, CancellationToken.None));
        var missing = await client.CheckAsync(Shop, usdc, CancellationToken.None);
        Assert.False(missing.Usable);
        Assert.Contains("has no USDC account yet", missing.Note);
        var tokenAccount = node.AddTokenAccount(Shop, FakeSolanaNode.Usdc);
        Assert.Equal(DestinationCheck.Fine, await client.CheckAsync(Shop, usdc, CancellationToken.None));

        // The token account itself is not a receiving address, and neither is a program's account.
        var wrong = await client.CheckAsync(tokenAccount, usdc, CancellationToken.None);
        Assert.False(wrong.Usable);
        Assert.Contains("is a token account, not a wallet", wrong.Note);
        var owned = FakeSolanaNode.Address("vault");
        node.AddProgramAccount(owned, FakeSolanaNode.Address("some-program"));
        Assert.False((await client.CheckAsync(owned, sol, CancellationToken.None)).Usable);

        // A wallet nobody has paid yet is fine for SOL, with a note about its first payment.
        var fresh = await client.CheckAsync(Stranger, sol, CancellationToken.None);
        Assert.True(fresh.Usable);
        Assert.Contains("at least 0.00065024 SOL", fresh.Note);

        var frozen = FakeSolanaNode.Address("frozen-shop");
        node.AddWallet(frozen);
        node.AddTokenAccount(frozen, FakeSolanaNode.Usdc, frozen: true);
        Assert.Contains("frozen", (await client.CheckAsync(frozen, usdc, CancellationToken.None)).Note);

        // Through the shared helper, as the store page uses it.
        Assert.Contains("has no USDC account yet", await DestinationChecks.FirstProblemAsync(client, [Stranger], usdc, CancellationToken.None));
        Assert.Null(await DestinationChecks.FirstProblemAsync(client, [Shop], usdc, CancellationToken.None));
    }
}
