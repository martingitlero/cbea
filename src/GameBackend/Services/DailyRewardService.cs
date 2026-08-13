using GameBackend.Models;
using Microsoft.Extensions.Logging;

namespace GameBackend.Services;

/// <summary>
/// Grants a fixed, server-owned daily reward at most once per player per UTC day.
/// </summary>
/// <remarks>
/// <b>Trust boundary.</b> Everything under <see cref="FunctionArgument"/> is authored by the game
/// client and untrusted; the only identity acted on is <c>CallerEntityProfile.Entity</c>, which
/// PlayFab attaches server-side. The dedup key is built from trusted values only — quest id, that
/// entity id, and the <em>server</em> UTC date — so a client cannot claim twice by varying
/// <see cref="FunctionArgument.ClientRequestId"/>, which is logged for tracing and nothing else.
/// </remarks>
public sealed class DailyRewardService : IDailyRewardService
{
    private readonly ISystemClock _clock;
    private readonly IDailyRewardStateStore _stateStore;
    private readonly ICurrencyWallet _wallet;
    private readonly ILogger<DailyRewardService> _logger;
    private readonly DailyRewardOptions _options;

    public DailyRewardService(
        ISystemClock clock,
        IDailyRewardStateStore stateStore,
        ICurrencyWallet wallet,
        ILogger<DailyRewardService> logger,
        DailyRewardOptions options)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ClaimDailyRewardResponse> ClaimAsync(
        PlayFabExecuteFunctionRequest? request,
        CancellationToken cancellationToken = default)
    {
        // Server UTC date only — client local time would let a player farm the reward by changing
        // their device clock. ToUniversalTime normalises the offset so the date is UTC's, whatever
        // offset the clock implementation hands back.
        var now = _clock.UtcNow.ToUniversalTime();
        var claimDateUtc = now.ToString("yyyy-MM-dd");

        var argument = request?.FunctionArgument;
        if (argument is null || string.IsNullOrWhiteSpace(argument.QuestId))
        {
            _logger.LogWarning("Rejected claim: malformed body or missing questId.");
            return Failure(RewardErrorCodes.BadRequest,
                "Request body is malformed or required fields are missing.",
                questId: argument?.QuestId, claimDateUtc: claimDateUtc);
        }

        var questId = argument.QuestId;

        // Caller identity — the only field we are willing to act on.
        var entity = request?.CallerEntityProfile?.Entity;
        if (entity is null
            || string.IsNullOrWhiteSpace(entity.Id)
            || string.IsNullOrWhiteSpace(entity.Type))
        {
            _logger.LogWarning(
                "Rejected claim for quest {QuestId}: caller entity missing or blank.", questId);
            return Failure(RewardErrorCodes.MissingCallerEntity,
                "CallerEntityProfile.Entity.Id and Type are required.",
                questId: questId, claimDateUtc: claimDateUtc);
        }

        var playerEntityId = entity.Id;

        // Quest allow-list, checked before any dependency is touched so an unknown quest can never
        // mutate wallet or state.
        if (!string.Equals(questId, _options.QuestId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Rejected claim for player {PlayerEntityId}: unsupported quest {QuestId}.",
                playerEntityId, questId);
            return Failure(RewardErrorCodes.UnknownQuest,
                $"Quest '{questId}' is not supported.",
                questId: questId, playerEntityId: playerEntityId, claimDateUtc: claimDateUtc);
        }

        var idempotencyKey = BuildIdempotencyKey(_options.QuestId, playerEntityId, claimDateUtc);

        try
        {
            if (await _stateStore.HasClaimedAsync(idempotencyKey, cancellationToken)
                .ConfigureAwait(false))
            {
                // A duplicate is a success, not an error — the outcome is already what the caller
                // wanted. Amount is zero, but the balance returned is real so the client can
                // reconcile.
                var currentBalance = await _wallet
                    .GetBalanceAsync(playerEntityId, _options.CurrencyId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Daily reward claim rejected as duplicate for {PlayerEntityId} on {ClaimDateUtc}: "
                    + "quest {QuestId}, key {IdempotencyKey}, alreadyClaimed {AlreadyClaimed}, "
                    + "rewardAmount {RewardAmount}, trace {ClientRequestId}.",
                    playerEntityId, claimDateUtc, questId, idempotencyKey, true, 0,
                    argument.ClientRequestId);

                return new ClaimDailyRewardResponse
                {
                    Success = true,
                    QuestId = questId,
                    PlayerEntityId = playerEntityId,
                    ClaimDateUtc = claimDateUtc,
                    RewardCurrencyId = _options.CurrencyId,
                    RewardAmount = 0,
                    NewlyClaimed = false,
                    AlreadyClaimed = true,
                    Balance = currentBalance,
                };
            }

            // Reserve before granting: a crash between these awaits costs the player one day's
            // reward (recoverable) rather than granting twice (an economy exploit that is not).
            //
            // KNOWN LIMITATION: read-then-write race — two concurrent requests can both observe
            // "not claimed". Fix is to collapse these two calls into one atomic conditional write.
            // See README section 3.
            await _stateStore.RecordClaimAsync(idempotencyKey, now, cancellationToken)
                .ConfigureAwait(false);

            var newBalance = await _wallet
                .AddAsync(playerEntityId, _options.CurrencyId, _options.Amount, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Daily reward granted to {PlayerEntityId} on {ClaimDateUtc}: quest {QuestId}, "
                + "key {IdempotencyKey}, alreadyClaimed {AlreadyClaimed}, "
                + "rewardAmount {RewardAmount} {CurrencyId}, trace {ClientRequestId}.",
                playerEntityId, claimDateUtc, questId, idempotencyKey, false, _options.Amount,
                _options.CurrencyId, argument.ClientRequestId);

            return new ClaimDailyRewardResponse
            {
                Success = true,
                QuestId = questId,
                PlayerEntityId = playerEntityId,
                ClaimDateUtc = claimDateUtc,
                RewardCurrencyId = _options.CurrencyId,
                RewardAmount = _options.Amount,
                NewlyClaimed = true,
                AlreadyClaimed = false,
                Balance = newBalance,
            };
        }
        catch (OperationCanceledException)
        {
            // Caller went away or the host is shutting down. Not an upstream
            // fault, so let it propagate rather than reporting a false 502.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Upstream dependency failed while claiming {QuestId} for {PlayerEntityId} on {ClaimDateUtc}.",
                questId, playerEntityId, claimDateUtc);

            return Failure(RewardErrorCodes.UpstreamPlayFabError,
                "A downstream state or inventory dependency failed.",
                questId: questId, playerEntityId: playerEntityId, claimDateUtc: claimDateUtc);
        }
    }

    /// <summary>
    /// Builds the dedup key from trusted values only, e.g.
    /// <c>daily_login:title_player_123:2026-07-09</c>.
    /// </summary>
    internal static string BuildIdempotencyKey(string questId, string playerEntityId, string claimDateUtc)
        => $"{questId}:{playerEntityId}:{claimDateUtc}";

    private ClaimDailyRewardResponse Failure(
        string errorCode,
        string errorMessage,
        string? questId = null,
        string? playerEntityId = null,
        string? claimDateUtc = null) => new()
        {
            Success = false,
            QuestId = questId,
            PlayerEntityId = playerEntityId,
            ClaimDateUtc = claimDateUtc,
            RewardCurrencyId = _options.CurrencyId,
            RewardAmount = 0,
            NewlyClaimed = false,
            AlreadyClaimed = false,
            Balance = 0,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
}
