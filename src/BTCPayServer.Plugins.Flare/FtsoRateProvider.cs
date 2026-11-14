using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Chains;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Logging;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Chains.Evm;

namespace BTCPayServer.Plugins.Flare;

/// <summary>
/// Exchange rates from the FTSO, the price oracle that is part of Flare itself: about a hundred data providers
/// submit prices and the chain publishes the result with every block. The rates are read on chain with plain
/// <c>eth_call</c>s against the node the plugin already uses, so no exchange and no API key stand between a store
/// and its prices. BTCPay Server knows the source as <c>flareftso</c>: a store can price any invoice with it, for
/// example <c>BTC_USD = flareftso(BTC_USD)</c>, and this plugin's own payment methods use it by default.
/// </summary>
public sealed class FtsoRateProvider(ChainPluginContext<FlarePlugin> context, ILogger<FtsoRateProvider> logger) : IRateProvider
{
    public const string SourceId = "flareftso";

    /// <summary>Flare's contract registry, at the same address on Flare and on Coston2; it names the current FtsoV2 contract.</summary>
    internal const string ContractRegistry = "0xad67fe66660fb8dfe9d6b1b4240d8650e30f6019";

    /// <summary>The feeds read, each against USD.</summary>
    internal static readonly string[] Feeds = ["FLR", "XRP", "BTC", "ETH", "USDT", "USDC"];

    /// <summary>The oracle updates with every block; a value this old means the feed has stopped.</summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(10);

    static readonly TimeSpan RegistryLifetime = TimeSpan.FromHours(1);

    readonly object gate = new();
    JsonRpcClient? client;
    string[] clientUrls = [];
    (string Address, DateTimeOffset At)? ftso;

    public RateSourceInfo RateSourceInfo { get; } = new(SourceId, "Flare FTSO", "https://dev.flare.network/ftso/overview");

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        var rpc = Client();
        var address = await FtsoAddress(rpc, cancellationToken);
        var calls = Feeds
            .Select(symbol => ("eth_call", new object?[] { new { to = address, data = GetFeedByIdCall(symbol) }, "latest" }))
            .ToArray();
        // One feed that the contract refuses (retired, renamed) must not take the other prices down with it.
        var answers = await rpc.CallBatchAsync(calls, cancellationToken, tolerant: true);
        var now = DateTimeOffset.UtcNow;
        var rates = new List<PairRate>();
        for (var i = 0; i < Feeds.Length; i++)
        {
            var value = DecodeFeed(answers[i].ValueKind == JsonValueKind.String ? answers[i].GetString() : null, now);
            if (value is null)
                logger.LogWarning("The FTSO feed {Feed}/USD gave no fresh value", Feeds[i]);
            else
                rates.Add(new PairRate(new CurrencyPair(Feeds[i], "USD"), new BidAsk(value.Value)));
        }
        return rates.ToArray();
    }

    /// <summary>One client per set of node addresses: the server's own node when it has set one, the public nodes otherwise.</summary>
    JsonRpcClient Client()
    {
        var urls = context.Chains[0].JsonRpcUris;
        lock (gate)
        {
            if (client is null || !clientUrls.SequenceEqual(urls, StringComparer.Ordinal))
            {
                client = new JsonRpcClient(urls, logger);
                clientUrls = urls;
                ftso = null;
            }
            return client;
        }
    }

    async Task<string> FtsoAddress(JsonRpcClient rpc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (ftso is { } known && DateTimeOffset.UtcNow - known.At < RegistryLifetime)
                return known.Address;
        }
        var answer = await rpc.CallAsync("eth_call", [new { to = ContractRegistry, data = ContractAddressByNameCall("FtsoV2") }, "latest"], cancellationToken);
        var address = DecodeAddress(answer.GetString())
                      ?? throw new InvalidOperationException("Flare's contract registry does not name an FtsoV2 contract.");
        lock (gate)
            ftso = (address, DateTimeOffset.UtcNow);
        return address;
    }

    /// <summary>The first four bytes of the Keccak hash of a function signature.</summary>
    internal static string Selector(string signature) =>
        "0x" + Convert.ToHexStringLower(Keccak.Hash256(Encoding.ASCII.GetBytes(signature)).AsSpan(0, 4));

    /// <summary>An FTSO feed id: one byte for the category (1 is crypto), then the feed's name in ASCII, padded with zeros to 21 bytes.</summary>
    internal static string FeedId(string symbol)
    {
        var id = new byte[21];
        id[0] = 1;
        Encoding.ASCII.GetBytes($"{symbol}/USD", id.AsSpan(1));
        return Convert.ToHexStringLower(id);
    }

    /// <summary><c>getFeedById(bytes21)</c>: a fixed-size byte value sits at the left of its 32-byte word.</summary>
    internal static string GetFeedByIdCall(string symbol) => Selector("getFeedById(bytes21)") + FeedId(symbol).PadRight(64, '0');

    /// <summary><c>getContractAddressByName(string)</c>: the offset of the string, its length, then its bytes padded to a word.</summary>
    internal static string ContractAddressByNameCall(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        var padded = Convert.ToHexStringLower(bytes).PadRight((bytes.Length + 31) / 32 * 64, '0');
        return Selector("getContractAddressByName(string)") + 32.ToString("x64", CultureInfo.InvariantCulture) + bytes.Length.ToString("x64", CultureInfo.InvariantCulture) + padded;
    }

    /// <summary>An address returned as one 32-byte word; null for a malformed answer or the zero address.</summary>
    internal static string? DecodeAddress(string? hex)
    {
        if (hex is null || hex.Length != 66 || !EvmHex.TryNormalizeAddress("0x" + hex[26..], out var address))
            return null;
        return address.TrimStart('0', 'x').Length == 0 ? null : address;
    }

    /// <summary>
    /// What <c>getFeedById</c> returns: the value as an integer, the number of decimals it carries (a signed byte),
    /// and the time of the update. Null for a malformed answer, a value that is not positive, or one older than
    /// <see cref="MaximumAge"/>.
    /// </summary>
    internal static decimal? DecodeFeed(string? hex, DateTimeOffset now)
    {
        if (hex is null || hex.Length != 2 + 3 * 64 || !hex.StartsWith("0x", StringComparison.Ordinal))
            return null;
        var value = EvmHex.ToBigInteger(hex[2..66]);
        var decimals = unchecked((sbyte)byte.Parse(hex.AsSpan(128, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        var updated = EvmHex.ToBigInteger(hex[130..194]);
        if (value.Sign <= 0 || updated > now.ToUnixTimeSeconds() + 60 || now.ToUnixTimeSeconds() - (long)updated > MaximumAge.TotalSeconds)
            return null;
        if (decimals is < -18 or > 28 || value > new BigInteger(decimal.MaxValue))
            return null;
        return decimals >= 0
            ? (decimal)value / (decimal)BigInteger.Pow(10, decimals)
            : (decimal)(value * BigInteger.Pow(10, -decimals));
    }
}
