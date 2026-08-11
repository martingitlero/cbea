using System.Text.Json.Serialization;

namespace GameBackend.Models;

/// <summary>
/// Payload PlayFab CloudScript posts to an <c>ExecuteFunction</c> HTTP trigger.
/// The envelope is PascalCase while the function argument body is camelCase, so every
/// property is bound explicitly rather than relying on global serializer configuration.
/// </summary>
public sealed class PlayFabExecuteFunctionRequest
{
    [JsonPropertyName("FunctionArgument")]
    public FunctionArgument? FunctionArgument { get; set; }

    [JsonPropertyName("CallerEntityProfile")]
    public CallerEntityProfile? CallerEntityProfile { get; set; }

    [JsonPropertyName("TitleAuthenticationContext")]
    public TitleAuthenticationContext? TitleAuthenticationContext { get; set; }
}

/// <summary>
/// Client-supplied arguments. Everything here crosses the trust boundary and is
/// treated as untrusted input.
/// </summary>
public sealed class FunctionArgument
{
    [JsonPropertyName("questId")]
    public string? QuestId { get; set; }

    /// <summary>
    /// Correlation identifier used for tracing and log stitching only. It is never used
    /// as the idempotency/dedup key, because a client controls its value and could
    /// vary it to replay a claim.
    /// </summary>
    [JsonPropertyName("clientRequestId")]
    public string? ClientRequestId { get; set; }

    /// <summary>
    /// Deliberately bound so the shape of the client payload is explicit, but NEVER read
    /// by server logic. This value is client-owned; trusting it would let any player pass
    /// another player's identifier and claim rewards on their behalf (impersonation).
    /// The authoritative player identity is <see cref="CallerEntity.Id"/>, which PlayFab
    /// populates from the authenticated entity token.
    /// </summary>
    [JsonPropertyName("clientPlayerId")]
    public string? ClientPlayerId { get; set; }
}

/// <summary>
/// Server-populated profile of the authenticated caller.
/// </summary>
public sealed class CallerEntityProfile
{
    [JsonPropertyName("Entity")]
    public CallerEntity? Entity { get; set; }
}

/// <summary>
/// The authenticated PlayFab entity: the only trustworthy source of player identity.
/// </summary>
public sealed class CallerEntity
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }
}

/// <summary>
/// Title-level authentication context supplied by PlayFab when invoking the function.
/// </summary>
public sealed class TitleAuthenticationContext
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("EntityToken")]
    public string? EntityToken { get; set; }
}
