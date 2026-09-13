using Modbot.Core.Time;

namespace Modbot.Client.Time;

/// <summary>How much the client's correction to server time can be trusted.</summary>
/// <remarks>
/// Rides on every batch so the server can see how much to trust the timestamps instead of guessing.
/// Protocol section 5.
/// </remarks>
public enum ClockConfidence
{
    /// <summary>Never measured. Timestamps are raw machine time.</summary>
    Unknown,

    /// <summary>One measurement, wildly disagreeing measurements, or a stale one.</summary>
    Poor,

    /// <summary>Consistent, but over a link slow or variable enough to matter.</summary>
    Fair,

    /// <summary>Several agreeing measurements over a fast link.</summary>
    Good,
}

public static class ClockConfidenceExtensions
{
    /// <summary>The protocol's spelling: lowercase, stable, branched on by the server.</summary>
    public static string ToWire(this ClockConfidence confidence) => confidence switch
    {
        ClockConfidence.Poor => "poor",
        ClockConfidence.Fair => "fair",
        ClockConfidence.Good => "good",
        _ => "unknown",
    };
}

/// <summary>
/// One measurement of how far the machine's clock is from a server's.
/// </summary>
/// <param name="SentAt">t0 — when the client asked, by its own clock.</param>
/// <param name="ServerTime">The instant the server reported.</param>
/// <param name="ReceivedAt">t3 — when the answer landed, by the client's own clock.</param>
public readonly record struct ClockSample(
    DateTimeOffset SentAt,
    DateTimeOffset ServerTime,
    DateTimeOffset ReceivedAt)
{
    public TimeSpan RoundTrip => ReceivedAt - SentAt;

    /// <summary>
    /// The SNTP estimate. The server's answer is assumed to have been produced halfway through the
    /// round trip, which is exactly right when the two legs are equally fast and wrong in
    /// proportion to how unequal they are — hence keeping the fastest samples and discarding the
    /// rest.
    /// </summary>
    public TimeSpan Offset => ServerTime - (SentAt + (RoundTrip / 2));
}

/// <summary>
/// Tracks the difference between this machine's clock and one server's, so presence facts can be
/// reported in server time.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> Analytics correctness depends on timestamps from
/// machines Modbot does not control. A moderator's PC ten minutes out would silently corrupt
/// session durations and every time-bucketed metric — producing plausible wrong numbers rather
/// than an error. Foundation 4.4.</para>
/// <para><strong>It never changes the machine's clock.</strong> The correction is applied to
/// reported timestamps and to nothing else. Modbot is a guest on a personal PC.</para>
/// <para><strong>Nothing is transmitted from here.</strong> The offset and the confidence do ride
/// along on each batch, and they say only "this machine's clock is N milliseconds out" — which is
/// what lets the server keep a deduplication window narrow enough to preserve a genuine
/// fifteen-second rejoin.</para>
/// </remarks>
public sealed class ServerClock
{
    /// <summary>How many measurements are kept. A stepped clock must be able to age out.</summary>
    public const int SampleWindow = 8;

    /// <summary>
    /// After this, a measurement is no longer evidence about now. Laptops sleep, VMs migrate, and
    /// NTP steps the clock without telling anybody.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private static readonly TimeSpan GoodEnough = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BarelyUsable = TimeSpan.FromSeconds(2);

    private readonly IModbotClock _clock;
    private readonly Queue<ClockSample> _samples = new();

    private TimeSpan _offset;
    private ClockConfidence _confidence = ClockConfidence.Unknown;
    private DateTimeOffset? _measuredAt;

    public ServerClock(IModbotClock clock) => _clock = clock;

    /// <summary>The correction to add to a local instant to get server time.</summary>
    public TimeSpan Offset => _offset;

    public ClockConfidence Confidence =>
        _measuredAt is { } at && _clock.UtcNow - at > StaleAfter && _confidence != ClockConfidence.Unknown
            ? ClockConfidence.Poor
            : _confidence;

    /// <summary>Records one probe and re-estimates.</summary>
    public void Add(ClockSample sample)
    {
        _samples.Enqueue(sample);
        while (_samples.Count > SampleWindow)
            _samples.Dequeue();

        _measuredAt = _clock.UtcNow;
        Recalculate();
    }

    /// <summary>Restates a local instant in the server's time.</summary>
    public DateTimeOffset ToServerTime(DateTimeOffset localInstant) => localInstant + _offset;

    private void Recalculate()
    {
        if (_samples.Count == 0)
        {
            _offset = TimeSpan.Zero;
            _confidence = ClockConfidence.Unknown;
            return;
        }

        var fastest = _samples.Min(s => s.RoundTrip);

        // Keep the samples whose round trip was close to the fastest seen. A probe that sat in a
        // queue for two seconds tells you almost nothing about the offset, because the delay was
        // not symmetric -- averaging it in drags the estimate towards the asymmetry rather than
        // away from it. The small floor stops a freakishly fast sample excluding every other.
        var cutoff = fastest + fastest + TimeSpan.FromMilliseconds(5);
        var kept = _samples.Where(s => s.RoundTrip <= cutoff).Select(s => s.Offset).Order().ToArray();

        // Median rather than mean: it is the smoothing that a single surviving outlier cannot move.
        _offset = kept.Length % 2 == 1
            ? kept[kept.Length / 2]
            : (kept[(kept.Length / 2) - 1] + kept[kept.Length / 2]) / 2;

        var spread = kept[^1] - kept[0];
        _confidence = (kept.Length, spread, fastest) switch
        {
            (1, _, _) => ClockConfidence.Poor,
            (_, _, _) when spread > BarelyUsable || fastest > BarelyUsable => ClockConfidence.Poor,
            (_, _, _) when spread <= GoodEnough && fastest <= GoodEnough => ClockConfidence.Good,
            _ => ClockConfidence.Fair,
        };
    }
}
