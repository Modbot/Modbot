namespace Modbot.Companion.Pipeline;

/// <summary>What the log reader believes is going on.</summary>
public enum LogHealthStatus
{
    /// <summary>No log output at all. VRChat is probably not running; nothing is wrong.</summary>
    Idle,

    /// <summary>Lines are arriving and Modbot understands them.</summary>
    Healthy,

    /// <summary>
    /// Lines are arriving and Modbot has recognised none of them for long enough that something is
    /// wrong: either VRChat changed its log format, or the verbose logging flags are missing.
    /// </summary>
    /// <remarks>
    /// This is the condition worth waking somebody for. A log parser that silently stops matching
    /// is the worst outcome available — presence history stops accruing, nobody notices for weeks,
    /// and the gap cannot be filled in later. M3 2.2.
    /// </remarks>
    NotUnderstood,
}

/// <summary>
/// Counters for how much of the log the client is reading and how much of it it understands.
/// </summary>
/// <param name="LinesRead">Every line handed over by the tail, of any tag.</param>
/// <param name="BehaviourLines">
/// The <c>[Behaviour]</c> subset — about 4% of a real log. The ratio is worth being able to state
/// plainly, because "we read one tag out of a dozen" is a claim a moderator can check.
/// </param>
/// <param name="RecognisedEvents">Lines that matched a shape Modbot acts on: fewer still.</param>
public sealed record LogHealth(
    long LinesRead,
    long BehaviourLines,
    long RecognisedEvents,
    DateTimeOffset? LastLineAt,
    DateTimeOffset? LastRecognisedAt)
{
    public static LogHealth Empty { get; } = new(0, 0, 0, null, null);

    /// <summary>
    /// Distinguishes "VRChat is not running" from "VRChat is running and I no longer understand
    /// it". The two look identical from the outside — nothing is being reported — and only one of
    /// them is a fault.
    /// </summary>
    public LogHealthStatus Evaluate(DateTimeOffset now, TimeSpan silenceThreshold)
    {
        if (LastLineAt is not { } lastLine || now - lastLine > silenceThreshold)
            return LogHealthStatus.Idle;

        if (LastRecognisedAt is { } lastRecognised && now - lastRecognised <= silenceThreshold)
            return LogHealthStatus.Healthy;

        return LogHealthStatus.NotUnderstood;
    }
}
