namespace Modbot.Core.Time;

/// <summary>
/// Modbot's single source of time.
/// </summary>
/// <remarks>
/// Nothing in Modbot may read the system clock directly. Analytics correctness depends on
/// timestamps from machines Modbot does not control (moderator PCs running the Windows client),
/// and the rate limiter must not believe a penalty expired because the host clock stepped.
/// Both problems are solved by there being exactly one clock. See foundation spec section 4.4.
/// </remarks>
public interface IModbotClock
{
    /// <summary>The current instant, always with a zero offset.</summary>
    DateTimeOffset UtcNow { get; }
}
