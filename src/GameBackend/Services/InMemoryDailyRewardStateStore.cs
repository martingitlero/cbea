using System.Collections.Concurrent;

namespace GameBackend.Services;

/// <summary>
/// Thread-safe, process-local <see cref="IDailyRewardStateStore"/> used for local runs and
/// tests. A real deployment swaps this for PlayFab player data or a durable store so the
/// idempotency record survives restarts and scale-out.
/// </summary>
public sealed class InMemoryDailyRewardStateStore : IDailyRewardStateStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);

    public Task<bool> HasClaimedAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_claims.ContainsKey(idempotencyKey));
    }

    public Task RecordClaimAsync(string idempotencyKey, DateTimeOffset claimedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();

        _claims[idempotencyKey] = claimedAt;
        return Task.CompletedTask;
    }
}
