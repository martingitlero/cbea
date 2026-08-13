using GameBackend.Models;
using GameBackend.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameBackend.Tests;

public sealed class DailyRewardServiceTests
{
    private const string QuestId = "daily_login";
    private const string RewardCurrencyId = "currency.soft";
    private const int RewardAmount = 50;
    private const int StartingBalance = 100;
    private const string PlayerEntityId = "title_player_123";
    private const string PlayerEntityType = "title_player_account";

    /// <summary>
    /// Client-supplied identity deliberately planted in every request fixture so
    /// that any accidental trust of <c>clientPlayerId</c> shows up as a failure.
    /// </summary>
    private const string SpoofedPlayerId = "spoofed_player_999";

    private static readonly DateTimeOffset FixedInstant = new(2026, 7, 9, 13, 45, 0, TimeSpan.Zero);
    private const string FixedInstantDate = "2026-07-09";

    [Fact]
    public async Task ClaimAsync_FirstValidClaim_Grants50SoftCurrencyAndRecordsClaimState()
    {
        // Arrange
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest());

        // Assert
        Assert.Null(response.ErrorCode);
        Assert.Null(response.ErrorMessage);
        Assert.True(response.Success);
        Assert.True(response.NewlyClaimed);
        Assert.False(response.AlreadyClaimed);
        Assert.Equal(RewardAmount, response.RewardAmount);
        Assert.Equal(RewardCurrencyId, response.RewardCurrencyId);
        Assert.Equal(StartingBalance + RewardAmount, response.Balance);
        Assert.Equal(QuestId, response.QuestId);
        Assert.Equal(PlayerEntityId, response.PlayerEntityId);
        Assert.Equal(FixedInstantDate, response.ClaimDateUtc);

        Assert.Equal(1, stateStore.RecordClaimCallCount);
        Assert.NotNull(stateStore.LastRecordedKey);
        Assert.Contains(PlayerEntityId, stateStore.LastRecordedKey);
        Assert.Contains(FixedInstantDate, stateStore.LastRecordedKey);
        Assert.Equal(StartingBalance + RewardAmount, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_MissingCallerEntity_ReturnsMissingCallerEntityAndMutatesNothing()
    {
        // Arrange — a body that carries a valid quest but no server-populated caller profile.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        var request = CreateRequest();
        request.CallerEntityProfile = null;

        // Act
        var response = await service.ClaimAsync(request);

        // Assert
        Assert.Equal(RewardErrorCodes.MissingCallerEntity, response.ErrorCode);
        Assert.False(response.Success);
        Assert.False(response.NewlyClaimed);
        Assert.False(response.AlreadyClaimed);
        Assert.Equal(0, response.RewardAmount);

        Assert.Equal(0, stateStore.RecordClaimCallCount);
        Assert.Equal(0, wallet.AddCallCount);
        Assert.Equal(StartingBalance, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_SecondClaimOnSameUtcDay_ReportsAlreadyClaimedWithZeroRewardAndUnchangedBalance()
    {
        // Arrange
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        var first = await service.ClaimAsync(CreateRequest());
        Assert.True(first.NewlyClaimed);

        // Act — same UTC day, a few hours later.
        clock.UtcNow = FixedInstant.AddHours(6);
        var second = await service.ClaimAsync(CreateRequest());

        // Assert
        Assert.Null(second.ErrorCode);
        Assert.True(second.Success);
        Assert.True(second.AlreadyClaimed);
        Assert.False(second.NewlyClaimed);
        Assert.Equal(0, second.RewardAmount);
        Assert.Equal(StartingBalance + RewardAmount, second.Balance);

        Assert.Equal(1, wallet.AddCallCount);
        Assert.Equal(1, stateStore.RecordClaimCallCount);
        Assert.Equal(StartingBalance + RewardAmount, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_RetriedWithSameClientRequestId_DoesNotDoubleGrant()
    {
        // Arrange — a flaky client replaying the identical request three times.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        var request = CreateRequest(clientRequestId: "retry-abc-001");

        // Act
        var first = await service.ClaimAsync(request);
        var secondRetry = await service.ClaimAsync(request);
        var thirdRetry = await service.ClaimAsync(request);

        // Assert
        Assert.True(first.NewlyClaimed);
        Assert.Equal(RewardAmount, first.RewardAmount);

        foreach (var retry in new[] { secondRetry, thirdRetry })
        {
            Assert.Null(retry.ErrorCode);
            Assert.True(retry.Success);
            Assert.True(retry.AlreadyClaimed);
            Assert.False(retry.NewlyClaimed);
            Assert.Equal(0, retry.RewardAmount);
            Assert.Equal(StartingBalance + RewardAmount, retry.Balance);
        }

        Assert.Equal(1, wallet.AddCallCount);
        Assert.Equal(StartingBalance + RewardAmount, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_RotatedClientRequestId_DoesNotGrantTwice()
    {
        // Arrange — the brief's rule stated as an attack: clientRequestId "must not be the only
        // protection against duplicate grants", so rotating it must not buy a second reward.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act — same player, same UTC day, deliberately different client request ids.
        var first = await service.ClaimAsync(CreateRequest(clientRequestId: "trace-aaa"));
        var second = await service.ClaimAsync(CreateRequest(clientRequestId: "trace-bbb"));

        // Assert
        Assert.True(first.NewlyClaimed);
        Assert.Equal(RewardAmount, first.RewardAmount);

        Assert.True(second.AlreadyClaimed);
        Assert.False(second.NewlyClaimed);
        Assert.True(second.Success);
        Assert.Null(second.ErrorCode);
        Assert.Equal(0, second.RewardAmount);

        Assert.Equal(1, wallet.AddCallCount);
        Assert.Equal(StartingBalance + RewardAmount, second.Balance);
    }

    [Fact]
    public async Task ClaimAsync_UnknownQuestId_ReturnsUnknownQuestAndTouchesNeitherWalletNorStateStore()
    {
        // Arrange
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest(questId: "weekly_login"));

        // Assert
        Assert.Equal(RewardErrorCodes.UnknownQuest, response.ErrorCode);
        Assert.False(response.Success);
        Assert.False(response.NewlyClaimed);
        Assert.False(response.AlreadyClaimed);
        Assert.Equal(0, response.RewardAmount);

        Assert.Equal(0, stateStore.RecordClaimCallCount);
        Assert.Equal(0, wallet.AddCallCount);
        Assert.Equal(StartingBalance, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_WhenStateStoreFails_ReturnsUpstreamPlayFabErrorAndNeverCreditsWallet()
    {
        // Arrange — state is reserved before the grant, so a store failure must abort the grant.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new ThrowingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest());

        // Assert
        Assert.Equal(RewardErrorCodes.UpstreamPlayFabError, response.ErrorCode);
        Assert.False(response.Success);
        Assert.False(response.NewlyClaimed);
        Assert.Equal(0, response.RewardAmount);

        Assert.Equal(0, wallet.AddCallCount);
        Assert.Equal(StartingBalance, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_WhenWalletFails_ReturnsUpstreamPlayFabErrorAndLeavesBalanceUnchanged()
    {
        // Arrange — the claim is already reserved by this point, so only the balance is asserted:
        // losing a day's reward is support-recoverable, double-granting is not.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new ThrowingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest());

        // Assert
        Assert.Equal(RewardErrorCodes.UpstreamPlayFabError, response.ErrorCode);
        Assert.False(response.Success);
        Assert.False(response.NewlyClaimed);
        Assert.Equal(0, response.RewardAmount);
        Assert.Equal(StartingBalance, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
    }

    [Fact]
    public async Task ClaimAsync_AfterUtcMidnightRollover_AllowsANewClaimForTheNextDay()
    {
        // Arrange — claim late on day one, then retry just after the UTC day boundary.
        var lateInDay = new DateTimeOffset(2026, 7, 9, 23, 59, 0, TimeSpan.Zero);
        var clock = new FakeClock(lateInDay);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        var dayOne = await service.ClaimAsync(CreateRequest());
        Assert.True(dayOne.NewlyClaimed);
        Assert.Equal("2026-07-09", dayOne.ClaimDateUtc);

        // Act — two minutes later, but a new UTC day.
        clock.UtcNow = lateInDay.AddMinutes(2);
        var dayTwo = await service.ClaimAsync(CreateRequest());

        // Assert
        Assert.Null(dayTwo.ErrorCode);
        Assert.True(dayTwo.Success);
        Assert.True(dayTwo.NewlyClaimed);
        Assert.False(dayTwo.AlreadyClaimed);
        Assert.Equal(RewardAmount, dayTwo.RewardAmount);
        Assert.Equal("2026-07-10", dayTwo.ClaimDateUtc);
        Assert.Equal(StartingBalance + (RewardAmount * 2), dayTwo.Balance);

        Assert.Equal(2, wallet.AddCallCount);
        Assert.Equal(2, stateStore.RecordClaimCallCount);
    }

    [Fact]
    public async Task ClaimAsync_IgnoresClientSuppliedPlayerId_AndActsOnlyOnCallerEntity()
    {
        // Arrange — the request carries a spoofed clientPlayerId alongside the
        // authenticated caller entity. This is the impersonation attack the
        // exercise calls out, so it gets a test of its own rather than living
        // as an incidental detail of the happy path.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest());

        // Assert — every observable effect binds to the trusted entity id.
        Assert.Equal(PlayerEntityId, response.PlayerEntityId);
        Assert.NotEqual(SpoofedPlayerId, response.PlayerEntityId);

        Assert.Equal(StartingBalance + RewardAmount, await wallet.GetBalanceAsync(PlayerEntityId, RewardCurrencyId));
        Assert.Equal(0, await wallet.GetBalanceAsync(SpoofedPlayerId, RewardCurrencyId));

        Assert.NotNull(stateStore.LastRecordedKey);
        Assert.Contains(PlayerEntityId, stateStore.LastRecordedKey);
        Assert.DoesNotContain(SpoofedPlayerId, stateStore.LastRecordedKey);
    }

    [Fact]
    public async Task ClaimAsync_NullRequestBody_ReturnsBadRequestAndMutatesNothing()
    {
        // Arrange — models the empty or unparseable body the HTTP trigger
        // forwards as null.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(null);

        // Assert
        Assert.Equal(RewardErrorCodes.BadRequest, response.ErrorCode);
        Assert.False(response.Success);
        Assert.Equal(0, response.RewardAmount);
        Assert.Equal(0, stateStore.RecordClaimCallCount);
        Assert.Equal(0, wallet.AddCallCount);
    }

    [Fact]
    public async Task ClaimAsync_MissingFunctionArgument_ReturnsBadRequest()
    {
        // Arrange
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        var request = CreateRequest();
        request.FunctionArgument = null;

        // Act
        var response = await service.ClaimAsync(request);

        // Assert
        Assert.Equal(RewardErrorCodes.BadRequest, response.ErrorCode);
        Assert.False(response.Success);
        Assert.Equal(0, wallet.AddCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ClaimAsync_MissingOrBlankQuestId_ReturnsBadRequestNotUnknownQuest(string? questId)
    {
        // Arrange — a blank quest id is a malformed request, not an unsupported
        // quest. The distinction is part of the published error contract.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest(questId: questId));

        // Assert
        Assert.Equal(RewardErrorCodes.BadRequest, response.ErrorCode);
        Assert.NotEqual(RewardErrorCodes.UnknownQuest, response.ErrorCode);
        Assert.False(response.Success);
        Assert.Equal(0, stateStore.RecordClaimCallCount);
        Assert.Equal(0, wallet.AddCallCount);
    }

    [Theory]
    [InlineData(null, PlayerEntityType)]
    [InlineData("", PlayerEntityType)]
    [InlineData("   ", PlayerEntityType)]
    [InlineData(PlayerEntityId, null)]
    [InlineData(PlayerEntityId, "")]
    [InlineData(PlayerEntityId, "   ")]
    public async Task ClaimAsync_BlankEntityIdOrType_ReturnsMissingCallerEntity(string? entityId, string? entityType)
    {
        // Arrange — a present-but-blank entity is as untrustworthy as an absent one.
        var clock = new FakeClock(FixedInstant);
        var stateStore = new RecordingStateStore();
        var wallet = new RecordingWallet(StartingBalance);
        var service = CreateService(clock, stateStore, wallet);

        // Act
        var response = await service.ClaimAsync(CreateRequest(entityId: entityId, entityType: entityType));

        // Assert
        Assert.Equal(RewardErrorCodes.MissingCallerEntity, response.ErrorCode);
        Assert.False(response.Success);
        Assert.Equal(0, wallet.AddCallCount);
    }

    private static DailyRewardService CreateService(
        ISystemClock clock,
        IDailyRewardStateStore stateStore,
        ICurrencyWallet wallet)
        => new(clock, stateStore, wallet, NullLogger<DailyRewardService>.Instance, new DailyRewardOptions());

    private static PlayFabExecuteFunctionRequest CreateRequest(
        string? questId = QuestId,
        string? entityId = PlayerEntityId,
        string? entityType = PlayerEntityType,
        string? clientRequestId = "req-001")
        => new()
        {
            FunctionArgument = new FunctionArgument
            {
                QuestId = questId,
                ClientRequestId = clientRequestId,
                ClientPlayerId = SpoofedPlayerId,
            },
            CallerEntityProfile = new CallerEntityProfile
            {
                Entity = new CallerEntity { Id = entityId, Type = entityType },
            },
        };

    /// <summary>Clock pinned to a fixed instant that a test can advance deliberately.</summary>
    private sealed class FakeClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    /// <summary>Real in-memory store that additionally counts and captures writes.</summary>
    private sealed class RecordingStateStore : IDailyRewardStateStore
    {
        private readonly InMemoryDailyRewardStateStore _inner = new();

        public int RecordClaimCallCount { get; private set; }

        public string? LastRecordedKey { get; private set; }

        public Task<bool> HasClaimedAsync(string idempotencyKey, CancellationToken cancellationToken = default)
            => _inner.HasClaimedAsync(idempotencyKey, cancellationToken);

        public Task RecordClaimAsync(string idempotencyKey, DateTimeOffset claimedAt, CancellationToken cancellationToken = default)
        {
            RecordClaimCallCount++;
            LastRecordedKey = idempotencyKey;
            return _inner.RecordClaimAsync(idempotencyKey, claimedAt, cancellationToken);
        }
    }

    /// <summary>Real in-memory wallet, seeded, that additionally counts credits.</summary>
    private sealed class RecordingWallet(int initialBalance) : ICurrencyWallet
    {
        private readonly InMemoryCurrencyWallet _inner = new(PlayerEntityId, RewardCurrencyId, initialBalance);

        public int AddCallCount { get; private set; }

        public Task<int> GetBalanceAsync(string playerEntityId, string currencyId, CancellationToken cancellationToken = default)
            => _inner.GetBalanceAsync(playerEntityId, currencyId, cancellationToken);

        public Task<int> AddAsync(string playerEntityId, string currencyId, int amount, CancellationToken cancellationToken = default)
        {
            AddCallCount++;
            return _inner.AddAsync(playerEntityId, currencyId, amount, cancellationToken);
        }
    }

    /// <summary>Store whose write leg fails, standing in for a PlayFab player-data outage.</summary>
    private sealed class ThrowingStateStore : IDailyRewardStateStore
    {
        public Task<bool> HasClaimedAsync(string idempotencyKey, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task RecordClaimAsync(string idempotencyKey, DateTimeOffset claimedAt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated PlayFab player-data outage.");
    }

    /// <summary>Wallet whose credit leg fails, standing in for a PlayFab Economy outage.</summary>
    private sealed class ThrowingWallet(int initialBalance) : ICurrencyWallet
    {
        private readonly InMemoryCurrencyWallet _inner = new(PlayerEntityId, RewardCurrencyId, initialBalance);

        public Task<int> GetBalanceAsync(string playerEntityId, string currencyId, CancellationToken cancellationToken = default)
            => _inner.GetBalanceAsync(playerEntityId, currencyId, cancellationToken);

        public Task<int> AddAsync(string playerEntityId, string currencyId, int amount, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated PlayFab Economy outage.");
    }
}
