using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.ProblemAvatars;

/// <summary>
/// Raises the flagged alert for somebody who arrives in, or switches to, an avatar on the problem
/// avatar list (avatar watch design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same card, the same bleep, the same Discord post as any flagged join.</strong> The
/// alert is a <see cref="FlaggedJoinAlertDto"/> and goes through the same hub, so the moderators in
/// that instance see it the way they see any flagged person.
/// </para>
/// <para>
/// <strong>The reporter is told too.</strong> Nothing in the reporting moderator's own client knows
/// the list, so unlike a flagged join it cannot already have raised this one itself.
/// </para>
/// <para>
/// <strong>Only the first report of a person in that avatar.</strong> The fact log de-duplicates one
/// report of one event; <see cref="ProblemAvatarSeen"/> stops the next moderator's sighting of the
/// same person in the same avatar from sounding it again.
/// </para>
/// </remarks>
public static class ProblemAvatarAlerts
{
    public static async Task RaiseAsync(
        IReadOnlyList<FactRecord> written,
        IReadOnlyList<FactWriteResult> results,
        ICompanionDeviceStore devices,
        AlertHub alerts,
        ProblemAvatarSeen seen,
        IDiscordChannelPoster? discord,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        var sightings = written
            .Where((fact, index) =>
                fact.Type is FactType.AvatarChanged or FactType.InstanceJoined or FactType.InstancePresenceObserved
                && !results[index].WasDeduplicated
                && fact.InstanceId is { Length: > 0 }
                && AuditJson.Text(fact.Data, "avatarName") is { Length: > 0 })
            .ToList();

        if (sightings.Count == 0)
            return;

        var listed = await database.ProblemAvatars.AsNoTracking()
            .Select(a => new { a.NameKey })
            .ToListAsync(ct);

        if (listed.Count == 0)
            return;

        // An entry added by id that VRChat gave no name for has an empty key and matches nobody.
        var keys = listed.Select(a => a.NameKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

        var hits = sightings
            .Where(f => keys.Contains(ProblemAvatar.KeyOf(AuditJson.Text(f.Data, "avatarName")!)))
            .Where(f => seen.TryMark(
                f.SubjectId, ProblemAvatar.KeyOf(AuditJson.Text(f.Data, "avatarName")!), f.WorldId, f.InstanceId!, clock.UtcNow))
            .ToList();

        if (hits.Count == 0)
            return;

        var ranks = await ContextHandler.TrustRanksAsync(database, hits.Select(f => f.SubjectId).Distinct(StringComparer.Ordinal).ToList(), ct);
        var rules = FlagRuleSettings.Read(
            await database.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.FlagRules).FirstOrDefaultAsync(ct));

        var paired = await devices.ListDevicesAsync(ct);
        var recipients = paired.Where(d => !d.IsRevoked).Select(d => d.Id).ToList();

        foreach (var hit in hits)
        {
            var avatarName = AuditJson.Text(hit.Data, "avatarName")!;
            var displayName = AuditJson.Text(hit.Data, "displayName");

            alerts.Raise(
                AlertHub.ForProblemAvatar(
                    clock, hit.SubjectId, displayName, hit.InstanceId!, avatarName, ranks.GetValueOrDefault(hit.SubjectId), hit.WorldId),
                Guid.Empty,
                recipients,
                clock.UtcNow);

            if (discord is null)
                continue;

            try
            {
                await FlaggedDiscordPost.PostAsync(discord, rules, FlaggedDiscordPost.AvatarText(displayName, avatarName), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The headset card has already gone out.
                Serilog.Log.Warning(e, "Could not post a problem avatar in Discord");
            }
        }
    }
}
