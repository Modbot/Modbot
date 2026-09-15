using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.EventBackup;

/// <summary>How one batch's times were read.</summary>
/// <param name="Observed">Cloud's received time minus the client's sent time.</param>
/// <param name="Applied">The offset Cloud trusts: what to add to the client PC's own clock to get Cloud's.</param>
/// <param name="Disagrees">The client's own measure and Cloud's are more than five minutes apart.</param>
/// <param name="Adjustment">
/// What to add to the client's already-corrected event times. Zero when the client's measure is
/// trusted, because it has already applied it.
/// </param>
public readonly record struct ClockReading(TimeSpan Observed, TimeSpan Applied, bool Disagrees, TimeSpan Adjustment);

/// <summary>
/// Turns a client's event times into Cloud's (cloud event backup spec 5).
/// </summary>
/// <remarks>
/// <para>
/// The server's approach (foundation 4.4): the client measures its offset against Cloud's clock the
/// SNTP way, corrects every event time by it before sending (protocol 5), and sends the offset too.
/// Cloud does not simply trust it: it also measures received time minus sent time, which is the PC's
/// clock error plus one request's network delay.
/// </para>
/// <para>
/// The client's measure stands when it says it is <c>good</c> or <c>fair</c> and agrees with Cloud's
/// within <see cref="InstallClock.DisagreeAbove"/>: it is the more precise, because it removes the
/// network delay. Otherwise Cloud undoes the client's correction and applies its own.
/// </para>
/// </remarks>
public static class ClockCorrection
{
    public static ClockReading Read(DateTimeOffset receivedAt, DateTimeOffset sentAt, long? reportedOffsetMs, string confidence)
    {
        var observed = receivedAt - sentAt;
        var reported = TimeSpan.FromMilliseconds(reportedOffsetMs ?? 0);

        if (reportedOffsetMs is null || confidence == "unknown")
            return new ClockReading(observed, observed, false, observed - reported);

        var disagrees = (reported - observed).Duration() > InstallClock.DisagreeAbove;
        var trusted = confidence is "good" or "fair" && !disagrees;
        var applied = trusted ? reported : observed;

        return new ClockReading(observed, applied, disagrees, applied - reported);
    }
}
