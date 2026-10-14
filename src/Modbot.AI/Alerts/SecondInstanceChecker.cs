using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.AI.Alerts;

/// <summary>
/// Raises the "open a second instance" alert when an open instance of the group holds as many people
/// as the operator asked for, or as much of its world's capacity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per instance, for its whole life.</strong> An instance that drops back and fills again
/// does not alert twice: the second instance it asked for is already open, or was not wanted, and a
/// card every time the head count wobbles around the line is worse than one card. The alert row names
/// the instance and the database allows one such row per instance, so two copies of Modbot on one
/// database cannot both raise it.
/// </para>
/// <para>
/// Not a statistical watcher: no sensitivity, no quiet time, and it runs every minute with the
/// scheduler rather than every quarter hour, because the head counts it reads change every 30 seconds
/// and a full instance is something to act on now.
/// </para>
/// <para>
/// A percent is of <see cref="VRChatWorld.Capacity"/>. A world whose capacity is not known never
/// reaches a percent; Modbot does not guess one.
/// </para>
/// </remarks>
public sealed class SecondInstanceChecker(
    ModbotContext db,
    IFactWriter facts,
    EventPartitionMaintainer partitions,
    IModbotClock clock)
{
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Analytics);

    /// <summary>Where in Modbot to look.</summary>
    public const string LinkPath = "/live";

    /// <summary>Raises the alerts that are due. Returns how many were stored.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var settings = await db.AlertSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (settings is null || (settings.SecondInstanceAtPeople is null && settings.SecondInstanceAtPercent is null))
            return 0;

        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is not { Length: > 0 })
            return 0;

        var open = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null && i.HeadCount != null)
            .OrderByDescending(i => i.HeadCount)
            .Take(50)
            .ToListAsync(ct);

        if (open.Count == 0)
            return 0;

        var worldIds = open.Select(i => i.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var worlds = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.Capacity })
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        var ids = open.Select(i => (Guid?)i.Id).ToList();
        var already = await db.Alerts.AsNoTracking()
            .Where(a => a.InstanceId != null && ids.Contains(a.InstanceId))
            .Select(a => a.InstanceId!.Value)
            .ToListAsync(ct);

        var alerted = already.ToHashSet();
        var now = clock.UtcNow;
        var written = 0;

        foreach (var instance in open)
        {
            if (alerted.Contains(instance.Id))
                continue;

            worlds.TryGetValue(instance.WorldId, out var world);

            var line = Threshold(settings.SecondInstanceAtPeople, settings.SecondInstanceAtPercent, world?.Capacity);

            if (line is null || instance.HeadCount!.Value < line.Value)
                continue;

            if (await StoreAsync(instance, world?.Name ?? instance.WorldId, line.Value, settings, now, ct))
                written++;
        }

        return written;
    }

    /// <summary>
    /// The head count that raises the alert: the lower of the two settings that apply, or null when none
    /// does. A percent needs the world's capacity.
    /// </summary>
    public static int? Threshold(int? people, int? percent, int? capacity)
    {
        int? line = people is > 0 ? people : null;

        if (percent is > 0 && capacity is > 0)
        {
            // Rounded up: 80% of 12 is 9.6 people, and nine people are not yet 80%.
            var share = (int)Math.Ceiling(capacity.Value * (long)percent.Value / 100.0);
            line = line is { } p ? Math.Min(p, share) : share;
        }

        return line;
    }

    private async Task<bool> StoreAsync(
        VRChatInstance instance, string where, int line, AlertSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var figures = new AlertFigures(
            AlertWatchers.InstanceSecond,
            AlertWatchers.Label(AlertWatchers.InstanceSecond),
            AlertWatchers.Counts(AlertWatchers.InstanceSecond),
            instance.OpenedAt,
            now,
            instance.HeadCount!.Value,
            line,
            0m,
            0m,
            AlertSensitivities.Normal,
            [],
            where,
            LinkPath);

        var alert = new Alert
        {
            Watcher = figures.Watcher,
            At = now,
            WindowStart = figures.WindowStart,
            WindowEnd = figures.WindowEnd,
            Now = figures.Now,
            Normal = figures.Normal,
            Spread = 0m,
            Score = 0m,
            Sensitivity = figures.Sensitivity,
            Figures = figures.ToJson(),
            Link = figures.Link,
            DiscordChannelId = settings.DiscordChannelId,
            InstanceId = instance.Id,
        };

        db.Alerts.Add(alert);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // Another copy of Modbot raised it a moment ago.
            db.Entry(alert).State = EntityState.Detached;
            _log.Debug(e, "The second-instance alert for an instance was raised already");
            return false;
        }

        await partitions.EnsureForAsync(now, ct);
        await facts.WriteAsync(new FactRecord
        {
            Type = FactType.InsightAlert,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = figures.Watcher,
            Source = FactSource.Modbot,
            Data = new JsonObject
            {
                ["watcher"] = figures.Watcher,
                ["label"] = figures.Label,
                ["counts"] = figures.Counts,
                ["from"] = figures.WindowStart,
                ["to"] = figures.WindowEnd,
                ["now"] = figures.Now,
                ["normal"] = figures.Normal,
                ["score"] = figures.Score,
                ["sensitivity"] = figures.Sensitivity,
                ["where"] = figures.Where,
                ["link"] = figures.Link,
                ["text"] = null,
            },
        }, ct);

        return true;
    }
}
