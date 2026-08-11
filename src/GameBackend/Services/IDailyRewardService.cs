using GameBackend.Models;

namespace GameBackend.Services;

/// <summary>
/// Trusted daily-reward claim logic, deliberately free of any HTTP concerns so
/// it can be unit tested without a function host.
/// </summary>
public interface IDailyRewardService
{
    /// <summary>
    /// Executes a daily reward claim for the PlayFab caller described by
    /// <paramref name="request"/>.
    /// </summary>
    /// <remarks>
    /// Never throws for expected failure modes — validation problems and
    /// upstream faults are returned as a populated
    /// <see cref="ClaimDailyRewardResponse.ErrorCode"/> instead.
    /// </remarks>
    Task<ClaimDailyRewardResponse> ClaimAsync(
        PlayFabExecuteFunctionRequest? request,
        CancellationToken cancellationToken = default);
}
