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
public sealed record SavedClip(
    long Id,
    string Hash,
    long Bytes,
    string WorldId,
    string InstanceId,
    DateTimeOffset SavedAt,
    string SavedById,
    string? SavedBy);

/// <summary>
/// The clips moderators' companions said they saved: one by its id, and the ones a case file's
/// person may be in.
/// </summary>
/// <remarks>
/// <para>Read from <see cref="FactType.InstanceClipSaved"/> facts and nothing else. The server never
/// has the clip until a moderator attaches it; what it has is when and where it was saved, by whom,
/// and the file's fingerprint (clips design spec §16).</para>
/// </remarks>
public sealed class SavedClips
{
    /// <summary>At most this many clips are looked at for one case file.</summary>
    private const int MaxClips = 200;

    private readonly ModbotContext _db;

    public SavedClips(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>The clip with this fact id, or null when there is none or the fact is not a saved clip.</summary>
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
        ArgumentNullException.ThrowIfNull(caseFile);
        ArgumentNullException.ThrowIfNull(held);

        var anchor = caseFile.BannedAt ?? caseFile.CreatedAt;
        var from = anchor - ClipsNearAPerson.LookBack;
        var to = anchor + ClipsNearAPerson.LookAhead;

        var facts = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.InstanceClipSaved && e.OccurredAt >= from && e.OccurredAt <= to)
            .OrderBy(e => e.OccurredAt)
            .Take(MaxClips)
            .ToListAsync(ct);

        var clips = facts.Select(Read).OfType<SavedClip>().Where(c => !held.Contains(c.Hash)).ToList();
        if (clips.Count == 0)
            return [];

        var instances = clips.Select(c => c.InstanceId).Distinct(StringComparer.Ordinal).ToList();
        var types = ClipsNearAPerson.PresenceTypes.ToList();
        var earliest = from - TimeInInstance.LongestStay;

        var marks = await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && e.SubjectId == caseFile.UserId
                && types.Contains(e.Type)
                && e.InstanceId != null
                && instances.Contains(e.InstanceId)
                && e.OccurredAt >= earliest
                && e.OccurredAt <= to)
            .Select(e => new PersonMark(e.Type, e.WorldId, e.InstanceId!, e.OccurredAt))
            .ToListAsync(ct);

        var picked = ClipsNearAPerson.Pick(
                clips.Select(c => new ClipMark(c.Id, c.WorldId, c.InstanceId, c.SavedAt)),
                marks)
            .ToHashSet();

        var chosen = clips.Where(c => picked.Contains(c.Id)).ToList();
        var names = await WorldNamesAsync(chosen.Select(c => c.WorldId), ct);

        return [.. chosen.Select(c => new SavedClipView(
            c.Id, c.SavedAt, c.WorldId, c.InstanceId, c.SavedById, c.SavedBy, c.Bytes,
            names.GetValueOrDefault(c.WorldId)))];
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

    /// <summary>A fact as a clip, or null when it does not carry a usable fingerprint.</summary>
    private static SavedClip? Read(ModbotEvent fact)
    {
        if (fact.WorldId is not { Length: > 0 } worldId || fact.InstanceId is not { Length: > 0 } instanceId)
            return null;

        var data = AuditJson.Parse(fact.Data);
        if (AuditJson.Text(data, ClipKeys.Hash) is not { Length: > 0 } hash
            || Bytes(data?[ClipKeys.Bytes]) is not { } bytes)
        {
            return null;
        }

        return new SavedClip(
            fact.Id, hash, bytes, worldId, instanceId, fact.OccurredAt, fact.SubjectId,
            AuditJson.Text(data, "displayName"));
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
