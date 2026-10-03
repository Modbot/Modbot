using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
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
/// <param name="Modbot">
/// The names Modbot's own producers write -- the calendar's, the Discord member recorder's, and the
/// page saves' -- read only by the cards for those kinds of event. Null when the payload was not
/// readable.
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
    IReadOnlyList<EventChange>? Changed = null,
    ModbotDetails? Modbot = null)
{
    /// <summary>An event whose payload carried none of this.</summary>
    public static EventDetails None { get; } = new();

    /// <summary>What changed, never null.</summary>
    public IReadOnlyList<EventChange> Changes => Changed ?? [];

    /// <summary>Modbot's own names, never null.</summary>
    public ModbotDetails Own => Modbot ?? ModbotDetails.None;

    /// <summary>The reasons picked in Modbot, never null.</summary>
    public IReadOnlyList<string> Reasons => Own.ReasonLabels ?? [];
}

/// <summary>
/// The parts of a payload Modbot wrote itself that a card may read, each under the name its
/// producer writes.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="EventDetails"/>, whose names are the audit-log mapper's and no
/// others. These are the calendar's (<c>CalendarEventFields</c>, the publishers, the opener, the
/// world picker, the invites), the Discord member recorder's, and the names the page saves write
/// for lists, roles, giveaways and the like. Each name here is one a producer in this repository
/// writes, never a guess.
/// </para>
/// <para>
/// <see cref="Before"/> and <see cref="After"/> are the scalar members of the <c>before</c> and
/// <c>after</c> objects a change fact carries, as text, in the order they were written. A nested
/// value in them is skipped: no card draws one.
/// </para>
/// </remarks>
/// <param name="Reason">
/// The reason a Discord moderator gave in Discord's own audit log, as <c>DiscordEventRecorder</c>
/// writes it.
/// </param>
/// <param name="ReasonLabels">
/// The reasons a moderator picked for an action taken in Modbot, as
/// <c>ModerationActionService</c> writes them. The note they may have written beside them is
/// deliberately not read: a card goes to a channel with no audit-log gate on it.
/// </param>
public sealed record ModbotDetails(
    string? Name = null,
    string? DisplayName = null,
    string? ChannelId = null,
    string? ChannelName = null,
    string? FromChannelId = null,
    string? FromChannelName = null,
    string? Count = null,
    string? Until = null,
    string? Old = null,
    string? New = null,
    string? Place = null,
    string? Action = null,
    string? Error = null,
    string? Problem = null,
    string? Fix = null,
    string? Date = null,
    string? StartsAt = null,
    string? EndsAt = null,
    string? OccurrenceStartsAt = null,
    string? On = null,
    string? WorldId = null,
    string? WorldName = null,
    string? List = null,
    string? EventId = null,
    IReadOnlyList<KeyValuePair<string, string?>>? Before = null,
    IReadOnlyList<KeyValuePair<string, string?>>? After = null,
    string? Reason = null,
    IReadOnlyList<string>? ReasonLabels = null)
{
    public static ModbotDetails None { get; } = new();

    /// <summary>A name out of <c>after</c> or <c>before</c>, for a change fact that carries none of its own.</summary>
    public string? NameInChange(string key)
        => Pick(After, key) ?? Pick(Before, key);

    private static string? Pick(IReadOnlyList<KeyValuePair<string, string?>>? values, string key)
        => values?.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal)).Value is { } value
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}

/// <summary>
/// Who decided an action in Modbot and why, carried onto VRChat's own record of the same action.
/// </summary>
/// <remarks>
/// VRChat's audit log says "Modbot banned X" and nothing else: it cannot say which moderator pressed
/// the button or what they picked as the reason. Modbot's own fact for the press can
/// (<c>modbot.action.*</c>), and the two are one decision (<c>LinkedActions</c>), so the card for
/// VRChat's record carries what Modbot's says.
/// </remarks>
/// <param name="ById">The Modbot account that decided it.</param>
/// <param name="ByName">That account's name.</param>
/// <param name="Reasons">The reasons picked from the group's list.</param>
public sealed record EventDecision(string? ById, string? ByName, IReadOnlyList<string> Reasons);

/// <summary>A Discord account as a card shows it: the name the server shows, and the picture.</summary>
/// <param name="AvatarUrl">Discord's own address for the picture, which Discord can always load.</param>
public sealed record DiscordFace(string? Name, string? AvatarUrl);

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
/// <param name="SubjectPlatform">
/// Which system the subject's id belongs to. A Discord account is shown as a Discord mention when
/// its name is not known, never as a bare id; a VRChat person has no such thing.
/// </param>
/// <param name="ActorPlatform">Which system the actor's id belongs to, for the same reason.</param>
/// <param name="Worlds">
/// World names by id, for the worlds a payload names on its own (a calendar event's world, before
/// and after a change) rather than in the fact's world column.
/// </param>
/// <param name="Decision">Modbot's record of the same action, on VRChat's record of one Modbot made.</param>
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
    EventDetails? Details = null,
    FactPlatform SubjectPlatform = FactPlatform.VRChat,
    FactPlatform? ActorPlatform = null,
    IReadOnlyDictionary<string, string?>? Worlds = null,
    EventDecision? Decision = null)
{
    /// <summary>The payload as a card reads it, never null.</summary>
    public EventDetails What => Details ?? EventDetails.None;

    /// <summary>The event is about a Discord account, or the Discord server, rather than VRChat.</summary>
    public bool OnDiscord => SubjectPlatform == FactPlatform.Discord;

    /// <summary>A world's name by its id, when Modbot has read that world.</summary>
    public string? WorldNamed(string? worldId)
        => worldId is not null && Worlds is not null && Worlds.TryGetValue(worldId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    /// <param name="worlds">World names by world id, for the events that name an instance.</param>
    /// <param name="discordNames">
    /// Discord accounts' names by Discord user id, for the subjects and actors whose platform is
    /// Discord. Looked up only for those: ids are opaque text, and a VRChat and a Discord id are
    /// never compared.
    /// </param>
    /// <param name="decisions">Modbot's record of an action, by the id of VRChat's record of it.</param>
    public static ModerationEventView From(
        ModbotEvent fact,
        IReadOnlyDictionary<string, string?> names,
        IReadOnlyDictionary<string, string?>? worlds = null,
        IReadOnlyDictionary<string, string?>? discordNames = null,
        IReadOnlyDictionary<long, EventDecision>? decisions = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(names);

        var (actorDisplayName, description, details) = ReadPayload(fact.Data);

        var subjectName = NameOf(fact.SubjectPlatform, fact.SubjectId, names, discordNames);

        // A Discord fact names its member at the time it was written; Modbot's member list may be
        // older than that, or may never have listed them.
        if (fact.SubjectPlatform == FactPlatform.Discord && string.IsNullOrWhiteSpace(subjectName))
            subjectName = details.Own.DisplayName;

        string? actorName = null;
        if (fact.ActorId is not null)
            actorName = NameOf(fact.ActorPlatform ?? FactPlatform.VRChat, fact.ActorId, names, discordNames);

        string? worldName = null;
        if (worlds is not null && fact.WorldId is not null)
            worlds.TryGetValue(fact.WorldId, out worldName);

        return new ModerationEventView(
            fact.Id,
            fact.Type,
            fact.OccurredAt,
            fact.SubjectId,
            string.IsNullOrWhiteSpace(subjectName) ? null : subjectName,
            fact.ActorId,
            string.IsNullOrWhiteSpace(actorName) ? actorDisplayName : actorName,
            description,
            fact.WorldId,
            fact.InstanceId,
            worldName,
            details,
            fact.SubjectPlatform,
            fact.ActorPlatform,
            worlds,
            decisions?.GetValueOrDefault(fact.Id));
    }

    /// <summary>
    /// The worlds a fact's payload names on its own, so the poster can read their names with the
    /// rest: a calendar event's world, and its world before and after a change.
    /// </summary>
    public static IEnumerable<string> WorldsNamedIn(ModbotEvent fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var own = ReadPayload(fact.Data).Details.Own;

        var inChange = (own.Before ?? []).Concat(own.After ?? [])
            .Where(v => string.Equals(v.Key, "worldId", StringComparison.Ordinal))
            .Select(v => v.Value);

        return inChange.Prepend(own.WorldId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal);
    }

    private static string? NameOf(
        FactPlatform platform,
        string id,
        IReadOnlyDictionary<string, string?> names,
        IReadOnlyDictionary<string, string?>? discordNames)
    {
        if (platform == FactPlatform.Discord)
            return discordNames is not null && discordNames.TryGetValue(id, out var discord) ? discord : null;

        return names.TryGetValue(id, out var name) ? name : null;
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
                Changed: Changes(root),
                Modbot: Own(root));

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

    /// <summary>The names Modbot's own producers write (<see cref="ModbotDetails"/>).</summary>
    private static ModbotDetails Own(JsonElement root) => new(
        Name: Text(root, "name"),
        DisplayName: Text(root, "displayName"),
        ChannelId: Text(root, "channelId"),
        ChannelName: Text(root, "channelName"),
        FromChannelId: Text(root, "from"),
        FromChannelName: Text(root, "fromName"),
        Count: Scalar(root, "count"),
        Until: Text(root, "until"),
        Old: Text(root, "old"),
        New: Text(root, "new"),
        Place: Text(root, "place"),
        Action: Text(root, "action"),
        Error: Text(root, "error"),
        Problem: Text(root, "problem"),
        Fix: Text(root, "fix"),
        Date: Text(root, "date"),
        StartsAt: Text(root, "startsAt"),
        EndsAt: Text(root, "endsAt"),
        OccurrenceStartsAt: Text(root, "occurrenceStartsAt"),
        On: Text(root, "on"),
        WorldId: Text(root, "worldId"),
        WorldName: Text(root, "worldName"),
        List: Text(root, "list"),
        EventId: Text(root, "eventId"),
        Before: Members(root, "before"),
        After: Members(root, "after"),
        Reason: Text(root, "reason"),
        ReasonLabels: Texts(root, "reasonLabels"));

    /// <summary>The scalar members of one object in the payload, as text, in the order written.</summary>
    private static IReadOnlyList<KeyValuePair<string, string?>>? Members(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        var members = new List<KeyValuePair<string, string?>>();

        foreach (var member in value.EnumerateObject())
        {
            if (member.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                continue;

            members.Add(new KeyValuePair<string, string?>(member.Name, Value(member.Value)));
        }

        return members;
    }

    /// <summary>A string, a number or a flag as text.</summary>
    private static string? Scalar(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? Value(value)
            : null;

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

    /// <summary>A list of words, skipping anything in it that is not one.</summary>
    private static IReadOnlyList<string>? Texts(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;

        var words = value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            .Select(v => v.GetString()!.Trim())
            .ToList();

        return words.Count == 0 ? null : words;
    }
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

/// <summary>
/// What each of a set of Discord accounts is called in the server, from Modbot's member list, by
/// Discord user id.
/// </summary>
/// <remarks>
/// The list is as fresh as the last time Modbot read the server's members, so a name here can be
/// missing; a card then shows the person as a Discord mention, which Discord draws with their name.
/// </remarks>
public static class DiscordNames
{
    public static async Task<Dictionary<string, string?>> LoadAsync(
        ModbotContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var rows = await db.DiscordMembers.AsNoTracking()
            .Where(m => wanted.Contains(m.UserId) && m.DisplayName != "")
            .Select(m => new { m.UserId, m.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var names = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var row in rows)
            names.TryAdd(row.UserId, row.DisplayName);

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

/// <summary>
/// What a set of Discord accounts are called and look like, by Discord user id: from the server's
/// member list, and from its ban list for somebody who is no longer a member.
/// </summary>
/// <remarks>
/// <para>
/// A card about a Discord account used to be headed by the account's id and linked to a VRChat
/// profile that could not exist: the names were only ever looked for among VRChat people. A Discord
/// id is looked for here instead.
/// </para>
/// <para>
/// The picture is Discord's own address. Discord loads its own pictures, so unlike a VRChat picture
/// it needs no upload, and the operator's switch for fetching VRChat pictures has nothing to say
/// about it.
/// </para>
/// </remarks>
public static class DiscordPeople
{
    public static async Task<Dictionary<string, DiscordFace>> LoadAsync(
        ModbotContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, DiscordFace>(StringComparer.Ordinal);

        // Newest row first, so a person in more than one server Modbot has watched is named the way
        // they were seen last.
        var members = await db.DiscordMembers.AsNoTracking()
            .Where(m => wanted.Contains(m.UserId))
            .OrderByDescending(m => m.UpdatedAt)
            .Select(m => new { m.UserId, m.DisplayName, m.AvatarUrl })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var faces = new Dictionary<string, DiscordFace>(StringComparer.Ordinal);

        foreach (var member in members)
            faces.TryAdd(member.UserId, new DiscordFace(Blank(member.DisplayName), Blank(member.AvatarUrl)));

        var missing = wanted.Where(id => !faces.ContainsKey(id)).ToArray();
        if (missing.Length == 0)
            return faces;

        var bans = await db.DiscordBans.AsNoTracking()
            .Where(b => missing.Contains(b.UserId))
            .OrderByDescending(b => b.UpdatedAt)
            .Select(b => new { b.UserId, b.DisplayName, b.Username, b.AvatarUrl })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var ban in bans)
            faces.TryAdd(ban.UserId, new DiscordFace(Blank(ban.DisplayName) ?? Blank(ban.Username), Blank(ban.AvatarUrl)));

        return faces;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// For VRChat's record of a ban, kick or unban that Modbot made, Modbot's own record of the same
/// press: who decided it and why (<see cref="EventDecision"/>).
/// </summary>
/// <remarks>
/// Paired the way the counts pair them (<see cref="LinkedActions"/>): the same person, within
/// seconds, the actors not disagreeing. Read only for the facts that can have one, so a pass of
/// joins and role changes reads nothing more.
/// </remarks>
public static class ModbotDecisions
{
    public static async Task<Dictionary<long, EventDecision>> LoadAsync(
        ModbotContext db, IReadOnlyCollection<ModbotEvent> facts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);

        var decisions = new Dictionary<long, EventDecision>();

        var main = facts
            .Where(f => f.SubjectPlatform == FactPlatform.VRChat && ActionsFor(f.Type).Length > 0)
            .ToList();

        if (main.Count == 0)
            return decisions;

        var types = main.SelectMany(f => ActionsFor(f.Type)).Distinct(StringComparer.Ordinal).ToArray();
        var subjects = main.Select(f => f.SubjectId).Distinct(StringComparer.Ordinal).ToArray();
        var from = main.Min(f => f.OccurredAt) - LinkedActions.Window;
        var to = main.Max(f => f.OccurredAt) + LinkedActions.Window;

        var actions = await db.Events.AsNoTracking()
            .Where(e => types.Contains(e.Type)
                        && e.SubjectPlatform == FactPlatform.VRChat
                        && subjects.Contains(e.SubjectId)
                        && e.OccurredAt >= from
                        && e.OccurredAt <= to)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (actions.Count == 0)
            return decisions;

        var names = await DisplayNames.LoadAsync(db, actions.Select(a => a.ActorId), ct).ConfigureAwait(false);

        foreach (var fact in main)
        {
            var wanted = ActionsFor(fact.Type);

            var action = actions
                .Where(a => wanted.Contains(a.Type, StringComparer.Ordinal) && LinkedActions.CouldBeOneDecision(fact, a))
                .OrderBy(a => (a.OccurredAt - fact.OccurredAt).Duration())
                .FirstOrDefault();

            if (action is null)
                continue;

            var view = ModerationEventView.From(action, names);

            decisions[fact.Id] = new EventDecision(
                action.ActorId,
                view.ActorName,
                view.What.Reasons);
        }

        return decisions;
    }

    /// <summary>Modbot's action types that can be the other half of a fact of this type.</summary>
    private static string[] ActionsFor(string type) => type switch
    {
        FactType.MemberBanned => [FactType.ActionBan],
        FactType.MemberKicked => [FactType.ActionKick],
        FactType.MemberUnbanned => [FactType.ActionUnban],
        _ => [],
    };
}
