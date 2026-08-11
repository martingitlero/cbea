using GameBackend.Models;
using Microsoft.Extensions.Logging;

namespace GameBackend.Services;

/// <summary>
/// Grants a fixed, server-owned daily reward at most once per player per UTC day.
/// </summary>
/// <remarks>
/// <para><b>Trust boundary.</b> This function sits behind PlayFab
/// <c>ExecuteFunction</c>. Everything under <see cref="FunctionArgument"/> is
/// authored by the game client and is therefore untrusted. The only identity
/// this service will act on is <c>CallerEntityProfile.Entity</c>, which PlayFab
/// attaches server-side. <see cref="FunctionArgument.ClientPlayerId"/> is never
/// read; honouring it would let any client claim another player's reward.</para>
/// <para><b>Idempotency.</b> The dedup key is derived entirely from trusted
/// values — quest id, the PlayFab entity id, and the <em>server</em> UTC date.
/// <see cref="FunctionArgument.ClientRequestId"/> is logged for tracing only:
/// a client that varies it must not be able to claim twice, and a client that
/// repeats it must not be able to suppress another player's grant.</para>
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
        DailyRewardOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new DailyRewardOptions();
    }

    public async Task<ClaimDailyRewardResponse> ClaimAsync(
        PlayFabExecuteFunctionRequest? request,
        CancellationToken cancellationToken = default)
    {
        // Server UTC date only. Client local time is untrusted and would let a
        // player farm the reward by changing their device clock or timezone.
        var now = _clock.UtcNow.ToUniversalTime();
        var claimDateUtc = now.ToString("yyyy-MM-dd");

        // (1) Shape validation.
        var argument = request?.FunctionArgument;
        if (argument is null || string.IsNullOrWhiteSpace(argument.QuestId))
        {
            _logger.LogWarning("Rejected claim: malformed body or missing questId.");
            return Failure(RewardErrorCodes.BadRequest,
                "Request body is malformed or required fields are missing.",
                questId: argument?.QuestId, claimDateUtc: claimDateUtc);
        }

        var questId = argument.QuestId;

        // (2) Caller identity — the only field we are willing to act on.
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

        // (3) Quest allow-list. Rejected before touching any dependency so an
        // unknown quest can never mutate wallet or state.
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
                // Duplicate same-day claim is a success, not an error: the client
                // retried and the server-side outcome is already what it wanted.
                // Amount is zero but the balance reported is the player's real
                // current balance, so the client can reconcile its local state.
                var currentBalance = await _wallet
                    .GetBalanceAsync(playerEntityId, _options.CurrencyId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Duplicate claim for {PlayerEntityId} on {ClaimDateUtc} (trace {ClientRequestId}); no grant.",
                    playerEntityId, claimDateUtc, argument.ClientRequestId);

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

            // Reserve before granting. If the process dies between these two
            // awaits the player loses one day's reward (recoverable by support)
            // rather than being granted twice (an unrecoverable economy exploit).
            //
            // KNOWN LIMITATION: HasClaimedAsync + RecordClaimAsync is a
            // read-then-write race — two concurrent requests can both observe
            // "not claimed". The in-memory fake makes this invisible. A durable
            // implementation must collapse these into one atomic conditional
            // write (Cosmos unique key / Table Storage insert-if-not-exists /
            // ETag precondition) and treat a conflict as the already-claimed path.
            await _stateStore.RecordClaimAsync(idempotencyKey, now, cancellationToken)
                .ConfigureAwait(false);

            var newBalance = await _wallet
                .AddAsync(playerEntityId, _options.CurrencyId, _options.Amount, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Granted {Amount} {CurrencyId} to {PlayerEntityId} for {ClaimDateUtc} (trace {ClientRequestId}).",
                _options.Amount, _options.CurrencyId, playerEntityId, claimDateUtc, argument.ClientRequestId);

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
