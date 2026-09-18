using Modbot.Core.Data.Entities;

namespace Modbot.Core.Notifications;

/// <summary>How serious a notification is (foundation §4.5.1).</summary>
/// <remarks>
/// Compared, never stored: the database keeps the name (<see cref="NotificationSeverities"/>), so a
/// severity added later never renumbers the rows already written.
/// </remarks>
public enum NotificationSeverity
{
    /// <summary>Worth knowing, never worth interrupting for. Daily summary only.</summary>
    Information = 1,

    /// <summary>Something needs a person before long.</summary>
    Warning = 2,

    /// <summary>Modbot cannot do its job. Every channel a person has, straight away.</summary>
    Critical = 3,
}

/// <summary>
/// The things Modbot raises notifications about, as dotted names like fact types.
/// </summary>
/// <remarks>
/// Text rather than an enum, for the reason fact types are (foundation §5.3.1): a deployment whose
/// database was written by a newer Modbot keeps rows it does not understand rather than losing them,
/// and a new kind never renumbers an old one.
/// </remarks>
public static class NotificationKinds
{
    /// <summary>One of Modbot's own health checks started failing.</summary>
    public const string HealthProblem = "modbot.health.problem";

    /// <summary>One of Modbot's own health checks started working again.</summary>
    public const string HealthRecovered = "modbot.health.recovered";

    /// <summary>The evidence store is not where it should be (evidence storage design §8.4).</summary>
    public const string EvidenceStoreMissing = "modbot.evidence.store-missing";

    /// <summary>A group instance dropped off VRChat's list with people still in it (M6 §5).</summary>
    public const string InstanceClosedPopulated = "vrchat.instance.closed-populated";
}

/// <summary>Who a notification is for.</summary>
/// <remarks>
/// <para>
/// Two ways of saying it, because the two real cases are different. Health problems go to the
/// accounts somebody chose on the settings screen — an explicit list. Everything else goes to
/// whoever holds the permission that makes the message mean something, so a group that adds a
/// moderator does not also have to remember to add them to a list.
/// </para>
/// <para>
/// Administrators are always included in a permission audience, the same way
/// <c>RequiresFlag</c> treats them: a permission added after an account was made is covered
/// without anybody editing anything.
/// </para>
/// </remarks>
public sealed record NotificationAudience
{
    private NotificationAudience(ModbotPermissions? permission, IReadOnlyList<Guid>? userIds)
    {
        Permission = permission;
        UserIds = userIds;
    }

    /// <summary>The permission everyone in the audience holds. Null when the audience is a list.</summary>
    public ModbotPermissions? Permission { get; }

    /// <summary>The accounts in the audience. Null when the audience is a permission.</summary>
    public IReadOnlyList<Guid>? UserIds { get; }

    /// <summary>Everyone holding this permission, plus every administrator.</summary>
    public static NotificationAudience Holding(ModbotPermissions permission) => new(permission, null);

    /// <summary>These accounts and nobody else.</summary>
    public static NotificationAudience These(IEnumerable<Guid> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        return new(null, userIds.Distinct().ToList());
    }

    /// <summary>Every administrator. What a problem with Modbot itself is for.</summary>
    public static NotificationAudience Administrators { get; } = new(ModbotPermissions.Administrator, null);
}

/// <summary>
/// One thing somebody should be told about (foundation §4.5).
/// </summary>
/// <param name="Kind">What happened. <see cref="NotificationKinds"/>.</param>
/// <param name="Severity">How serious it is. The first routing input.</param>
/// <param name="Title">One line: an email subject, the first line of a direct message.</param>
/// <param name="Body">A sentence or two somebody can act on.</param>
/// <param name="Audience">Who it is for.</param>
public sealed record Notification(
    string Kind,
    NotificationSeverity Severity,
    string Title,
    string Body,
    NotificationAudience Audience)
{
    /// <summary>Where in Modbot to look, as a path such as <c>/health</c>.</summary>
    public string? Link { get; init; }

    /// <summary>
    /// What two notifications have to share to count as the same one, inside the quiet time.
    /// </summary>
    /// <remarks>
    /// <strong>It names the thing that is wrong, not the moment it was noticed.</strong> The sync
    /// being stopped is one key however often the check runs, so it is said once and then counted.
    /// A key that includes the time, the count, or anything else that moves is not a key at all:
    /// every raise looks new and the pipeline sends every minute, which is the failure mode
    /// §4.5.1 exists to prevent. Left unset it is the <see cref="Kind"/>, which is right whenever
    /// there is only one of the thing.
    /// </remarks>
    public string? SameAs { get; init; }

    /// <summary>The key actually used. <see cref="SameAs"/> when it is set, otherwise the kind.</summary>
    public string SameAsKey => string.IsNullOrWhiteSpace(SameAs) ? Kind : SameAs;

    /// <summary>
    /// How long this particular thing stays quiet after it has been said. Null uses the setting.
    /// </summary>
    /// <remarks>
    /// Here because two things that were built before this pipeline already had a quiet time of
    /// their own with a control on a settings screen. Moving them onto the pipeline should not
    /// quietly change the number an operator chose, so the caller can keep saying it.
    /// </remarks>
    public TimeSpan? Quiet { get; init; }
}

/// <summary>What raising a notification did.</summary>
/// <param name="Id">The record's id. Null when nothing was written.</param>
/// <param name="Repeat">True when this was the same thing again inside the quiet time, so nothing went out.</param>
/// <param name="People">How many accounts it was addressed to.</param>
/// <param name="Sending">How many messages are on their way.</param>
/// <param name="ForSummary">How many are held for a daily summary.</param>
/// <param name="NobodyCouldReceive">True when it reached nobody on any channel.</param>
public sealed record NotificationOutcome(
    Guid? Id,
    bool Repeat,
    int People,
    int Sending,
    int ForSummary,
    bool NobodyCouldReceive)
{
    public static NotificationOutcome Nothing { get; } = new(null, false, 0, 0, 0, false);
}

/// <summary>
/// The one way anything in Modbot says somebody should be told something (foundation §4.5).
/// </summary>
/// <remarks>
/// <para>
/// Channels are delivery mechanisms behind this, not parallel systems: a new kind of event reaches
/// every channel for free, and a new channel serves every existing kind for free. That is the whole
/// reason it is one pipeline rather than a Discord feature and an email feature.
/// </para>
/// <para>
/// <strong>Raising never fails the thing that raised it.</strong> A ban does not fail because SMTP
/// is down. Nothing here throws for a delivery problem; sending happens out of band afterwards and
/// failures are recorded, not returned.
/// </para>
/// </remarks>
public interface INotifier
{
    Task<NotificationOutcome> RaiseAsync(Notification notification, CancellationToken ct = default);
}
