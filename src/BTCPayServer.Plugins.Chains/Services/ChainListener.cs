using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Chains.Services;

/// <summary>
/// Follows each chain block by block from where it left off, looking only at the addresses that are reserved by an
/// invoice. With nothing reserved it just moves the cursor: a payment can only land in a block made after its
/// invoice existed. On chains with probabilistic finality it stays <see cref="ChainDefinition.FinalityBlocks"/>
/// behind the tip, so what it records is final. One advisory lock per chain keeps two hosts from scanning the same
/// chain at once.
/// </summary>
public sealed class ChainListener<TPlugin>(
    ChainPluginContext<TPlugin> context,
    ChainRpcProvider<TPlugin> rpc,
    ChainStateStore states,
    ChainActivationService<TPlugin> activation,
    AddressAllocator allocator,
    PaymentRecorder recorder,
    ApplicationDbContextFactory factory,
    ILogger<ChainListener<TPlugin>> logger) : IHostedService where TPlugin : class
{
    internal const int BlocksPerTurn = 50;
    const int InitialBackoffMs = 5_000;
    const int MaximumBackoffMs = 60_000;

    CancellationTokenSource? cts;
    Task workers = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workers = Task.WhenAll(context.Chains.Select(chain => Loop(chain.Id, cts.Token)));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cts?.Cancel();
        try
        {
            await workers.WaitAsync(cancellationToken);
        }
        // A shutdown deadline is expected; letting it escape makes BTCPay count the restart as a plugin crash.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || cts?.IsCancellationRequested is true)
        {
        }
    }

    async Task Loop(string chainId, CancellationToken token)
    {
        var backoff = InitialBackoffMs;
        while (!token.IsCancellationRequested)
        {
            var delay = 1000;
            try
            {
                if (!context.TryGetChain(chainId, out var chain))
                    break;
                var key = ChainStateStore.ListenerStateKey(chain);
                if (!await activation.IsActivatedAsync(chainId, token) && await states.Get(key) is null)
                {
                    delay = 30_000;
                }
                else
                {
                    var advanced = false;
                    await WithChainLock(chainId, async () => advanced = await Scan(chain, key, token), token);
                    if (advanced)
                        delay = 0;
                }
                backoff = InitialBackoffMs;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsRateLimit(ex))
            {
                delay = backoff;
                backoff = Math.Min(backoff * 2, MaximumBackoffMs);
                logger.LogWarning("{Chain}: node is throttling; retrying in {Delay} ms", chainId, delay);
            }
            catch (Exception ex)
            {
                delay = 5000;
                logger.LogError(ex, "{Chain}: scan interrupted; the checkpoint is kept and the block is retried", chainId);
            }
            try
            {
                if (delay > 0)
                    await Task.Delay(delay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Scans up to <see cref="BlocksPerTurn"/> new blocks. Returns true when the cursor moved.</summary>
    async Task<bool> Scan(ChainDefinition chain, string key, CancellationToken token)
    {
        var client = rpc.GetClient(chain.Id);
        var ceiling = (long)await client.GetTipAsync(token) - chain.FinalityBlocks;
        var state = await states.Get(key);
        if (state is null)
        {
            // Start at the tip: older blocks cannot hold a payment for an invoice that did not exist.
            state = new ListenerState { LastBlockIndex = ceiling - 1, UpdatedAt = DateTimeOffset.UtcNow };
            await states.Save(key, state);
            logger.LogInformation("{Chain}: starting at block {Block}", chain.Id, ceiling);
        }
        if (state.LastBlockIndex >= ceiling)
            return false;

        var watched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in chain.PaymentMethods)
            foreach (var claim in await allocator.CurrentClaims(chain.PaymentMethodId(method)))
                watched.Add(claim.Key);
        if (watched.Count == 0)
        {
            state.LastBlockIndex = ceiling;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await states.Save(key, state);
            return true;
        }

        var tokens = chain.TokenHashes();
        var end = Math.Min(ceiling, state.LastBlockIndex + BlocksPerTurn);
        for (var index = state.LastBlockIndex + 1; index <= end; index++)
        {
            token.ThrowIfCancellationRequested();
            var block = await client.ScanBlockAsync((uint)index, watched, tokens, token);
            foreach (var transfer in block.Transfers)
                await recorder.RecordAsync(chain, transfer, token);
            state.LastBlockIndex = index;
            state.LastBlockTime = block.Time;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await states.Save(key, state);
        }
        return true;
    }

    async Task WithChainLock(string chainId, Func<Task> action, CancellationToken cancellationToken)
    {
        await using var context = factory.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"Chains.Listener.{chainId}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        await action();
        await transaction.CommitAsync(cancellationToken);
    }

    internal static bool IsRateLimit(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("429", StringComparison.Ordinal) || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || message.Contains("request limit", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
