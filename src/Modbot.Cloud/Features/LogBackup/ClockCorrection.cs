using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>How one batch's times were read.</summary>
/// <param name="Observed">Cloud's received time minus the client's sent time.</param>
/// <param name="Applied">The correction added to the client's times to get Cloud's.</param>
/// <param name="Disagrees">The client's own measure and Cloud's are more than five minutes apart.</param>
public readonly record struct ClockReading(TimeSpan Observed, TimeSpan Applied, bool Disagrees);

/// <summary>
/// Turns a client PC's times into Cloud's (cloud log backup spec 5).
/// </summary>
/// <remarks>
/// <para>
/// The server's approach (foundation 4.4): the client measures its offset against Cloud's clock the
/// SNTP way and sends it, and Cloud does not simply trust it. Cloud measures too, as received time
/// minus sent time, which is the PC's clock error plus one request's network delay.
/// </para>
/// <para>
/// The client's measure is used when it says it is <c>good</c> or <c>fair</c> and it agrees with
/// Cloud's within <see cref="InstallClock.DisagreeAbove"/>: it is the more precise of the two,
/// because it removes the network delay. Otherwise Cloud's own measure is used, which is never
/// more than a request's delay out.
/// </para>
/// </remarks>
public static class ClockCorrection
{
    public static ClockReading Read(DateTimeOffset receivedAt, DateTimeOffset sentAt, long? reportedOffsetMs, string confidence)
    {
        var observed = receivedAt - sentAt;

        if (reportedOffsetMs is not { } reportedMs || confidence == "unknown")
            return new ClockReading(observed, observed, false);

        var reported = TimeSpan.FromMilliseconds(reportedMs);
        var disagrees = (reported - observed).Duration() > InstallClock.DisagreeAbove;
        var trusted = confidence is "good" or "fair" && !disagrees;

        return new ClockReading(observed, trusted ? reported : observed, disagrees);
    }

    /// <summary>
    /// A log timestamp as an instant in Cloud's time, or null when there is not enough to say.
    /// </summary>
    public static DateTimeOffset? OccurredAt(DateTime? loggedAt, short? utcOffsetMinutes, TimeSpan applied)
    {
        if (loggedAt is not { } logged || utcOffsetMinutes is not { } minutes)
            return null;

        try
        {
            var local = new DateTimeOffset(DateTime.SpecifyKind(logged, DateTimeKind.Unspecified), TimeSpan.FromMinutes(minutes));
            return local.ToUniversalTime() + applied;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Year 1 or 9999 plus an offset: a timestamp nobody could mean.
            return null;
        }
    }
}
