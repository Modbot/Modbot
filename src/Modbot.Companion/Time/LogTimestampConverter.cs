namespace Modbot.Companion.Time;

/// <summary>
/// Turns VRChat's bare wall-clock timestamps into real instants.
/// </summary>
/// <remarks>
/// <para><strong>The problem.</strong> VRChat writes <c>2026.09.03 20:27:14</c> and records no
/// timezone and no offset, anywhere in the file. Two moderators in different timezones produce
/// identical-looking timestamps for different instants, and a DST transition shifts them by an hour
/// mid-session. Every presence fact depends on getting this right, and getting it wrong produces
/// plausible wrong numbers rather than an error.</para>
/// <para><strong>Nothing here reads the machine clock</strong> — it converts a timestamp it is
/// handed. The timezone is supplied rather than looked up at each call, so tests and a moderator in
/// Perth exercise the same code.</para>
/// </remarks>
public sealed class LogTimestampConverter
{
    private readonly TimeZoneInfo _zone;

    public LogTimestampConverter(TimeZoneInfo? zone = null) => _zone = zone ?? TimeZoneInfo.Local;

    /// <summary>
    /// Reads a VRChat timestamp as an instant, resolving the two ways a local wall clock can fail
    /// to name one.
    /// </summary>
    public DateTimeOffset ToInstant(DateTime logTimestamp)
    {
        var naive = DateTime.SpecifyKind(logTimestamp, DateTimeKind.Unspecified);

        if (_zone.IsInvalidTime(naive))
        {
            // Spring forward: this wall-clock time never happened. VRChat can still write it if the
            // machine's clock was adjusted mid-session. Pushing it past the gap keeps the sequence
            // monotonic, which matters more than the hour, and the error is bounded by the size of
            // the gap.
            var gap = _zone.GetAdjustmentRules()
                .FirstOrDefault(r => naive >= r.DateStart && naive <= r.DateEnd)
                ?.DaylightDelta ?? TimeSpan.FromHours(1);

            naive = naive.Add(gap);
        }

        if (_zone.IsAmbiguousTime(naive))
        {
            // Autumn: this wall-clock hour happens twice and the log cannot say which. Taking the
            // first pass -- the larger offset -- is a deliberate coin flip, and the error it can
            // cause is bounded to one hour. The server's own observed_at stays the authority for
            // ordering regardless, which is what keeps this from mattering more than it does.
            var offsets = _zone.GetAmbiguousTimeOffsets(naive);
            return new DateTimeOffset(naive, offsets.Max());
        }

        return new DateTimeOffset(naive, _zone.GetUtcOffset(naive));
    }
}
