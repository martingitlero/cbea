using System.Text.Json.Serialization;

namespace GameBackend.Models;

/// <summary>
/// Payload PlayFab CloudScript posts to an <c>ExecuteFunction</c> HTTP trigger. The envelope is
/// PascalCase while the function argument body is camelCase, so every property is bound explicitly
/// rather than relying on global serializer configuration.
/// </summary>
/// <remarks>
/// <c>TitleAuthenticationContext</c> is deliberately not modelled: nothing here reads it, and
/// unmapped JSON properties are ignored, so a real PlayFab payload still binds. Verifying the
/// title entity token would belong here if this function ever trusted the title rather than the
/// caller entity.
/// </remarks>
public sealed class PlayFabExecuteFunctionRequest
{
    [JsonPropertyName("FunctionArgument")]
    public FunctionArgument? FunctionArgument { get; set; }

    [JsonPropertyName("CallerEntityProfile")]
    public CallerEntityProfile? CallerEntityProfile { get; set; }
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
    /// Tracing and log stitching only — never the dedup key, since a client controls its value
    /// and could vary it to replay a claim.
    /// </summary>
    [JsonPropertyName("clientRequestId")]
    public string? ClientRequestId { get; set; }

    /// <summary>
    /// Bound so the client payload's shape is explicit, but NEVER read by server logic: trusting
    /// it would let any player claim on another player's behalf. The authoritative identity is
    /// <see cref="CallerEntity.Id"/>.
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
