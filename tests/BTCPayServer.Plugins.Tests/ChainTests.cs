using System.Globalization;
using System.Numerics;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.BNBChain;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Plugins.Chains.Payments;
using BTCPayServer.Plugins.Chains.Services;
using BTCPayServer.Plugins.Neo;
using Microsoft.Extensions.Configuration;
using NeoPaymentWatcher.Chains;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests;

/// <summary>The shared core, exercised through the two plugins' chain definitions.</summary>
public class ChainTests
{
    static readonly ChainDefinition N3 = NeoChains.NeoN3(mainnet: true);
    static readonly ChainDefinition NeoX = NeoChains.NeoX(mainnet: true);
    static readonly ChainDefinition Bsc = BNBChains.SmartChain(mainnet: true);

    static PaymentMethodDefinition Method(ChainDefinition chain, string currency) => chain.PaymentMethods.Single(m => m.Currency == currency);

    static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Theory]
    [InlineData("NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe", true)]
    [InlineData(" NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe ", true)]
    [InlineData("NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTf", false)]   // checksum
    [InlineData("0x1234567890abcdef1234567890abcdef12345678", false)]
    [InlineData("", false)]
    public void Neo_n3_addresses_are_checked_by_base58_checksum(string input, bool valid)
    {
        Assert.Equal(valid, AddressRules.TryNormalize(N3, input, out var canonical, out var key));
        if (valid)
        {
            Assert.Equal(input.Trim(), canonical);
            Assert.Equal(40, key.Length);
        }
    }

    [Theory]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", true)]     // EIP-55 test vector
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", true)]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD", false)]    // one letter flipped
    [InlineData("0x0000000000000000000000000000000000000000", false)]
    [InlineData("NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe", false)]
    public void Evm_addresses_are_hex_with_an_eip55_checksum(string input, bool valid)
    {
        foreach (var chain in new[] { NeoX, Bsc })
        {
            Assert.Equal(valid, AddressRules.TryNormalize(chain, input, out var canonical, out var key));
            if (valid)
            {
                Assert.Equal("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", canonical);
                Assert.Equal(canonical, key);
            }
        }
    }

    [Fact]
    public void Address_lists_reject_duplicates_in_any_spelling()
    {
        Assert.True(AddressRules.TryNormalizeAll(NeoX, ["0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed"], out var one, out _));
        Assert.Equal(["0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed"], one);

        Assert.False(AddressRules.TryNormalizeAll(NeoX, ["0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed"], out _, out var error));
        Assert.Contains("Duplicate", error);

        Assert.False(AddressRules.TryNormalizeAll(N3, ["NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe", "not-an-address"], out _, out error));
        Assert.Contains("Address 2", error);
    }

    [Fact]
    public void Payment_links_follow_nep9_on_neo_n3_and_eip681_on_evm_chains()
    {
        Assert.Equal("neo:NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe?asset=gas&amount=1.5",
            ChainPaymentLinkExtension.Build(N3, Method(N3, "GAS"), "NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe", 1.5m, PaymentLinkFormat.Standard));
        Assert.Equal("ethereum:0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed@47763?value=1500000000000000000",
            ChainPaymentLinkExtension.Build(NeoX, Method(NeoX, "GAS"), "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", 1.5m, PaymentLinkFormat.Standard));
        Assert.Equal("ethereum:0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed@56?value=500000000000000000",
            ChainPaymentLinkExtension.Build(Bsc, Method(Bsc, "BNB"), "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", 0.5m, PaymentLinkFormat.Standard));
        Assert.Equal("ethereum:0x55d398326f99059ff775485246999027b3197955@56/transfer?address=0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed&uint256=10000000000000000000",
            ChainPaymentLinkExtension.Build(Bsc, Method(Bsc, "USDT"), "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", 10m, PaymentLinkFormat.Standard));
        Assert.Equal("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed",
            ChainPaymentLinkExtension.Build(Bsc, Method(Bsc, "USDT"), "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", 10m, PaymentLinkFormat.AddressOnly));
        Assert.Null(ChainPaymentLinkExtension.Build(N3, Method(N3, "GAS"), null, 1.5m, PaymentLinkFormat.Standard));
    }

    [Fact]
    public void The_stored_link_format_is_read_as_a_number_or_a_name()
    {
        Assert.Equal(PaymentLinkFormat.AddressOnly, ChainPaymentLinkExtension.ReadFormat(JObject.Parse("""{ "paymentLinkFormat": 2 }""")));
        Assert.Equal(PaymentLinkFormat.AddressOnly, ChainPaymentLinkExtension.ReadFormat(JObject.Parse("""{ "paymentLinkFormat": "AddressOnly" }""")));
        Assert.Equal(PaymentLinkFormat.Standard, ChainPaymentLinkExtension.ReadFormat(JObject.Parse("""{ "paymentLinkFormat": 7 }""")));
        Assert.Equal(PaymentLinkFormat.Standard, ChainPaymentLinkExtension.ReadFormat(JObject.Parse("""{ "paymentLinkFormat": "nonsense" }""")));
        Assert.Equal(PaymentLinkFormat.Standard, ChainPaymentLinkExtension.ReadFormat(null));
    }

    [Theory]
    [InlineData("1.5", 8, "150000000")]
    [InlineData("1.50001234", 18, "1500012340000000000")]
    [InlineData("0", 18, "0")]
    [InlineData("0.00000001", 8, "1")]
    public void Due_amounts_become_exact_base_units(string due, int decimals, string units) =>
        Assert.Equal(BigInteger.Parse(units), ChainPaymentLinkExtension.ToBaseUnits(decimal.Parse(due, CultureInfo.InvariantCulture), decimals));

    [Fact]
    public void Transfers_become_decimal_amounts_and_unique_payment_ids()
    {
        var transfer = new TokenTransfer("0x5838ee781d75fc76eab5b7d7234a1fb9c912e0fb8948525f35a7be161a391d98", -1, 8068890, 1_761_000_000_000,
            "native", "0x62b6e27213824c6a2b983c018377d9bd0fdc5e17", "0xd2dd7b597fd2435b6db61ddf48544fd931e6869f", BigInteger.Parse("116993864000000000000"));

        Assert.Equal(116.993864m, PaymentRecorder.ToDecimal(transfer.Amount, 18));
        Assert.Equal(1.50001444m, PaymentRecorder.ToDecimal(150001444, 8));
        var pmi = NeoX.PaymentMethodId(Method(NeoX, "GAS"));
        var id = PaymentRecorder.PaymentId(pmi, transfer);
        Assert.Equal("0x5838ee781d75fc76eab5b7d7234a1fb9c912e0fb8948525f35a7be161a391d98--1-GAS-NEOX", id);
        Assert.Equal("https://xexplorer.neo.org/tx/0x5838ee781d75fc76eab5b7d7234a1fb9c912e0fb8948525f35a7be161a391d98",
            new ChainTransactionLinkProvider(NeoX.BlockExplorerLink).GetTransactionLink(id));
    }

    [Fact]
    public void A_chain_maps_tokens_to_its_payment_methods()
    {
        Assert.Equal("USDT", Bsc.FindByToken("0x55D398326f99059fF775485246999027B3197955")?.Currency);
        Assert.Equal("BNB", Bsc.FindByToken("native")?.Currency);
        Assert.Null(Bsc.FindByToken("0x1111111111111111111111111111111111111111"));
        Assert.Equal(["0x55d398326f99059ff775485246999027b3197955", "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d", "native"], Bsc.TokenHashes().Order());
        Assert.Equal(["BNB-BSC", "USDT-BSC", "USDC-BSC"], Bsc.PaymentMethods.Select(m => Bsc.PaymentMethodId(m).ToString()));
    }

    [Fact]
    public void Configuration_overrides_nodes_and_token_contracts_and_drops_tokens_without_one()
    {
        var testnet = BNBChains.SmartChain(mainnet: false);
        Assert.Equal(["BNB"], ChainDefaults.ApplyConfiguration(testnet, Configuration()).PaymentMethods.Select(m => m.Currency));

        var configured = ChainDefaults.ApplyConfiguration(testnet, Configuration(
            ("BSC_JSONRPC_URI", "https://rpc.example/bsc"),
            ("BSC_USDT_CONTRACT", "0x337610d27c682E347C9cD60BD4b3b107C9d34dDd")));
        Assert.Equal(["https://rpc.example/bsc"], configured.JsonRpcUris);
        Assert.Equal(["BNB", "USDT"], configured.PaymentMethods.Select(m => m.Currency));
        Assert.Equal("0x337610d27c682e347c9cd60bd4b3b107c9d34ddd", Method(configured, "USDT").TokenHash);

        Assert.Throws<InvalidOperationException>(() => ChainDefaults.ApplyConfiguration(testnet, Configuration(("BSC_USDC_CONTRACT", "not-a-contract"))));

        var n3 = ChainDefaults.ApplyConfiguration(N3, Configuration(("NEO_JSONRPC_URI", "https://rpc.example/neo")));
        Assert.Equal(["https://rpc.example/neo"], n3.JsonRpcUris);
        Assert.Equal(2, ChainDefaults.ApplyConfiguration(N3, Configuration()).JsonRpcUris.Length);
    }

    [Fact]
    public void The_context_finds_payment_methods_and_replaces_one_chain_at_a_time()
    {
        var context = new ChainPluginContext<ChainTests>(new ChainPluginDefinition
        {
            Id = "Test",
            DisplayName = "Test",
            NavKey = "Test",
            ViewPrefix = "Test",
            Chains = [N3, NeoX],
        });

        Assert.True(context.TryGetPaymentMethod(new PaymentMethodId("GAS-NEOX"), out var chain, out var method));
        Assert.Equal("NEOX", chain.Id);
        Assert.Equal("native", method.TokenHash);
        Assert.False(context.TryGetPaymentMethod(new PaymentMethodId("GAS-BSC"), out _, out _));

        context.Replace(NeoX with { JsonRpcUris = ["https://rpc.example/neox"] });
        Assert.Equal(["https://rpc.example/neox"], context.Chains.Single(c => c.Id == "NEOX").JsonRpcUris);
        Assert.Equal(N3.JsonRpcUris, context.Chains.Single(c => c.Id == "NEO").JsonRpcUris);
        Assert.Equal(NeoX.JsonRpcUris, context.Default("NEOX").JsonRpcUris);
        Assert.Throws<ArgumentException>(() => new ChainPluginContext<ChainTests>(new ChainPluginDefinition
        {
            Id = "Test",
            DisplayName = "Test",
            NavKey = "Test",
            ViewPrefix = "Test",
            Chains = [N3, N3],
        }));
    }

    [Fact]
    public void An_address_stays_reserved_while_its_invoice_can_still_be_paid()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(AddressAllocator.ShouldReserve(InvoiceStatus.New, now.AddHours(1), now));
        Assert.True(AddressAllocator.ShouldReserve(InvoiceStatus.Processing, now.AddHours(-1), now));
        Assert.True(AddressAllocator.ShouldReserve(InvoiceStatus.Expired, now.AddMinutes(5), now));
        Assert.False(AddressAllocator.ShouldReserve(InvoiceStatus.Expired, now.AddMinutes(-5), now));
        Assert.False(AddressAllocator.ShouldReserve(InvoiceStatus.Settled, now.AddHours(1), now));
        Assert.False(AddressAllocator.ShouldReserve(InvoiceStatus.Invalid, now.AddHours(1), now));
    }

    [Fact]
    public void Rate_limit_errors_are_recognised_through_inner_exceptions()
    {
        Assert.True(ChainListener<NeoPlugin>.IsRateLimit(new InvalidOperationException("All RPC endpoints failed", new HttpRequestException("Response status code does not indicate success: 429 (Too Many Requests)."))));
        Assert.True(ChainListener<NeoPlugin>.IsRateLimit(new InvalidOperationException("RPC eth_getLogs failed with code -32011: request limit reached")));
        Assert.False(ChainListener<NeoPlugin>.IsRateLimit(new InvalidOperationException("Block 5 is not available from the node yet.")));
    }
}
