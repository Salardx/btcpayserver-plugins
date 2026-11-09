using System.Text.Json;
using BTCPayServer.Plugins.Chains.Near;
using BTCPayServer.Plugins.NEAR;
using Microsoft.Extensions.Logging.Abstractions;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Services;

namespace BTCPayServer.Plugins.Tests;

/// <summary>
/// The NEAR client against the public testnet, read-only. Runs only with <c>NEAR_LIVE=1</c>: it makes a few hundred
/// calls to public nodes and depends on what happened on testnet in the last minute. It reads the transfers the
/// chunks of the last blocks list and checks that the balance scan finds every one of them.
/// </summary>
public class NearLiveTests
{
    [Fact]
    public async Task The_balance_scan_finds_what_the_chunks_of_the_last_blocks_show()
    {
        if (Environment.GetEnvironmentVariable("NEAR_LIVE") != "1")
            Assert.Skip("Set NEAR_LIVE=1 to run the NEAR client against the public testnet.");

        var profile = NEARChains.Protocol(mainnet: false).ToChainProfile();
        var client = new NearChainClient(profile, NullLogger<NearChainClient>.Instance);
        var rpc = new NearRpc(new JsonRpcClient(profile.RpcUrls, NullLogger.Instance));
        await client.VerifyNetworkAsync(CancellationToken.None);

        var tip = await client.GetTipAsync(CancellationToken.None);
        var expected = new List<(string Receiver, string TxId)>();
        var from = tip;
        // Walk back from the tip until a few plain transfers are in hand; testnet traffic is mostly contract calls.
        for (var height = tip; height > tip - 400 && expected.Count < 4; height--)
        {
            from = height;
            JsonElement block;
            try
            {
                block = await rpc.BlockAsync(height, CancellationToken.None);
            }
            catch (Exception ex) when (NearRpc.IsCause(ex, NearRpc.UnknownBlock))
            {
                continue;
            }
            foreach (var chunk in block.GetProperty("chunks").EnumerateArray())
            {
                if (chunk.GetProperty("tx_root").GetString() == NearRpc.EmptyRoot)
                    continue;
                var body = await rpc.ChunkAsync(chunk.GetProperty("chunk_hash").GetString()!, CancellationToken.None);
                foreach (var tx in body.GetProperty("transactions").EnumerateArray())
                {
                    var receiver = tx.GetProperty("receiver_id").GetString()!;
                    var signer = tx.GetProperty("signer_id").GetString()!;
                    var actions = tx.GetProperty("actions");
                    var transfers = actions.EnumerateArray().Count(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("Transfer", out _));
                    if (transfers > 0 && transfers == actions.GetArrayLength() && signer != receiver && expected.Count < 6)
                        expected.Add((receiver, tx.GetProperty("hash").GetString()!));
                }
                await Task.Delay(150);
            }
        }
        Assert.NotEmpty(expected);

        // The receipt of a transfer in the last block lands one block later, so the span reaches one block past the tip.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var to = await client.GetTipAsync(CancellationToken.None);
        var watched = expected.Select(e => e.Receiver).ToHashSet(StringComparer.Ordinal);
        var range = await client.ScanRangeAsync(from, to, watched, new HashSet<string> { TokenRegistry.Native }, CancellationToken.None);

        foreach (var (receiver, txId) in expected)
        {
            TokenTransfer[] found = [.. range.Transfers.Where(t => t.To == receiver)];
            Assert.True(found.Any(t => t.TxId == txId), $"transfer {txId} to {receiver} was not found; found for that account: {string.Join(", ", found.Select(t => $"{t.TxId} ({t.Amount} at {t.BlockIndex})"))}");
        }
    }

    /// <summary>
    /// A payment we made ourselves (with demo/pay.mjs): <c>NEAR_LIVE_SHOP</c> is the receiving account, <c>NEAR_LIVE_TX</c>
    /// the transaction hash, <c>NEAR_LIVE_HEIGHT</c> the height of the block that holds the transaction, and
    /// <c>NEAR_LIVE_TOKEN</c> the token contract for a token payment (empty for NEAR). Skipped when they are not set.
    /// </summary>
    [Fact]
    public async Task A_payment_we_sent_is_found_with_its_transaction()
    {
        var shop = Environment.GetEnvironmentVariable("NEAR_LIVE_SHOP");
        var txId = Environment.GetEnvironmentVariable("NEAR_LIVE_TX");
        var height = Environment.GetEnvironmentVariable("NEAR_LIVE_HEIGHT");
        var token = Environment.GetEnvironmentVariable("NEAR_LIVE_TOKEN");
        if (Environment.GetEnvironmentVariable("NEAR_LIVE") != "1" || string.IsNullOrEmpty(shop) || string.IsNullOrEmpty(txId) || string.IsNullOrEmpty(height))
            Assert.Skip("Set NEAR_LIVE=1, NEAR_LIVE_SHOP, NEAR_LIVE_TX and NEAR_LIVE_HEIGHT to check a payment made with demo/pay.mjs.");

        var profile = NEARChains.Protocol(mainnet: false).ToChainProfile();
        var client = new NearChainClient(profile, NullLogger<NearChainClient>.Instance);
        var at = uint.Parse(height, System.Globalization.CultureInfo.InvariantCulture);
        var tokens = new HashSet<string> { string.IsNullOrEmpty(token) ? TokenRegistry.Native : token };

        var range = await client.ScanRangeAsync(at - 20, at + 15, new HashSet<string> { shop }, tokens, CancellationToken.None);

        var transfer = Assert.Single(range.Transfers, t => t.TxId == txId);
        Assert.Equal(shop, transfer.To);
        Assert.InRange(transfer.BlockIndex, at, at + 2);
        Assert.True(transfer.Amount.Sign > 0);
    }
}
