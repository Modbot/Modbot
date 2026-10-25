namespace Modbot.Companion.Clips;

/// <summary>A recording the companion has: its file, when it began, and how long it runs.</summary>
/// <param name="Path">The file.</param>
/// <param name="StartedAt">When its first picture was taken, on this PC's clock.</param>
/// <param name="Length">How long it runs so far.</param>
public sealed record RecordingSpan(string Path, DateTimeOffset StartedAt, TimeSpan Length)
{
    public DateTimeOffset EndsAt => StartedAt + Length;
}

/// <summary>The part of one recording to cut, measured from the start of that recording.</summary>
/// <param name="Span">The recording.</param>
/// <param name="Start">Where the clip starts. The cutter may move it back to the nearest keyframe.</param>
/// <param name="End">Where the clip ends.</param>
/// <param name="ActionAt">Where the action falls in the recording.</param>
public sealed record ClipPlan(RecordingSpan Span, TimeSpan Start, TimeSpan End, TimeSpan ActionAt)
{
    /// <summary>
    /// The same clip starting later, for the one retry after a cut came out too big. Null when it
    /// cannot start any later without losing the action itself.
    /// </summary>
    public ClipPlan? Later()
    {
        var start = End - ((End - Start) / 2);
        if (start > ActionAt)
            start = ActionAt;

        return start > Start ? this with { Start = start } : null;
    }
}

/// <summary>What to do with a cut that has been tried.</summary>
public enum ClipNext
{
    /// <summary>Send the cut.</summary>
    UploadCut,

    /// <summary>The cut failed, but the whole recording is small enough to send.</summary>
    UploadWhole,

    /// <summary>Neither fits: tell the server the clip could not be cut.</summary>
    CouldNotCut,
}

/// <summary>
/// The timing, size and choice rules for answering a clip request. Pure: no clock, no files.
/// </summary>
/// <remarks>
/// Every time here is on one clock, the one the recordings' <see cref="RecordingSpan.StartedAt"/>
/// come from. Nothing is sent from here.
/// </remarks>
public static class ClipSendRules
{
    /// <summary>How much is kept after the action.</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(30);

    /// <summary>A little extra wait, so the last pictures after the action are in the file.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>How long before the request closes the companion stops waiting and starts sending.</summary>
    public static readonly TimeSpan LeaveToSend = TimeSpan.FromMinutes(2);

    /// <summary>The largest file the server takes (100 MB).</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>How much of the limit a cut is planned to fill. The rest is room for a picture that came out bigger than its average.</summary>
    public const double Fill = 0.9;

    /// <summary>
    /// When to look for the clip: once 30 seconds after the action have been recorded, but never
    /// later than the time left to send it in. Already past when the request came late.
    /// </summary>
    public static DateTimeOffset WaitUntil(DateTimeOffset action, DateTimeOffset expiresAt)
    {
        var wanted = action + After + Slack;
        var latest = expiresAt - LeaveToSend;
        return wanted < latest ? wanted : latest;
    }

    /// <summary>
    /// Which recording to cut and from where to where, or null when the action is in none of them.
    /// </summary>
    /// <remarks>
    /// A recording holds the action when it began at or before it and had not ended. Of those, one
    /// that reaches 30 seconds past the action is preferred, and among those the one that began
    /// first, because it holds the most from before. When none reaches that far, the one that
    /// reaches furthest is used. The clip ends 30 seconds after the action, or where the recording
    /// does. It starts as far back as fits in <paramref name="maxBytes"/> at the recorder's
    /// <paramref name="bitsPerSecond"/>, with <see cref="Fill"/> left over.
    /// </remarks>
    public static ClipPlan? Plan(
        DateTimeOffset action,
        IReadOnlyList<RecordingSpan> spans,
        long bitsPerSecond,
        long maxBytes)
    {
        var holding = spans
            .Where(s => s.StartedAt <= action && action < s.EndsAt)
            .ToList();

        if (holding.Count == 0)
            return null;

        var reaching = holding.Where(s => s.EndsAt >= action + After).OrderBy(s => s.StartedAt).ToList();
        var span = reaching.Count > 0
            ? reaching[0]
            : holding.OrderByDescending(s => s.EndsAt).ThenBy(s => s.StartedAt).First();

        var actionAt = action - span.StartedAt;
        var end = TimeSpan.FromTicks(Math.Min((actionAt + After).Ticks, span.Length.Ticks));

        var room = TimeSpan.FromSeconds(maxBytes * Fill * 8 / Math.Max(1, bitsPerSecond));
        var start = end - room;
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;

        return new ClipPlan(span, start, end, actionAt);
    }

    /// <summary>What to send once the cut has been tried.</summary>
    /// <param name="cutWorked">Whether a cut file was written.</param>
    /// <param name="cutBytes">The cut file's size.</param>
    /// <param name="wholeBytes">The whole recording's size.</param>
    public static ClipNext AfterCut(bool cutWorked, long cutBytes, long wholeBytes, long maxBytes)
    {
        if (cutWorked && cutBytes > 0 && cutBytes < maxBytes)
            return ClipNext.UploadCut;

        return wholeBytes > 0 && wholeBytes < maxBytes ? ClipNext.UploadWhole : ClipNext.CouldNotCut;
    }
}
