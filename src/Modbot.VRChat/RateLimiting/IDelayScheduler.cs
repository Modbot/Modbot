namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// How the limiter waits.
/// </summary>
/// <remarks>
/// Injected rather than called directly so a test can simulate hours of cold stop in
/// milliseconds. That is not a convenience: the behaviour spec 4.3.1 specifies is measured in
/// fifteen-minute waiting periods, and a suite that could only observe it in real time would
/// either be skipped or be rewritten to assert something cheaper and less true.
/// </remarks>
public interface IDelayScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct = default);
}

/// <summary>The production implementation: actually wait.</summary>
public sealed class RealDelayScheduler : IDelayScheduler
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
