using System.Numerics;

namespace NeoPaymentWatcher.Chains;

public sealed record TokenInfo(string Hash, string Symbol, int Decimals);

/// <summary>A token transfer into a watched address, as recorded on chain. NotifyIndex is the notification or log index; -1 is a native-coin transfer.</summary>
public sealed record TokenTransfer(
    string TxId,
    int NotifyIndex,
    uint BlockIndex,
    long BlockTime,
    string TokenHash,
    string From,
    string To,
    BigInteger Amount);

public sealed record ScannedBlock(uint Index, long Time, IReadOnlyList<TokenTransfer> Transfers);

/// <summary>
/// What the scanner needs from a chain: its tip, the incoming transfers of a block, token metadata and
/// the address format. Block times are Unix milliseconds. Addresses have a canonical form (what is
/// stored and shown) and a key (what notifications and logs are matched on).
/// </summary>
public interface IChainClient
{
    ChainProfile Chain { get; }

    /// <summary>Throws when the node serves a different network than the one configured.</summary>
    Task VerifyNetworkAsync(CancellationToken ct);

    /// <summary>Index of the newest block the node has.</summary>
    Task<uint> GetTipAsync(CancellationToken ct);

    Task<ScannedBlock> ScanBlockAsync(uint index, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes, CancellationToken ct);

    Task<TokenInfo> GetTokenInfoAsync(string tokenHash, CancellationToken ct);

    bool TryCanonicalizeAddress(string? input, out string canonical, out string key);
}
