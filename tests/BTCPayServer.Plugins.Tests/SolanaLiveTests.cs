using System.Globalization;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Solana;
using BTCPayServer.Plugins.Solana;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Tests;

/// <summary>
/// The pay page's transfer against the public devnet. Runs only with <c>SOLANA_LIVE=1</c>, and needs no key: the
/// first test builds the unsigned transaction a browser wallet would be handed and writes it out, for
/// <c>demo/pay-solana.mjs --transaction</c> to sign and send within the half minute its blockhash is good for; the
/// second finds that payment again the way the listener does, by the reference.
///
///   SOLANA_LIVE_PAYER       the wallet that pays
///   SOLANA_LIVE_SHOP        the wallet that receives
///   SOLANA_LIVE_REFERENCE   the reference of the imagined invoice (any fresh address)
///   SOLANA_LIVE_AMOUNT      the amount in whole units, "0.001"
///   SOLANA_LIVE_TOKEN       the token's mint for a token payment; empty for SOL
///   SOLANA_LIVE_OUT         where the first test writes the transaction, in Base64
///   SOLANA_LIVE_TX, SOLANA_LIVE_SLOT   for the second test: the payment's signature and slot
/// </summary>
public class SolanaLiveTests
{
    static readonly ChainDefinition Devnet = ChainDefaults.ApplyConfiguration(SolanaChains.Cluster(mainnet: false), new ConfigurationBuilder().Build());

    static string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    static PaymentMethodDefinition Method(string? token) => token is null
        ? Devnet.PaymentMethods.Single(m => m.IsNative)
        : Devnet.PaymentMethods.Single(m => m.Currency == "USDC") with { TokenHash = token };

    [Fact]
    public async Task The_transfer_the_pay_page_hands_to_a_wallet_is_built_from_what_devnet_says()
    {
        var payer = Setting("SOLANA_LIVE_PAYER");
        var shop = Setting("SOLANA_LIVE_SHOP");
        var reference = Setting("SOLANA_LIVE_REFERENCE");
        var amount = Setting("SOLANA_LIVE_AMOUNT");
        var output = Setting("SOLANA_LIVE_OUT");
        if (Setting("SOLANA_LIVE") != "1" || payer is null || shop is null || reference is null || amount is null || output is null)
            Assert.Skip("Set SOLANA_LIVE=1, SOLANA_LIVE_PAYER, SOLANA_LIVE_SHOP, SOLANA_LIVE_REFERENCE, SOLANA_LIVE_AMOUNT and SOLANA_LIVE_OUT to build a pay page transfer on devnet.");

        var method = Method(Setting("SOLANA_LIVE_TOKEN"));
        var client = new SolanaChainClient(Devnet.ToChainProfile(), NullLogger<SolanaChainClient>.Instance);
        await client.VerifyNetworkAsync(CancellationToken.None);
        Assert.True(TokenAmount.TryParse(amount, method.Decimals, out var units), $"'{amount}' is not an amount of {method.Currency}");

        var prepared = await client.PreparePaymentAsync(payer, shop, method, units, reference, CancellationToken.None);

        Assert.Null(prepared.Refusal);
        Assert.NotNull(prepared.Transaction);
        var wire = SolanaWire.Parse(prepared.Transaction);
        Assert.Equal(payer, wire.Accounts[0]);
        var transfer = Assert.Single(wire.Instructions);
        Assert.Equal(reference, transfer.Accounts[^1]);
        if (method.IsNative)
        {
            Assert.Equal([payer, shop, reference], transfer.Accounts);
            Assert.Equal((ulong)units, SolanaWire.SystemTransferLamports(transfer.Data));
        }
        else
        {
            Assert.Equal(method.TokenHash, transfer.Accounts[1]);
            Assert.Equal((ulong)units, SolanaWire.TransferChecked(transfer.Data).Amount);
        }
        await File.WriteAllTextAsync(output, Convert.ToBase64String(prepared.Transaction));
    }

    [Fact]
    public async Task A_payment_made_from_the_pay_page_is_found_by_its_reference()
    {
        var shop = Setting("SOLANA_LIVE_SHOP");
        var reference = Setting("SOLANA_LIVE_REFERENCE");
        var signature = Setting("SOLANA_LIVE_TX");
        var slot = Setting("SOLANA_LIVE_SLOT");
        var amount = Setting("SOLANA_LIVE_AMOUNT");
        if (Setting("SOLANA_LIVE") != "1" || shop is null || reference is null || signature is null || slot is null || amount is null)
            Assert.Skip("Set SOLANA_LIVE=1, SOLANA_LIVE_SHOP, SOLANA_LIVE_REFERENCE, SOLANA_LIVE_AMOUNT, SOLANA_LIVE_TX and SOLANA_LIVE_SLOT to look for a payment made with demo/pay-solana.mjs --transaction.");

        var method = Method(Setting("SOLANA_LIVE_TOKEN"));
        var client = new SolanaChainClient(Devnet.ToChainProfile(), NullLogger<SolanaChainClient>.Instance);
        var at = uint.Parse(slot, CultureInfo.InvariantCulture);
        Assert.True(TokenAmount.TryParse(amount, method.Decimals, out var units));
        // The scan reads up to the newest finalized slot; an invoice that shares its address is watched by address and reference.
        var tip = await client.GetTipAsync(CancellationToken.None);
        Assert.True(tip >= at, $"slot {at} is not final yet (the newest final slot is {tip})");
        var watched = new HashSet<string> { SolanaAddress.SharedKey(shop, reference) };
        var tokens = new HashSet<string> { method.IsNative ? TokenRegistry.Native : method.TokenHash };

        var range = await client.ScanRangeAsync(at, tip, watched, tokens, CancellationToken.None);

        var transfer = Assert.Single(range.Transfers, t => t.TxId == signature);
        Assert.Equal(SolanaAddress.SharedKey(shop, reference), transfer.To);
        Assert.Equal(units, transfer.Amount);
        Assert.Equal(at, transfer.BlockIndex);

        // And the page's own two questions about it answer as they should.
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(signature, await client.PendingPaymentAsync(shop, reference, method.IsNative ? TokenRegistry.Native : method.TokenHash, at, none, CancellationToken.None));
        Assert.Equal(SolanaSignatureState.Paid, await client.SignatureStateAsync(signature, 0, CancellationToken.None));
    }
}
