namespace GameBackend.Services;

/// <summary>Production <see cref="ISystemClock"/> backed by the machine clock.</summary>
public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
