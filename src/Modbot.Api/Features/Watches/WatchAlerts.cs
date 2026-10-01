using System.Security.Cryptography;
using System.Text;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Core.Data.Entities;
using Modbot.Core.Notifications;

namespace Modbot.Api.Features.Watches;

/// <summary>
/// Tells the team when a watched person walks into one of the group's instances (watching a person
/// design §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same arrivals as the flagged-join card.</strong> Called from the companion's ingest
/// with the joins it already picked out: genuine arrivals, first report only, so six moderators in
/// one instance are one notification and somebody already there when a moderator arrived is none.
/// The headset card goes to the moderators standing in the instance; this goes through the
/// notification pipeline to everybody who could act on it from anywhere else.
/// </para>
/// <para>
/// <strong>Once per watch per instance, inside the quiet time.</strong> The key names the watch and
/// the instance, so somebody who leaves and comes back ten times in an evening is said once, and the
/// same person turning up in another instance is said again.
/// </para>
/// </remarks>
public static class WatchAlerts
{
    /// <summary>Who is told: everybody who may see live instances and read the moderation log.</summary>
    /// <remarks>
    /// Both, because what it leads to is where somebody is standing (the Live page's permission)
    /// and a watch, which is a fact in the moderation log.
    /// </remarks>
    public static readonly ModbotPermissions Audience = ModbotPermissions.ViewLiveInstances | ModbotPermissions.ViewAuditLog;

    /// <param name="arrivals">The genuine joins in this batch.</param>
    /// <param name="flagged">What the flag rules decided for the people in them.</param>
    /// <returns>How many notifications were raised.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Nothing about the person is written into the notification.</strong> Not their name,
    /// not their id and not the watch's reason: a notification row is kept after a purge has erased
    /// the person, and it goes out by email and Discord message. It says that a watched person
    /// joined and points at Live, where whoever opens it sees who under their own permissions.
    /// </para>
    /// <para>
    /// The key names the watch and a hash of the instance's id rather than the id itself, because a
    /// VRChat instance id can carry its owner's user id.
    /// </para>
    /// </remarks>
    public static async Task<int> RaiseAsync(
        INotifier notifier,
        IReadOnlyList<FactRecord> arrivals,
        IReadOnlyDictionary<string, FlagMatch> flagged,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(flagged);

        var raised = 0;

        foreach (var arrival in arrivals)
        {
            if (flagged.GetValueOrDefault(arrival.SubjectId)?.Watch is not { } watch)
                continue;

            await notifier.RaiseAsync(
                new Notification(
                    NotificationKinds.WatchedPersonJoined,
                    NotificationSeverity.Warning,
                    "Modbot: a watched person joined",
                    "A watched person joined a group instance.",
                    NotificationAudience.Holding(Audience))
                {
                    SameAs = $"{NotificationKinds.WatchedPersonJoined}:{watch.Id}:{InstanceKey(arrival.InstanceId)}",
                    Link = "/live",
                },
                ct);

            raised++;
        }

        return raised;
    }

    /// <summary>A short hash of an instance's id: the same instance, the same key, and no id in it.</summary>
    public static string InstanceKey(string? instanceId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId ?? string.Empty)))[..16].ToLowerInvariant();
}
