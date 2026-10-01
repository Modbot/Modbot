using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Core.Data;
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
    /// Both, because the message says where somebody is standing (the Live page's permission) and
    /// why they are watched (a fact in the moderation log).
    /// </remarks>
    public static readonly ModbotPermissions Audience = ModbotPermissions.ViewLiveInstances | ModbotPermissions.ViewAuditLog;

    /// <param name="arrivals">The genuine joins in this batch.</param>
    /// <param name="flagged">What the flag rules decided for the people in them.</param>
    /// <param name="names">The names the batch carried, by person.</param>
    /// <returns>How many notifications were raised.</returns>
    public static async Task<int> RaiseAsync(
        INotifier notifier,
        ModbotContext db,
        IReadOnlyList<FactRecord> arrivals,
        IReadOnlyDictionary<string, FlagMatch> flagged,
        IReadOnlyDictionary<string, string?> names,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(flagged);
        ArgumentNullException.ThrowIfNull(names);

        var watched = arrivals
            .Where(a => flagged.GetValueOrDefault(a.SubjectId)?.Watch is not null)
            .ToList();

        if (watched.Count == 0)
            return 0;

        var people = watched.Select(a => a.SubjectId).Distinct(StringComparer.Ordinal).ToList();
        var worldIds = watched.Where(a => a.WorldId is not null).Select(a => a.WorldId!).Distinct(StringComparer.Ordinal).ToList();

        var storedNames = await db.VRChatUsers.AsNoTracking()
            .Where(u => people.Contains(u.UserId) && u.DisplayName != null)
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);

        var worldNames = worldIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await db.VRChatWorlds.AsNoTracking()
                .Where(w => worldIds.Contains(w.WorldId) && w.Name != null)
                .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct);

        var raised = 0;

        foreach (var arrival in watched)
        {
            var watch = flagged[arrival.SubjectId].Watch!;
            var name = names.GetValueOrDefault(arrival.SubjectId) ?? storedNames.GetValueOrDefault(arrival.SubjectId) ?? arrival.SubjectId;
            var where = arrival.WorldId is { } world && worldNames.TryGetValue(world, out var worldName)
                ? worldName
                : "one of the group's instances";

            await notifier.RaiseAsync(
                new Notification(
                    NotificationKinds.WatchedPersonJoined,
                    NotificationSeverity.Warning,
                    "Modbot: a watched person joined",
                    $"{name} joined {where}.\n\nWatched: {watch.Reason}",
                    NotificationAudience.Holding(Audience))
                {
                    SameAs = $"{NotificationKinds.WatchedPersonJoined}:{watch.Id}:{arrival.InstanceId}",
                    Link = WatchLinks.Person(FactPlatform.VRChat, arrival.SubjectId, "/live"),
                },
                ct);

            raised++;
        }

        return raised;
    }
}
