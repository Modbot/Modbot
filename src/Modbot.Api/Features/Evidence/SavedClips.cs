using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Api.Features.Evidence;

/// <summary>A clip a moderator's companion said it saved, read back from its fact.</summary>
/// <param name="Id">The fact's id.</param>
/// <param name="Hash">The file's SHA-256, as the companion worked it out when the clip was saved.</param>
/// <param name="Bytes">The file's size.</param>
/// <param name="SavedById">The moderator's VRChat id: the fact's subject, checked against the device's owner when it arrived.</param>
/// <param name="SavedByUserId">The Modbot account the reporting device was paired to.</param>
/// <param name="SavedBy">That account's username when the clip was reported.</param>
/// <param name="DeviceId">The device that reported it.</param>
public sealed record SavedClip(
    long Id,
    string Hash,
    long Bytes,
    string WorldId,
    string InstanceId,
    DateTimeOffset SavedAt,
    string SavedById,
    Guid SavedByUserId,
    string SavedBy,
    Guid? DeviceId);

/// <summary>
/// The clips moderators' companions said they saved: one by its id, and the ones a case file's
/// person may be in.
/// </summary>
/// <remarks>
/// <para>Read from <see cref="FactType.InstanceClipSaved"/> facts and nothing else. The server never
/// has the clip until a moderator attaches it; what it has is when and where it was saved, by whose
/// device, and the file's fingerprint (clips design spec §16).</para>
/// <para>A clip is credited to the account its device was paired to, written onto the fact by the
/// server when it arrived, never to a name the event carried. A fact without that is not offered.</para>
/// </remarks>
public sealed class SavedClips
{
    /// <summary>At most this many clips are looked at for one case file, the newest kept.</summary>
    private const int MaxClips = 200;

    private readonly ModbotContext _db;

    public SavedClips(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>The clip with this fact id, or null when there is none or the fact is not a usable saved clip.</summary>
    public async Task<SavedClip?> FindAsync(long id, CancellationToken ct)
    {
        var fact = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.Type == FactType.InstanceClipSaved, ct);

        return fact is null ? null : Read(fact);
    }

    /// <summary>
    /// The clips the person on this case file may be in, that it does not already hold, oldest first.
    /// </summary>
    /// <param name="caseFile">The case file.</param>
    /// <param name="held">The hashes the case file holds now: a clip already on it is not offered again.</param>
    public async Task<IReadOnlyList<SavedClipView>> NearAsync(
        CaseFile caseFile, IReadOnlySet<string> held, CancellationToken ct)
    {
        var chosen = await OfferedAsync(caseFile, held, ct);
        if (chosen.Count == 0)
            return [];

        var names = await WorldNamesAsync(chosen.Select(c => c.WorldId), ct);

        return [.. chosen.Select(c => new SavedClipView(
            c.Id, c.SavedAt, c.WorldId, c.InstanceId, c.SavedById, c.SavedBy, c.Bytes,
            names.GetValueOrDefault(c.WorldId)))];
    }

    /// <summary>
    /// Whether this clip is one the case file offers: saved where the person was, near the ban.
    /// A clip the case file already holds still counts, so putting the same clip on twice is
    /// answered the same way both times. Anything else is not told apart from no clip at all, so
    /// a caller cannot probe clip ids the case file never showed.
    /// </summary>
    public async Task<bool> OffersAsync(CaseFile caseFile, long clipId, CancellationToken ct)
    {
        var offered = await OfferedAsync(caseFile, new HashSet<string>(StringComparer.Ordinal), ct);
        return offered.Any(c => c.Id == clipId);
    }

    private async Task<IReadOnlyList<SavedClip>> OfferedAsync(
        CaseFile caseFile, IReadOnlySet<string> held, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        ArgumentNullException.ThrowIfNull(held);

        var anchor = caseFile.BannedAt ?? caseFile.CreatedAt;
        var from = anchor - ClipsNearAPerson.LookBack;
        var to = anchor + ClipsNearAPerson.LookAhead;
        var types = ClipsNearAPerson.PresenceTypes.ToList();

        // The person first: only the instances they were in can hold a clip of them, so the clips
        // are looked for there and nowhere else. A busy group saving many clips elsewhere in the
        // week cannot push this person's clip out of the limit below.
        var marks = await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && e.SubjectId == caseFile.UserId
                && types.Contains(e.Type)
                && e.InstanceId != null
                && e.OccurredAt >= from - TimeInInstance.LongestStay
                && e.OccurredAt <= to)
            .Select(e => new PersonMark(e.Type, e.WorldId, e.InstanceId!, e.OccurredAt))
            .ToListAsync(ct);

        if (marks.Count == 0)
            return [];

        var instances = marks.Select(m => m.InstanceId).Distinct(StringComparer.Ordinal).ToList();

        // Newest first under the limit, so the ones nearest the ban are the ones kept.
        var facts = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.InstanceClipSaved
                && e.InstanceId != null
                && instances.Contains(e.InstanceId)
                && e.OccurredAt >= from
                && e.OccurredAt <= to)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(MaxClips)
            .ToListAsync(ct);

        var clips = facts.Select(Read).OfType<SavedClip>().Where(c => !held.Contains(c.Hash)).ToList();
        if (clips.Count == 0)
            return [];

        var picked = ClipsNearAPerson.Pick(
                clips.Select(c => new ClipMark(c.Id, c.WorldId, c.InstanceId, c.SavedAt)),
                marks)
            .ToHashSet();

        return [.. clips.Where(c => picked.Contains(c.Id)).OrderBy(c => c.SavedAt).ThenBy(c => c.Id)];
    }

    /// <summary>The names Modbot has read for these worlds. A world it has not read is left out.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WorldNamesAsync(IEnumerable<string?> worldIds, CancellationToken ct)
    {
        var ids = worldIds.OfType<string>().Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var rows = await _db.VRChatWorlds.AsNoTracking()
            .Where(w => ids.Contains(w.WorldId) && w.Name != null)
            .Select(w => new { w.WorldId, w.Name })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.WorldId, r => r.Name!, StringComparer.Ordinal);
    }

    /// <summary>
    /// A fact as a clip, or null when it does not carry a usable fingerprint or was not credited to
    /// a device's owner when it arrived.
    /// </summary>
    private static SavedClip? Read(ModbotEvent fact)
    {
        if (fact.WorldId is not { Length: > 0 } worldId || fact.InstanceId is not { Length: > 0 } instanceId)
            return null;

        var data = AuditJson.Parse(fact.Data);
        if (AuditJson.Text(data, ClipKeys.Hash) is not { Length: > 0 } hash
            || Bytes(data?[ClipKeys.Bytes]) is not { } bytes
            || !Guid.TryParse(AuditJson.Text(data, ClipKeys.SavedByUserId), out var ownerId)
            || AuditJson.Text(data, ClipKeys.SavedByUsername) is not { Length: > 0 } owner)
        {
            return null;
        }

        return new SavedClip(
            fact.Id, hash, bytes, worldId, instanceId, fact.OccurredAt, fact.SubjectId,
            ownerId, owner, ClientReport.DeviceIdOf(fact.Data));
    }

    private static long? Bytes(JsonNode? node)
    {
        try
        {
            return node switch
            {
                JsonValue value when value.TryGetValue<long>(out var number) => number,
                JsonValue value when value.TryGetValue<string>(out var text)
                    && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
