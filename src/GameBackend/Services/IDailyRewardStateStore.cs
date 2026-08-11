namespace GameBackend.Services;

/// <summary>
/// Durable record of which daily-reward claims have already been granted, keyed by a
/// server-derived idempotency key. This is what makes a retried claim safe.
/// </summary>
public interface IDailyRewardStateStore
{
    /// <summary>Returns <see langword="true"/> when a claim for <paramref name="idempotencyKey"/> was already recorded.</summary>
    Task<bool> HasClaimedAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Records that the claim identified by <paramref name="idempotencyKey"/> was granted at <paramref name="claimedAt"/>.</summary>
    Task RecordClaimAsync(string idempotencyKey, DateTimeOffset claimedAt, CancellationToken cancellationToken = default);
}
