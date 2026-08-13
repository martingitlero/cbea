using System.Text.Json.Serialization;

namespace GameBackend.Models;

/// <summary>
/// One response shape for both success and failure. Every field always serializes — including
/// explicit nulls — so a client binds one stable schema instead of branching on presence.
/// </summary>
public sealed class ClaimDailyRewardResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("questId")]
    public string? QuestId { get; set; }

    [JsonPropertyName("playerEntityId")]
    public string? PlayerEntityId { get; set; }

    /// <summary>UTC claim date formatted as <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("claimDateUtc")]
    public string? ClaimDateUtc { get; set; }

    [JsonPropertyName("rewardCurrencyId")]
    public string? RewardCurrencyId { get; set; }

    [JsonPropertyName("rewardAmount")]
    public int RewardAmount { get; set; }

    [JsonPropertyName("newlyClaimed")]
    public bool NewlyClaimed { get; set; }

    [JsonPropertyName("alreadyClaimed")]
    public bool AlreadyClaimed { get; set; }

    [JsonPropertyName("balance")]
    public int Balance { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
