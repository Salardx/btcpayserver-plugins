using NeoPaymentWatcher.Chains;

namespace BTCPayServer.Plugins.Chains.Services;

public sealed record ScannedRange(uint To, long ToTime, IReadOnlyList<TokenTransfer> Transfers);

/// <summary>
/// A chain client that scans a run of blocks in one go instead of one block at a time. NEAR makes a block every
/// 0.6 seconds across a dozen shards, so the listener asks for the whole span since its last turn and the client
/// finds the payments by comparing balances, which costs a few calls per watched account rather than per block.
/// </summary>
public interface IRangeScanner
{
    /// <summary>The longest span one call may cover; a listener that fell behind catches up span by span.</summary>
    int MaxBlocksPerRange { get; }

    /// <summary>How long the listener waits between turns once it has reached the tip.</summary>
    TimeSpan PollInterval { get; }

    /// <summary>Transfers into the watched addresses in blocks <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    Task<ScannedRange> ScanRangeAsync(uint from, uint to, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct);
}
