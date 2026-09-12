using Modbot.Core.Time;

namespace Modbot.TestSupport;

/// <summary>
/// Deterministic clock for tests. Every test that needs time uses this, never the system clock.
/// </summary>
public sealed class FakeClock : IModbotClock
{
    public FakeClock(DateTimeOffset? start = null)
        => UtcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
