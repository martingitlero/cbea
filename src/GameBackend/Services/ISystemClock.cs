namespace GameBackend.Services;

/// <summary>
/// Abstraction over the ambient clock so date-sensitive logic (the UTC-day rollover of a
/// daily reward) is deterministic under test.
/// </summary>
public interface ISystemClock
{
    /// <summary>Current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
