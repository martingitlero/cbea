namespace GameBackend.Services;

/// <summary>
/// Server-owned reward configuration.
/// </summary>
/// <remarks>
/// These values are read from app settings (declared in <c>infra/main.bicep</c>),
/// never from the request. A client that could influence the quest id, the
/// currency, or the amount could mint currency at will, so nothing here is
/// bound from <see cref="Models.FunctionArgument"/>.
/// </remarks>
public sealed class DailyRewardOptions
{
    /// <summary>The only quest id this function services.</summary>
    public string QuestId { get; init; } = "daily_login";

    /// <summary>Virtual currency granted by a successful claim.</summary>
    public string CurrencyId { get; init; } = "currency.soft";

    /// <summary>Units of <see cref="CurrencyId"/> granted per UTC day.</summary>
    public int Amount { get; init; } = 50;

    /// <summary>
    /// Builds options from environment app settings, falling back to the
    /// documented defaults when a setting is absent or unparseable.
    /// </summary>
    public static DailyRewardOptions FromEnvironment()
    {
        var currency = Environment.GetEnvironmentVariable("DAILY_REWARD_CURRENCY_ID");
        var rawAmount = Environment.GetEnvironmentVariable("DAILY_REWARD_AMOUNT");

        return new DailyRewardOptions
        {
            CurrencyId = string.IsNullOrWhiteSpace(currency) ? "currency.soft" : currency,
            Amount = int.TryParse(rawAmount, out var amount) && amount > 0 ? amount : 50,
        };
    }
}
