namespace Modbot.Companion.Pipeline;

/// <summary>What the log reader believes is going on.</summary>
public enum LogHealthStatus
{
    /// <summary>No log output at all. VRChat is probably not running; nothing is wrong.</summary>
    Idle,

    /// <summary>Lines are arriving and Modbot understands them.</summary>
    Healthy,

    /// <summary>
    /// VRChat is writing, Modbot can still read the lines, and none of the kind it reads have
    /// arrived for a while. Somebody sitting alone in an instance looks exactly like this, and it
    /// is not a fault.
    /// </summary>
    /// <remarks>
    /// The log has a heartbeat and it is not <c>[Behaviour]</c>: in the measured sample the largest
    /// gap between lines of any tag was eleven seconds, while the gap between <c>[Behaviour]</c>
    /// lines reached forty-five minutes with the moderator demonstrably still present. Research doc
    /// `vrchat-log-events.md` §1.0.
    /// </remarks>
    Quiet,

    /// <summary>
    /// Lines of the kind Modbot reads are arriving and it has recognised none of them for long
    /// enough that something is wrong: either VRChat changed its log format, or the verbose logging
    /// flags are missing.
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
/// <param name="LastLineAt">When a line of any tag was last read. VRChat is running.</param>
/// <param name="LastTimestampedLineAt">
/// When a line last arrived with VRChat's own timestamp at the front of it — the shape every line
/// in the file has had for years. It is what separates "VRChat is writing something else" from
/// "VRChat is writing something Modbot cannot read at all".
/// </param>
/// <param name="LastBehaviourLineAt">
/// When a <c>[Behaviour]</c> line last arrived. This is the line Modbot reads, so this is the clock
/// that says whether there was anything to recognise.
/// </param>
/// <param name="LastRecognisedAt">When a line last matched a shape Modbot acts on.</param>
public sealed record LogHealth(
    long LinesRead,
    long BehaviourLines,
    long RecognisedEvents,
    DateTimeOffset? LastLineAt,
    DateTimeOffset? LastRecognisedAt,
    DateTimeOffset? LastTimestampedLineAt = null,
    DateTimeOffset? LastBehaviourLineAt = null)
{
    public static LogHealth Empty { get; } = new(0, 0, 0, null, null);

    /// <summary>
    /// Distinguishes "VRChat is not running", "VRChat is running and nothing is happening" and
    /// "VRChat is running and I no longer understand it". All three look identical from the outside
    /// — nothing is being reported — and only the last is a fault.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why the behaviour clock and not the line clock.</strong> A moderator alone in
    /// an instance produces no joins, no leaves and no avatar changes for as long as they are
    /// alone, while VRChat keeps writing frame-rate and tracking lines several times a minute. Read
    /// off <see cref="LastLineAt"/> against <see cref="LastRecognisedAt"/> alone, that is
    /// indistinguishable from a parser that has stopped matching, and the client accuses itself of
    /// being broken for as long as the instance is calm. Measured against the 2026-09-03 sample it
    /// did so for 55% of a 64-minute session.</para>
    /// <para><strong>What is left.</strong> A <c>[Behaviour]</c> line arriving and not being
    /// recognised is the real signal, and it is a sharp one: only about 12% of <c>[Behaviour]</c>
    /// lines are shapes Modbot acts on, yet in that same session the rule below spends seven
    /// seconds in <see cref="LogHealthStatus.NotUnderstood"/> rather than 2,089.</para>
    /// <para><strong>And the format changing out from under all of it.</strong> If VRChat rewrote
    /// its lines so completely that the timestamp and tag no longer parse, there would be no
    /// <c>[Behaviour]</c> lines to count and a behaviour-only rule would read the break as quiet.
    /// So the line shape has its own clock: lines arriving, none of them in VRChat's own format, is
    /// a fault too.</para>
    /// </remarks>
    public LogHealthStatus Evaluate(DateTimeOffset now, TimeSpan silenceThreshold)
    {
        if (LastLineAt is not { } lastLine || now - lastLine > silenceThreshold)
            return LogHealthStatus.Idle;

        if (Recent(LastRecognisedAt))
            return LogHealthStatus.Healthy;

        // The lines Modbot reads are arriving and not one of them matched.
        if (Recent(LastBehaviourLineAt))
            return LogHealthStatus.NotUnderstood;

        // Something is being written and none of it is in VRChat's format.
        if (!Recent(LastTimestampedLineAt))
            return LogHealthStatus.NotUnderstood;

        return LogHealthStatus.Quiet;

        bool Recent(DateTimeOffset? at) => at is { } last && now - last <= silenceThreshold;
    }
}
