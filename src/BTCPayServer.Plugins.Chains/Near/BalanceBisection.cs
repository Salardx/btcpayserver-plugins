using System.Numerics;

namespace BTCPayServer.Plugins.Chains.Near;

/// <summary>
/// Finds the blocks in which a balance changed, given only the balance at the two ends of a range and a way to read
/// it at any height in between. The range is halved until each change sits in one block, so a payment costs a
/// logarithmic number of balance reads instead of one read per block. A height whose balance equals the previous
/// height's is never reported, which is what makes skipped heights harmless: they read as the last block before them.
/// </summary>
public static class BalanceBisection
{
    public static async Task<List<(uint Height, BigInteger Delta)>> FindChangesAsync(
        uint from, uint to, BigInteger before, BigInteger after, Func<uint, Task<BigInteger>> balanceAt, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);
        var changes = new List<(uint, BigInteger)>();
        await Walk(from, to, before, after);
        return changes;

        async Task Walk(uint lo, uint hi, BigInteger balanceBeforeLo, BigInteger balanceAtHi)
        {
            ct.ThrowIfCancellationRequested();
            if (balanceBeforeLo == balanceAtHi)
                return;
            if (lo == hi)
            {
                changes.Add((lo, balanceAtHi - balanceBeforeLo));
                return;
            }
            var mid = lo + (hi - lo) / 2;
            var balanceAtMid = await balanceAt(mid);
            await Walk(lo, mid, balanceBeforeLo, balanceAtMid);
            await Walk(mid + 1, hi, balanceAtMid, balanceAtHi);
        }
    }
}
