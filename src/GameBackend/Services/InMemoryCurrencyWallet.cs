using System.Collections.Concurrent;

namespace GameBackend.Services;

/// <summary>
/// Thread-safe, process-local <see cref="ICurrencyWallet"/> used for local runs and tests.
/// A real deployment swaps this for the PlayFab Economy inventory API.
/// </summary>
public sealed class InMemoryCurrencyWallet : ICurrencyWallet
{
    private readonly ConcurrentDictionary<(string PlayerEntityId, string CurrencyId), int> _balances = new();

    /// <summary>Creates an empty wallet: every player starts at a zero balance. Used by DI.</summary>
    public InMemoryCurrencyWallet()
    {
    }

    /// <summary>
    /// Creates a wallet holding a single seeded balance, so a test can assert that a grant
    /// adds to an existing balance rather than overwriting it.
    /// </summary>
    public InMemoryCurrencyWallet(string playerEntityId, string currencyId, int initialBalance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyId);

        _balances[(playerEntityId, currencyId)] = initialBalance;
    }

    public Task<int> GetBalanceAsync(string playerEntityId, string currencyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyId);
        cancellationToken.ThrowIfCancellationRequested();

        _balances.TryGetValue((playerEntityId, currencyId), out var balance);
        return Task.FromResult(balance);
    }

    public Task<int> AddAsync(string playerEntityId, string currencyId, int amount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyId);
        cancellationToken.ThrowIfCancellationRequested();

        var newBalance = _balances.AddOrUpdate(
            (playerEntityId, currencyId),
            amount,
            (_, existing) => existing + amount);

        return Task.FromResult(newBalance);
    }
}
