using Modbot.Core.Data.Entities;

namespace Modbot.Core.Notifications;

/// <summary>
/// The pipeline's rules, with no database and no clock (foundation §4.5.1).
/// </summary>
/// <remarks>
/// Every decision the pipeline makes about whether something goes out is here, as a function of
/// its arguments, because these are the rules a reader has to be able to check and the rules a
/// test has to be able to pin. What is left in <c>Notifier</c> is reading rows and writing them.
/// </remarks>
public static class NotificationRouting
{
    /// <summary>How often a person may get one daily summary on one channel.</summary>
    public static readonly TimeSpan SummaryEvery = TimeSpan.FromHours(24);

    /// <summary>Whether this severity goes out on a channel set to this level, as it happens.</summary>
    public static bool GoesNow(NotificationSeverity severity, string level) => level switch
    {
        NotificationLevels.Everything => true,
        NotificationLevels.Warning => severity >= NotificationSeverity.Warning,
        NotificationLevels.Critical => severity >= NotificationSeverity.Critical,
        _ => false,
    };

    /// <summary>
    /// Whether it waits for the daily summary instead.
    /// </summary>
    /// <remarks>
    /// Anything the level silenced goes in the summary when the summary is on, not only
    /// information. A warning that email is set not to interrupt for is still a warning, and a
    /// channel that drops it entirely would make "critical only" mean "and forget the rest".
    /// </remarks>
    public static bool GoesInSummary(NotificationSeverity severity, string level, bool dailySummary)
        => dailySummary && !GoesNow(severity, level);

    /// <summary>
    /// Whether this is the same thing again, close enough to the last time to stay quiet about.
    /// </summary>
    /// <param name="lastSeverity">How serious the notification already on record was.</param>
    /// <param name="lastAt">When it last happened.</param>
    /// <param name="severity">How serious this one is.</param>
    /// <param name="now">Now.</param>
    /// <param name="quiet">The quiet time.</param>
    /// <remarks>
    /// <para>
    /// The caller has already matched <c>SameAs</c>; this decides the rest. Inside the quiet time
    /// the notification is counted and nothing is sent — which is the whole point: a sync failing
    /// every minute is one message and then a number, not sixty messages.
    /// </para>
    /// <para>
    /// <strong>Unless it got worse.</strong> A warning that has become critical is news, and
    /// swallowing it would mean the severity that matters most is the one most likely to be eaten
    /// by a quiet time somebody set for something milder. Going the other way is not news: a
    /// critical that is now merely a warning is the same problem, still there.
    /// </para>
    /// </remarks>
    public static bool IsRepeat(
        NotificationSeverity lastSeverity,
        DateTimeOffset lastAt,
        NotificationSeverity severity,
        DateTimeOffset now,
        TimeSpan quiet)
        => severity <= lastSeverity && now - lastAt < quiet;

    /// <summary>
    /// Whether a daily summary is due on one channel for one person.
    /// </summary>
    /// <param name="oldestWaiting">When the oldest thing held for the summary was raised.</param>
    /// <param name="lastSummaryAt">When the last summary went out on this channel. Null means none has.</param>
    /// <param name="now">Now.</param>
    /// <remarks>
    /// Counted from the last summary, or from the oldest thing waiting when there has never been
    /// one. Counting from "now" would send a summary of one item the moment the first information
    /// notification landed, and then call it daily.
    /// </remarks>
    public static bool SummaryDue(DateTimeOffset oldestWaiting, DateTimeOffset? lastSummaryAt, DateTimeOffset now)
        => now - (lastSummaryAt ?? oldestWaiting) >= SummaryEvery;

    /// <summary>The quiet time the settings row asks for, kept inside its bounds.</summary>
    public static TimeSpan QuietTime(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return TimeSpan.FromHours(Math.Clamp(settings.QuietHours, 0, NotificationSettings.MaxQuietHours));
    }

    /// <summary>The stored name of a severity.</summary>
    public static string NameOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => NotificationSeverities.Critical,
        NotificationSeverity.Warning => NotificationSeverities.Warning,
        _ => NotificationSeverities.Information,
    };

    /// <summary>A stored name back into a severity. Anything unknown is the quietest.</summary>
    public static NotificationSeverity SeverityOf(string? name) => name switch
    {
        NotificationSeverities.Critical => NotificationSeverity.Critical,
        NotificationSeverities.Warning => NotificationSeverity.Warning,
        _ => NotificationSeverity.Information,
    };
}
