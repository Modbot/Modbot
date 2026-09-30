using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Writes "ended on its own" into the audit log for the group's instances that ended and were not
/// closed by hand.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why Modbot writes this itself.</strong> VRChat's audit log has an entry for an instance a
/// moderator closes by hand and none for one that empties out and drops off the group's list, which is
/// how nearly every instance ends. Modbot knows those ends from its own instance list. Without an
/// entry the audit log said the group opened four instances and closed none, and a moderator reading
/// it could not tell that from a log that had lost something.
/// </para>
/// <para>
/// <strong>Never worded "closed", and never written too early.</strong> The entry claims no
/// moderator closed the instance, and that cannot be known until VRChat's audit log has been read past
/// the instance's end: VRChat writes a close late now and then. So an end waits until the audit log
/// has been read to its newest entry by a pass that began after the end, and the few minutes a
/// close entry may be dated after it (<see cref="InstanceCloseEntries.Leeway"/>). An audit log
/// that has never been read to the end writes nothing, however long it takes. Which instances a
/// moderator closed is decided by the same rule the instance rows and the Stats page use
/// (<see cref="InstanceCloseEntries"/>).
/// </para>
/// <para>
/// <strong>Catch-up.</strong> The first passes after an update find every instance that ended before
/// this existed and write its entry at the end time Modbot recorded for it, never at the time of
/// writing. Those carry <c>catchUp</c> in their payload, and the Discord log does not post them: it
/// would be one card for every instance the group ever ran. Anything written within
/// <see cref="LiveFor"/> of the end is news and is posted where routes ask for it.
/// </para>
/// <para>
/// <strong>Facts are only ever added.</strong> An entry is written once per instance: the row carries
/// <see cref="VRChatInstance.EndRecordedAt"/>, and the fact log is asked as well, so a pass that stopped
/// between the two writes nothing twice.
/// </para>
/// </remarks>
public sealed class InstanceEndFacts
{
    /// <summary>How many ended instances one pass settles, so a group with years of history is caught up over a few passes.</summary>
    public const int PerPass = 200;

    /// <summary>
    /// How long after an instance ended an entry still counts as news. Past this it is catch-up:
    /// written for the record, not posted to Discord.
    /// </summary>
    public static readonly TimeSpan LiveFor = TimeSpan.FromHours(1);

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ILogger _log;

    public InstanceEndFacts(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>
    /// Settles the group's ended instances the audit log has been read past.
    /// </summary>
    /// <param name="now">From <c>IModbotClock</c>. Decides only whether an entry is news or catch-up.</param>
    /// <returns>How many entries were written.</returns>
    public async Task<int> SettleAsync(string groupId, Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(settings);

        // The audit log has not been read to its newest entry yet, ever: nothing is known about
        // who closed what, so nothing is written.
        if (settings.AuditLogReadToEndAt is not { } readAt)
            return 0;

        var readPast = readAt - InstanceCloseEntries.Leeway;

        var waiting = await _db.VRChatInstances
            .Where(i => i.GroupId == groupId
                        && i.ClosedAt != null && i.ClosedAt <= readPast
                        && i.EndRecordedAt == null)
            .OrderBy(i => i.ClosedAt)
            .Take(PerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (waiting.Count == 0)
            return 0;

        var byHand = await InstanceCloseEntries.ClosedByHandAsync(
                _db,
                waiting.Select(i => new InstanceEndRow(i.Id, i.WorldId, i.VRChatInstanceId, i.OpenedAt, i.ClosedAt)).ToList(),
                ct)
            .ConfigureAwait(false);

        var written = 0;

        foreach (var instance in waiting)
        {
            var closedAt = instance.ClosedAt!.Value;

            try
            {
                if (!byHand.Contains(instance.Id) && !await AlreadyWrittenAsync(instance, closedAt, ct).ConfigureAwait(false))
                {
                    var catchUp = now - closedAt > LiveFor;

                    await _partitions.EnsureForAsync(closedAt, ct).ConfigureAwait(false);
                    await _facts.WriteAsync(Build(groupId, instance, closedAt, catchUp), ct).ConfigureAwait(false);
                    written++;
                }

                instance.EndRecordedAt = now;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One instance that cannot be written must not hold back the ones behind it. It is
                // left unmarked and tried again next pass.
                _log.Warning(ex, "Could not write the entry for an instance that ended on its own; it will be tried again");
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (written > 0)
        {
            _log.Information(
                "Wrote {Written} entries for instances that ended on their own ({Checked} instances checked)",
                written, waiting.Count);
        }

        return written;
    }

    /// <summary>Whether the fact log already has this instance's entry: one at its end time, about its location.</summary>
    private async Task<bool> AlreadyWrittenAsync(VRChatInstance instance, DateTimeOffset closedAt, CancellationToken ct)
        => await _db.Events.AsNoTracking()
            .AnyAsync(e => e.Type == FactType.InstanceEndedOnItsOwn
                           && e.SubjectId == instance.Location
                           && e.OccurredAt >= instance.OpenedAt
                           && e.OccurredAt <= closedAt, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// The entry: about the instance's location like VRChat's own instance entries, at the time
    /// Modbot saw the instance leave the list, with nobody named -- a list cannot say who, and
    /// nobody did.
    /// </summary>
    private static FactRecord Build(string groupId, VRChatInstance instance, DateTimeOffset closedAt, bool catchUp)
    {
        var data = new JsonObject
        {
            ["source"] = "list-diff",
            ["groupId"] = groupId,
            ["openedAt"] = instance.OpenedAt.ToString("O", CultureInfo.InvariantCulture),
        };

        // How Modbot noticed: the list stopped carrying it, or nothing had been seen for a long time.
        if (instance.ClosedBy is { Length: > 0 } how)
            data["endedBy"] = how;

        if (instance.GroupAccessType is { Length: > 0 } access)
            data["groupAccessType"] = access;

        if (instance.Name is { Length: > 0 } name)
            data["instanceName"] = name;

        if (catchUp)
            data["catchUp"] = true;

        return new FactRecord
        {
            Type = FactType.InstanceEndedOnItsOwn,
            OccurredAt = closedAt,
            OccurredBefore = null,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = instance.Location,
            ActorPlatform = null,
            ActorId = null,
            WorldId = instance.WorldId,
            InstanceId = instance.VRChatInstanceId,
            Source = FactSource.SyncDiff,
            Data = data,
        };
    }
}
