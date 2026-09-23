using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Discord.ModerationLog;

/// <summary>One thing that changed, as the payload's <c>{old, new}</c> pair reads.</summary>
/// <param name="Name">The payload's own name for what changed, as it was written.</param>
/// <param name="Before">What it was, as text, or null when it was nothing.</param>
/// <param name="After">What it is now, as text, or null when it is nothing.</param>
public sealed record EventChange(string Name, string? Before, string? After);

/// <summary>
/// The parts of an event's payload a card is allowed to read.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Exactly the names <c>AuditLogEntryMapper.Lift</c> writes, and no
/// others</strong> (Discord event cards design §3). The mapper lifts a name only where a real
/// sample showed it, because a field read under a guessed name is one that two producers and a
/// query then depend on; reading one here under a guessed name would be the same mistake one step
/// further on.
/// </para>
/// <para>
/// VRChat's own payload, <c>auditData</c>, is deliberately not read. It is the record, not the
/// display: its shape is documented only as "dependent on the event type", and a card that reads
/// it is a card that can be wrong about what a value means.
/// </para>
/// </remarks>
/// <param name="RoleId">Carried, never printed: an id says nothing a moderator wanted (<c>CardLink</c>).</param>
/// <param name="RoleName">The role a grant, a removal or a change was about.</param>
/// <param name="GroupAccessType">How open an instance was made: VRChat's own word.</param>
/// <param name="Title">The event's own title: an announcement's, a post's, a calendar entry's.</param>
/// <param name="Message">An announcement's words.</param>
/// <param name="Text">A group post's words.</param>
/// <param name="AuthorId">Carried, never printed, for the same reason as <paramref name="RoleId"/>.</param>
/// <param name="Visibility">Who may see a group post.</param>
/// <param name="Kind">What kind of calendar entry it is. VRChat calls this field <c>type</c>.</param>
/// <param name="AccessType">Who may come to a calendar entry.</param>
/// <param name="Changed">
/// Every <c>{old, new}</c> pair the payload carried, which is the one shape several kinds of event
/// share and the one a card can draw without knowing the kind.
/// </param>
public sealed record EventDetails(
    string? RoleId = null,
    string? RoleName = null,
    string? GroupAccessType = null,
    string? Title = null,
    string? Message = null,
    string? Text = null,
    string? AuthorId = null,
    string? Visibility = null,
    string? Kind = null,
    string? AccessType = null,
    IReadOnlyList<EventChange>? Changed = null)
{
    /// <summary>An event whose payload carried none of this.</summary>
    public static EventDetails None { get; } = new();

    /// <summary>What changed, never null.</summary>
    public IReadOnlyList<EventChange> Changes => Changed ?? [];
}

/// <summary>
/// One moderation event with the names filled in: what the embed and the lookup reply show.
/// </summary>
/// <param name="SubjectName">From <c>vrchat_user</c> when Modbot has fetched that profile; else null.</param>
/// <param name="ActorName">
/// From <c>vrchat_user</c> when known, else the name VRChat put on the audit entry at the time.
/// </param>
/// <param name="Description">VRChat's own sentence for the entry, when the fact carries one.</param>
/// <param name="WorldId">
/// The fact's own world column, which the audit-log mapper fills for an instance kick, warn,
/// opening, closing or announcement. A column Modbot wrote, not a name guessed at in the payload.
/// </param>
/// <param name="InstanceId">The fact's own instance column, beside <paramref name="WorldId"/>.</param>
/// <param name="WorldName">From <c>vrchat_world</c> when Modbot has read that world; else null.</param>
/// <param name="Details">The payload, as far as a card may read it. Null means none was readable.</param>
public sealed record ModerationEventView(
    long Id,
    string Type,
    DateTimeOffset OccurredAt,
    string SubjectId,
    string? SubjectName,
    string? ActorId,
    string? ActorName,
    string? Description,
    string? WorldId = null,
    string? InstanceId = null,
    string? WorldName = null,
    EventDetails? Details = null)
{
    /// <summary>The payload as a card reads it, never null.</summary>
    public EventDetails What => Details ?? EventDetails.None;

    /// <param name="worlds">World names by world id, for the events that name an instance.</param>
    public static ModerationEventView From(
        ModbotEvent fact,
        IReadOnlyDictionary<string, string?> names,
        IReadOnlyDictionary<string, string?>? worlds = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(names);

        var (actorDisplayName, description, details) = ReadPayload(fact.Data);

        names.TryGetValue(fact.SubjectId, out var subjectName);

        string? actorName = null;
        if (fact.ActorId is not null)
            names.TryGetValue(fact.ActorId, out actorName);

        string? worldName = null;
        if (worlds is not null && fact.WorldId is not null)
            worlds.TryGetValue(fact.WorldId, out worldName);

        return new ModerationEventView(
            fact.Id,
            fact.Type,
            fact.OccurredAt,
            fact.SubjectId,
            subjectName,
            fact.ActorId,
            actorName ?? actorDisplayName,
            description,
            fact.WorldId,
            fact.InstanceId,
            worldName,
            details);
    }

    private static (string? ActorDisplayName, string? Description, EventDetails Details) ReadPayload(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return (null, null, EventDetails.None);

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, EventDetails.None);

            var details = new EventDetails(
                RoleId: Text(root, "roleId"),
                RoleName: Text(root, "roleName"),
                GroupAccessType: Text(root, "groupAccessType"),
                Title: Text(root, "title"),
                Message: Text(root, "message"),
                Text: Text(root, "text"),
                AuthorId: Text(root, "authorId"),
                Visibility: Text(root, "visibility"),
                Kind: Text(root, "type"),
                AccessType: Text(root, "accessType"),
                Changed: Changes(root));

            return (Text(root, "actorDisplayName"), Text(root, "description"), details);
        }
        catch (JsonException)
        {
            return (null, null, EventDetails.None);
        }
    }

    /// <summary>
    /// The <c>changed</c> object as a list of pairs, in the order the payload wrote them.
    /// </summary>
    /// <remarks>
    /// Only a member that is itself an object with an <c>old</c> and a <c>new</c> counts, which is
    /// what the mapper writes and what the profile sync writes under the same name. Anything else
    /// under <c>changed</c> is skipped rather than guessed at.
    /// </remarks>
    private static IReadOnlyList<EventChange>? Changes(JsonElement root)
    {
        if (!root.TryGetProperty("changed", out var changed) || changed.ValueKind != JsonValueKind.Object)
            return null;

        var pairs = new List<EventChange>();

        foreach (var member in changed.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!member.Value.TryGetProperty("old", out var before) || !member.Value.TryGetProperty("new", out var after))
                continue;

            pairs.Add(new EventChange(member.Name, Value(before), Value(after)));
        }

        return pairs.Count == 0 ? null : pairs;
    }

    /// <summary>
    /// One side of a pair as text: the string itself, nothing for a null, and the raw JSON for
    /// anything else -- a number, a flag, or the represented-group object a profile diff carries.
    /// </summary>
    private static string? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Display names for a set of ids: from <c>vrchat_user</c>, and for a Modbot account id the
/// account's username.
/// </summary>
public static class DisplayNames
{
    public static async Task<Dictionary<string, string?>> LoadAsync(
        ModbotContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var rows = await db.VRChatUsers.AsNoTracking()
            .Where(u => wanted.Contains(u.UserId))
            .Select(u => new { u.UserId, u.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var names = rows.ToDictionary(r => r.UserId, r => r.DisplayName, StringComparer.Ordinal);

        // Modbot's own actions name the account that did them by its id.
        var accountIds = wanted
            .Where(id => !names.ContainsKey(id))
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .ToArray();

        if (accountIds.Length > 0)
        {
            var accounts = await db.Users.AsNoTracking()
                .Where(u => accountIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var account in accounts)
                names[account.Id.ToString()] = account.Username;
        }

        return names;
    }
}

/// <summary>What each of a set of worlds is called, from <c>vrchat_world</c>, by world id.</summary>
/// <remarks>
/// A card names an instance by its world, because "The Black Cat #26093" is what a moderator
/// recognises and <c>wrld_4cf5…</c> is not. A world Modbot has never read has no name here, and the
/// card then leaves the instance off rather than printing the id (<c>CardLink</c>).
/// </remarks>
public static class WorldNames
{
    public static async Task<Dictionary<string, string?>> LoadAsync(
        ModbotContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var rows = await db.VRChatWorlds.AsNoTracking()
            .Where(w => wanted.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.WorldId, r => r.Name, StringComparer.Ordinal);
    }
}

/// <summary>
/// The picture to show for a set of people: whichever of their stored pictures
/// <see cref="ProfilePictures"/> picks, by VRChat user id.
/// </summary>
/// <remarks>
/// Its own read rather than a wider one on <see cref="DisplayNames"/>, because a pass that is only
/// counting facts should not be pulling picture addresses it will not use, and because a Modbot
/// account has no picture: only VRChat people appear here.
/// </remarks>
public static class PersonPictures
{
    public static async Task<Dictionary<string, string?>> LoadAsync(
        ModbotContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var rows = await db.VRChatUsers.AsNoTracking()
            .Where(u => wanted.Contains(u.UserId))
            .Select(u => new { u.UserId, u.ProfilePictureUrl, u.IconUrl, u.CurrentAvatarThumbnailImageUrl })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(
            r => r.UserId,
            r => ProfilePictures.Best(r.ProfilePictureUrl, r.IconUrl, r.CurrentAvatarThumbnailImageUrl),
            StringComparer.Ordinal);
    }
}
