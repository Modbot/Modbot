using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Live;

/// <summary>
/// Loads the facts <see cref="InstanceWatching"/> needs and runs it, for one instance or for several.
/// </summary>
/// <remarks>
/// Reads Modbot's own tables and nothing else. Nothing here calls VRChat, so the Live page, the
/// overlay and the Discord card can ask as often as they like without spending the group's budget.
/// </remarks>
public sealed class InstancePeopleReader
{
    /// <summary>
    /// How far before an instance's recorded opening its facts are read.
    /// </summary>
    /// <remarks>
    /// An instance's row opens when Modbot first sees it, and the group's list is only polled every ten
    /// seconds, so a moderator who walks in the moment an instance is created can report their arrival a
    /// little before the row exists. Two minutes covers that; a number VRChat hands out again that
    /// quickly is rare, and the instance rule already treats a return within ten minutes as the same instance.
    /// </remarks>
    public static readonly TimeSpan BeforeOpening = TimeSpan.FromMinutes(2);

    private static readonly string[] PresenceTypes =
    [
        FactType.InstanceJoined,
        FactType.InstancePresenceObserved,
        FactType.InstanceLeft,
        FactType.InstanceLogStopped,
    ];

    private readonly ModbotContext _db;

    public InstancePeopleReader(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Who is watching and who is in each of these instances, keyed on Modbot's instance id.</summary>
    public async Task<IReadOnlyDictionary<Guid, InstancePeople>> ForInstancesAsync(
        IReadOnlyCollection<VRChatInstance> instances,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instances);

        var result = new Dictionary<Guid, InstancePeople>();
        var numbered = instances.Where(r => r.VRChatInstanceId is { Length: > 0 }).ToList();

        foreach (var instance in instances.Except(numbered))
            result[instance.Id] = InstancePeople.NeverWatched;

        if (numbered.Count == 0)
            return result;

        var numbers = numbered.Select(r => r.VRChatInstanceId!).Distinct(StringComparer.Ordinal).ToList();
        var from = numbered.Min(r => r.OpenedAt) - BeforeOpening;

        var marks = await LoadAsync(
            _db.Events.Where(e => e.InstanceId != null && numbers.Contains(e.InstanceId) && e.OccurredAt >= from),
            ct).ConfigureAwait(false);

        var owners = await DeviceOwnersAsync(ct).ConfigureAwait(false);

        var perInstance = numbered.ToDictionary(
            r => r.Id,
            r =>
            {
                var key = new InstanceKey(r.WorldId, r.VRChatInstanceId!);
                var start = r.OpenedAt - BeforeOpening;
                return (Key: key, Start: r.OpenedAt - BeforeOpening, Marks: marks.Where(m => m.At >= start && key.Holds(m)).ToList());
            });

        var watchers = perInstance.Values
            .SelectMany(r => InstanceWatching.PossibleWatchers(r.Key, r.Marks, owners))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var elsewhere = await ElsewhereAsync(watchers, from, ct).ConfigureAwait(false);

        foreach (var instance in numbered)
        {
            var (key, start, inInstance) = perInstance[instance.Id];
            var theirs = InstanceWatching.PossibleWatchers(key, inInstance, owners).ToHashSet(StringComparer.Ordinal);

            var combined = inInstance.Concat(elsewhere.Where(m =>
                m.At >= start && !key.Holds(m) && theirs.Contains(m.SubjectId)));

            result[instance.Id] = InstanceWatching.Work(key, combined, owners, instance.ClosedAt);
        }

        return result;
    }

    /// <summary>
    /// Who is watching and who is in the instance with this instance number, from facts since
    /// <paramref name="from"/>. For callers that know the number but not Modbot's instance.
    /// </summary>
    public async Task<InstancePeople> ForNumberAsync(
        string instanceId,
        string? worldId,
        DateTimeOffset from,
        DateTimeOffset? closedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        var key = new InstanceKey(worldId, instanceId);

        var inInstance = await LoadAsync(
            _db.Events.Where(e => e.InstanceId == instanceId
                && (worldId == null || e.WorldId == worldId)
                && e.OccurredAt >= from),
            ct).ConfigureAwait(false);

        var owners = await DeviceOwnersAsync(ct).ConfigureAwait(false);
        var watchers = InstanceWatching.PossibleWatchers(key, inInstance, owners);
        var elsewhere = await ElsewhereAsync(watchers, from, ct).ConfigureAwait(false);

        return InstanceWatching.Work(key, inInstance.Concat(elsewhere.Where(m => !key.Holds(m))), owners, closedAt);
    }

    /// <summary>
    /// Facts placing these moderators anywhere, which is how "their presence turned up in a
    /// different instance" is noticed. Few people, a bounded stretch of time.
    /// </summary>
    private async Task<List<PresenceMark>> ElsewhereAsync(
        IReadOnlyCollection<string> watchers,
        DateTimeOffset from,
        CancellationToken ct)
    {
        if (watchers.Count == 0)
            return [];

        return await LoadAsync(
            _db.Events.Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && watchers.Contains(e.SubjectId)
                && e.OccurredAt >= from),
            ct).ConfigureAwait(false);
    }

    private static async Task<List<PresenceMark>> LoadAsync(IQueryable<ModbotEvent> query, CancellationToken ct)
    {
        var rows = await query
            .AsNoTracking()
            .Where(e => PresenceTypes.Contains(e.Type))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new { e.Type, e.SubjectId, e.OccurredAt, e.WorldId, e.InstanceId, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r =>
            {
                var (device, name) = ReadPayload(r.Data);
                return new PresenceMark(r.Type, r.SubjectId, r.OccurredAt, r.WorldId, r.InstanceId, device, name);
            })
            .ToList();
    }

    /// <summary>Every paired device, mapped to the VRChat account of the person it was issued to.</summary>
    /// <remarks>
    /// Revoked devices are included: a fact reported before the revocation is still something that
    /// moderator's client saw. A device whose owner has no VRChat link maps to nothing, so its
    /// reports can never start a watch -- there is no way to tell which subject is the moderator.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, string>> DeviceOwnersAsync(CancellationToken ct)
    {
        var pairs = await (
                from device in _db.CompanionDevices.AsNoTracking()
                join user in _db.Users.AsNoTracking() on device.IssuedToUserId equals user.Id
                where user.VRChatUserId != null
                select new { device.Id, user.VRChatUserId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return pairs.ToDictionary(p => p.Id, p => p.VRChatUserId!);
    }

    /// <summary>
    /// The reporting device and the display name out of a fact's payload. A payload that cannot be
    /// read yields neither, rather than taking a whole instance's answer down with it.
    /// </summary>
    private static (Guid? Device, string? Name) ReadPayload(string? data)
    {
        if (data is not { Length: > 2 })
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            Guid? device = root.TryGetProperty("deviceId", out var d)
                && d.ValueKind == JsonValueKind.String
                && Guid.TryParse(d.GetString(), out var parsed)
                ? parsed
                : null;

            var name = root.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            return (device, name);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
