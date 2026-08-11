namespace GameBackend.Services;

/// <summary>
/// Virtual-currency ledger for a player. In production this is fronted by the PlayFab
/// Economy API; the interface keeps the reward flow testable without a network hop.
/// </summary>
public interface ICurrencyWallet
{
    /// <summary>Current balance of <paramref name="currencyId"/> held by <paramref name="playerEntityId"/>.</summary>
    Task<int> GetBalanceAsync(string playerEntityId, string currencyId, CancellationToken cancellationToken = default);

    /// <summary>Credits <paramref name="amount"/> of <paramref name="currencyId"/> and returns the new balance.</summary>
    Task<int> AddAsync(string playerEntityId, string currencyId, int amount, CancellationToken cancellationToken = default);
}
