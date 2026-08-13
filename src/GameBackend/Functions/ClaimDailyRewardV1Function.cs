using System.Text.Json;
using GameBackend.Models;
using GameBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GameBackend.Functions;

/// <summary>
/// HTTP entry point invoked by PlayFab <c>ExecuteFunction</c>.
/// </summary>
/// <remarks>
/// Owns two concerns only — body to DTO, and domain error code to HTTP status — so every business
/// rule stays in <see cref="IDailyRewardService"/>, testable without a function host.
/// </remarks>
public sealed class ClaimDailyRewardV1Function
{
    /// <summary>
    /// Case-insensitive to tolerate casing drift between PlayFab's envelope (PascalCase) and the
    /// client-authored function argument (camelCase).
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDailyRewardService _rewardService;
    private readonly ILogger<ClaimDailyRewardV1Function> _logger;

    public ClaimDailyRewardV1Function(
        IDailyRewardService rewardService,
        ILogger<ClaimDailyRewardV1Function> logger)
    {
        _rewardService = rewardService ?? throw new ArgumentNullException(nameof(rewardService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// POST /api/ClaimDailyRewardV1
    /// </summary>
    /// <remarks>
    /// <see cref="AuthorizationLevel.Function"/> rather than
    /// <see cref="AuthorizationLevel.Anonymous"/>: PlayFab holds a function key
    /// for this endpoint, so an arbitrary internet caller cannot reach it even
    /// though the reward logic would reject them anyway. Defence in depth.
    /// </remarks>
    [Function("ClaimDailyRewardV1")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "ClaimDailyRewardV1")]
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        PlayFabExecuteFunctionRequest? payload = null;

        try
        {
            payload = await JsonSerializer
                .DeserializeAsync<PlayFabExecuteFunctionRequest>(
                    request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Swallowed into the null path below: malformed and absent bodies are the same
            // BadRequest, and letting this escape would surface an untyped 500 instead.
            _logger.LogWarning(ex, "Malformed ClaimDailyRewardV1 request body.");
        }

        var response = await _rewardService
            .ClaimAsync(payload, cancellationToken)
            .ConfigureAwait(false);

        return new ObjectResult(response)
        {
            StatusCode = MapStatusCode(response.ErrorCode),
        };
    }

    /// <summary>
    /// The unknown-code arm returns 500 so a newly added error code fails loudly rather than
    /// masquerading as a success.
    /// </summary>
    internal static int MapStatusCode(string? errorCode) => errorCode switch
    {
        null => StatusCodes.Status200OK,
        RewardErrorCodes.BadRequest => StatusCodes.Status400BadRequest,
        RewardErrorCodes.MissingCallerEntity => StatusCodes.Status400BadRequest,
        RewardErrorCodes.UnknownQuest => StatusCodes.Status400BadRequest,
        RewardErrorCodes.UpstreamPlayFabError => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status500InternalServerError,
    };
}
