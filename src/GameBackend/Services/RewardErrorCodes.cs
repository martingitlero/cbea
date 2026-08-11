namespace GameBackend.Services;

/// <summary>
/// Stable, machine-readable error codes returned in the response body.
/// These are part of the public contract with the game client, so the string
/// values must not change without a version bump of the function.
/// </summary>
/// <remarks>
/// Deliberately transport-agnostic: the domain layer never decides HTTP status
/// codes. <see cref="Functions.ClaimDailyRewardV1Function"/> owns that mapping.
/// </remarks>
public static class RewardErrorCodes
{
    /// <summary>Request body is malformed or a required field is missing.</summary>
    public const string BadRequest = "BadRequest";

    /// <summary>PlayFab caller entity id or type is missing or blank.</summary>
    public const string MissingCallerEntity = "MissingCallerEntity";

    /// <summary>The requested quest id is not supported by this function.</summary>
    public const string UnknownQuest = "UnknownQuest";

    /// <summary>A downstream state or inventory dependency failed.</summary>
    public const string UpstreamPlayFabError = "UpstreamPlayFabError";
}
