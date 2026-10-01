using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Live;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.Session;
using Serilog;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>What one pass of the inviter did.</summary>
/// <param name="Queued">People put on a queue this pass, skipped ones included.</param>
/// <param name="Sent">VRChat invites VRChat accepted: none or one.</param>
/// <param name="Stopped">Rows stopped because their instance closed or their time ended.</param>
public sealed record CalendarInviterResult(int Queued, int Sent, int Stopped);

/// <summary>
/// Invites an event's host, staff and list to the instance Modbot opened for it, one VRChat invite
/// every thirty seconds (calendar auto-invite design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The queue is written once per occurrence</strong>, the first pass after the instance
/// opened, in order: host, staff, the list's people as the list holds them at that moment. One row
/// per person, so somebody who is host, staff and on the list is invited once. Everything is in
/// <c>calendar_invite</c>, so a restart carries on where it stopped.
/// </para>
/// <para>
/// <strong>VRChat only lets an account invite its friends.</strong> Modbot does not read the
/// friends list (that would be another endpoint whose limit nobody has answered); it remembers
/// what it learns in <c>vrchat_friend</c>: an accepted invite means a friend, a 403 means not, and
/// the friends ids that come with a sign-in add friends. Somebody marked not a friend goes straight
/// to the calendar's Discord loop for a direct message, without spending a VRChat turn, and is
/// tried on VRChat again after <see cref="AskAgainAfter"/>. Somebody unknown gets one VRChat try.
/// </para>
/// <para>
/// <strong>One invite every thirty seconds, never retried.</strong> Kept by the
/// <c>invites.send</c> bucket and again by the last send time on the rows, which a crash cannot hand
/// back. A row is marked sending before the call; a crash between the two leaves it there, counted
/// as sent, never sent again. A 429 cold-stops the class (foundation §4.3.1) and that person falls
/// to Discord. Only a request the gate never sent leaves the row waiting.
/// </para>
/// <para>
/// Nothing is logged about a person: Modbot's own log can leave the server, and an event id says
/// enough to find the rows.
/// </para>
/// </remarks>
public sealed class CalendarInviter
{
    /// <summary>The shortest gap between two VRChat invites, anywhere in the deployment.</summary>
    public static readonly TimeSpan NoFasterThan = TimeSpan.FromSeconds(30);

    /// <summary>The longest one occurrence may last; openings older than this cannot still be running.</summary>
    private static readonly TimeSpan LongestOccurrence = TimeSpan.FromDays(7);

    /// <summary>How many rows one pass may skip or stop before it gives the loop back.</summary>
    private const int MaxLooksPerPass = 50;

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly GiveawayRuleChecker _checker;
    private readonly CalendarInvites _invites;
    private readonly SignInFriends? _signInFriends;
    private readonly ILogger _log;

    /// <summary>
    /// How long a person marked not a friend goes straight to Discord before VRChat is tried once
    /// more, in case they added the group's account since.
    /// </summary>
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(7);

    public CalendarInviter(
        IVRChatGate gate,
        ModbotContext db,
        IModbotClock clock,
        GiveawayRuleChecker checker,
        CalendarInvites invites,
        SignInFriends? signInFriends = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(invites);

        _gate = gate;
        _db = db;
        _clock = clock;
        _checker = checker;
        _invites = invites;
        _signInFriends = signInFriends;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<CalendarInviterResult> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        if (_signInFriends?.Take() is { } seen)
            await RememberSignInFriendsAsync(seen, ct).ConfigureAwait(false);

        var queued = await QueueAsync(now, ct).ConfigureAwait(false);

        // Stopping writes straight to the table; nothing the queue left tracked may answer for it.
        _db.ChangeTracker.Clear();

        var stopped = await _invites.StopAsync(now, ct).ConfigureAwait(false);
        var sent = await SendNextAsync(now, ct).ConfigureAwait(false);

        return new CalendarInviterResult(queued, sent, stopped);
    }

    // ── The queue ────────────────────────────────────────────────────────────────────────

    private async Task<int> QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var since = now - LongestOccurrence;

        var openings = await _db.CalendarOpenings
            .Where(o => o.Location != null && o.InvitesQueuedAt == null && o.OccurrenceStartsAt > since)
            .ToListAsync(ct).ConfigureAwait(false);

        var queued = 0;

        foreach (var opening in openings)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            // An event that invites nobody is asked again next pass, so ticking a host while the
            // instance is still open still invites them.
            if (calendarEvent is null || !calendarEvent.InvitesAnybody)
                continue;

            if (await _invites.WhyStoppedAsync(opening.EventId, opening.OccurrenceStartsAt, now, ct).ConfigureAwait(false) is not null)
                continue;

            var rows = await RowsAsync(calendarEvent, opening, now, ct).ConfigureAwait(false);

            _db.CalendarInvites.AddRange(rows);
            opening.InvitesQueuedAt = now;

            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Another process wrote this occurrence's queue first. It owns it.
                foreach (var row in rows)
                    _db.Entry(row).State = EntityState.Detached;

                _db.Entry(opening).State = EntityState.Detached;
                continue;
            }

            queued += rows.Count;
            _log.Information(
                "Queued {Count} invites for the event {EventId}", rows.Count, calendarEvent.Id);
        }

        return queued;
    }

    /// <summary>Host, then staff, then the list's people, once each, with who is left out marked.</summary>
    private async Task<List<CalendarInvite>> RowsAsync(
        CalendarEvent calendarEvent, CalendarOpening opening, DateTimeOffset now, CancellationToken ct)
    {
        var people = new List<(string Role, string? VRChat, string? Discord, string Key)>();

        await AddStaffAsync(calendarEvent, people, now, ct).ConfigureAwait(false);
        await AddListAsync(calendarEvent, people, ct).ConfigureAwait(false);

        // One row per person: the first time they turn up is the role they are invited as.
        var seenVRChat = new HashSet<string>(StringComparer.Ordinal);
        var seenDiscord = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<(string Role, string? VRChat, string? Discord, string Key)>();

        foreach (var person in people)
        {
            if ((person.VRChat is { } v && seenVRChat.Contains(v)) || (person.Discord is { } d && seenDiscord.Contains(d)))
                continue;

            if (person.VRChat is { } vrchat)
                seenVRChat.Add(vrchat);

            if (person.Discord is { } discord)
                seenDiscord.Add(discord);

            unique.Add(person);
        }

        var (bannedVRChat, bannedDiscord) = await BannedAsync(
            [.. unique.Where(p => p.VRChat != null).Select(p => p.VRChat!)],
            [.. unique.Where(p => p.Discord != null).Select(p => p.Discord!)],
            ct).ConfigureAwait(false);

        var rows = new List<CalendarInvite>(unique.Count);

        for (var position = 0; position < unique.Count; position++)
        {
            var (role, vrchat, discord, key) = unique[position];

            var (state, problem) =
                vrchat is not null && bannedVRChat.Contains(vrchat) ? (CalendarInviteStates.Skipped, CalendarInvites.BannedFromGroup)
                : discord is not null && bannedDiscord.Contains(discord) ? (CalendarInviteStates.Skipped, CalendarInvites.BannedFromServer)
                : vrchat is not null ? (CalendarInviteStates.Waiting, (string?)null)
                : discord is not null ? (CalendarInviteStates.ToMessage, (string?)null)
                : (CalendarInviteStates.NoWay, CalendarInvites.NoAccount);

            rows.Add(new CalendarInvite
            {
                EventId = opening.EventId,
                OccurrenceStartsAt = opening.OccurrenceStartsAt,
                Position = position,
                Role = role,
                PersonKey = key,
                VRChatUserId = vrchat,
                DiscordUserId = discord,
                State = state,
                Problem = problem,
                QueuedAt = now,
                UpdatedAt = now,
            });
        }

        return rows;
    }

    /// <summary>The host and the staff, reached through the accounts they linked.</summary>
    private async Task AddStaffAsync(
        CalendarEvent calendarEvent,
        List<(string Role, string? VRChat, string? Discord, string Key)> people,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var order = new List<Guid>();

        if (calendarEvent.InviteHostUserId is { } host)
            order.Add(host);

        order.AddRange(calendarEvent.InviteStaffUserIds.Where(id => !order.Contains(id)));

        if (order.Count == 0)
            return;

        var accounts = await _db.Users.AsNoTracking()
            .Where(u => order.Contains(u.Id) && u.DeletedAt == null && !u.IsDisabled)
            .ToListAsync(ct).ConfigureAwait(false);

        var discordIds = await StaffDiscord.CountedIdsAsync(
            _db, accounts.Select(u => (u.Id, u.DiscordUserId, u.DiscordVerifiedAt)), now, ct).ConfigureAwait(false);

        // A staff member who proved no Discord account on Modbot may still have linked their
        // VRChat account to one in the server.
        var vrchatIds = accounts.Where(u => u.IsVRChatLinked).Select(u => u.VRChatUserId!).ToList();
        var linked = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => vrchatIds.Contains(l.VRChatUserId) && l.UnlinkedAt == null)
            .ToDictionaryAsync(l => l.VRChatUserId, l => l.DiscordUserId, StringComparer.Ordinal, ct).ConfigureAwait(false);

        foreach (var id in order)
        {
            if (accounts.FirstOrDefault(u => u.Id == id) is not { } account)
                continue;

            var vrchat = account.IsVRChatLinked ? account.VRChatUserId : null;
            var discord = discordIds.GetValueOrDefault(id)
                ?? (vrchat is not null ? linked.GetValueOrDefault(vrchat) : null);

            var key = vrchat is not null || discord is not null
                ? CalendarInvite.KeyFor(vrchat, discord)
                : $"staff:{id}";

            people.Add((id == calendarEvent.InviteHostUserId ? CalendarInviteRoles.Host : CalendarInviteRoles.Staff, vrchat, discord, key));
        }
    }

    /// <summary>The list's people as it holds them now, by the same checker as the Lists page.</summary>
    private async Task AddListAsync(
        CalendarEvent calendarEvent,
        List<(string Role, string? VRChat, string? Discord, string Key)> people,
        CancellationToken ct)
    {
        if (calendarEvent.InviteListId is not { } listId)
            return;

        var list = await _db.SavedLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct).ConfigureAwait(false);

        if (list is null)
        {
            _log.Warning("The invite list for the event {EventId} does not exist any more", calendarEvent.Id);
            return;
        }

        var answer = await _checker.PeopleAsync(GiveawayRules.ReadStored(list.Rules), ct).ConfigureAwait(false);

        if (answer.Unanswerable is { } why)
        {
            _log.Warning("Could not work out the invite list for the event {EventId}: {Why}", calendarEvent.Id, why);
            return;
        }

        foreach (var person in answer.People)
        {
            var vrchat = person.Person.VRChatUserId is { Length: > 0 } v ? v : null;
            var discord = person.Person.DiscordUserId is { Length: > 0 } d ? d : null;

            if (vrchat is null && discord is null)
                continue;

            people.Add((CalendarInviteRoles.List, vrchat, discord, CalendarInvite.KeyFor(vrchat, discord)));
        }
    }

    /// <summary>Who of these Modbot knows is banned now, from the group and from the Discord server.</summary>
    private async Task<(HashSet<string> VRChat, HashSet<string> Discord)> BannedAsync(
        List<string> vrchatIds, List<string> discordIds, CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var groupId = settings.ManagedGroupId;
        var guildId = settings.DiscordGuildId?.Trim();

        var vrchat = vrchatIds.Count == 0 || groupId is not { Length: > 0 }
            ? []
            : await _db.GroupBans.AsNoTracking()
                .Where(b => b.GroupId == groupId && b.LiftedAt == null && vrchatIds.Contains(b.UserId))
                .Select(b => b.UserId)
                .ToListAsync(ct).ConfigureAwait(false);

        var discord = discordIds.Count == 0 || guildId is not { Length: > 0 }
            ? []
            : await _db.DiscordBans.AsNoTracking()
                .Where(b => b.GuildId == guildId && b.LiftedAt == null && discordIds.Contains(b.UserId))
                .Select(b => b.UserId)
                .ToListAsync(ct).ConfigureAwait(false);

        return ([.. vrchat], [.. discord]);
    }

    // ── Sending ──────────────────────────────────────────────────────────────────────────

    /// <returns>1 when VRChat accepted an invite, else 0.</returns>
    private async Task<int> SendNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Kept here as well as by the bucket: the bucket's count reaches the database at most every
        // thirty seconds, and a crash could hand a turn back. This time is written before each send.
        var last = await _db.CalendarInvites.MaxAsync(i => i.TriedAt, ct).ConfigureAwait(false);
        var turnTaken = last is { } then && now - then < NoFasterThan;

        for (var looked = 0; looked < MaxLooksPerPass; looked++)
        {
            var row = await _db.CalendarInvites
                .Where(i => i.State == CalendarInviteStates.Waiting)
                .OrderBy(i => i.QueuedAt)
                .ThenBy(i => i.EventId)
                .ThenBy(i => i.Position)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (row is null)
                return 0;

            var opening = await _db.CalendarOpenings.AsNoTracking()
                .FirstOrDefaultAsync(o => o.EventId == row.EventId && o.OccurrenceStartsAt == row.OccurrenceStartsAt, ct)
                .ConfigureAwait(false);

            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == row.EventId, ct).ConfigureAwait(false);

            var why = await _invites.WhyStoppedAsync(row.EventId, row.OccurrenceStartsAt, now, ct).ConfigureAwait(false);

            if (why is not null || opening?.Location is null || calendarEvent is null)
            {
                Mark(row, CalendarInviteStates.Stopped, why ?? CalendarInvites.InstanceClosed, now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            var instance = opening.InstanceId is { } instanceId
                ? await _db.VRChatInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, ct).ConfigureAwait(false)
                : null;

            if (await LeftOutAsync(row, instance, ct).ConfigureAwait(false) is { } skipped)
            {
                Mark(row, CalendarInviteStates.Skipped, skipped, now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            // Known not to be a friend, lately: VRChat would only refuse. Straight to Discord, and no
            // VRChat turn is spent on it.
            var friend = await _db.VRChatFriends.AsNoTracking()
                .FirstOrDefaultAsync(f => f.UserId == row.VRChatUserId, ct).ConfigureAwait(false);

            if (friend is { IsFriend: false } && now - friend.CheckedAt < AskAgainAfter)
            {
                await NotFriendsAsync(row, calendarEvent, instance, now, ct).ConfigureAwait(false);
                continue;
            }

            // A friend, somebody Modbot has not learned about yet, or a "not a friend" old enough to
            // ask again: this one needs a VRChat turn, and the order waits for it.
            if (turnTaken)
                return 0;

            return await SendAsync(row, opening, calendarEvent, instance, now, ct).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// A person VRChat will not take an invite for: to Discord when Modbot knows their account
    /// there, else nobody can reach them.
    /// </summary>
    private async Task NotFriendsAsync(
        CalendarInvite row, CalendarEvent calendarEvent, VRChatInstance? instance, DateTimeOffset now, CancellationToken ct)
    {
        if (row.DiscordUserId is { Length: > 0 })
        {
            // The calendar's Discord loop takes it from here.
            Mark(row, CalendarInviteStates.ToMessage, CalendarInvites.NotFriends, now);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        Mark(row, CalendarInviteStates.NoWay, CalendarInvites.NotFriends, now);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _invites.RecordAsync(
            row, calendarEvent.Title, sent: false, via: "vrchat", now,
            instance?.VRChatInstanceId, instance?.WorldId ?? calendarEvent.WorldId, ct).ConfigureAwait(false);
    }

    // ── Who is a friend ──────────────────────────────────────────────────────────────────

    /// <summary>Writes down what VRChat's answer to an invite said about the person.</summary>
    private async Task LearnAsync(string userId, bool isFriend, string from, DateTimeOffset now, CancellationToken ct)
    {
        var row = await _db.VRChatFriends.FirstOrDefaultAsync(f => f.UserId == userId, ct).ConfigureAwait(false);

        if (row is null)
        {
            row = new VRChatFriend { UserId = userId };
            _db.VRChatFriends.Add(row);
        }

        row.IsFriend = isFriend;
        row.CheckedAt = now;
        row.LearnedFrom = from;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The friends ids that came with the last sign-in. Only adds: the list may be incomplete, so
    /// somebody missing from it is not marked as not a friend.
    /// </summary>
    private async Task RememberSignInFriendsAsync(SignInFriendsSeen seen, CancellationToken ct)
    {
        if (seen.Ids.Count == 0)
            return;

        var known = await _db.VRChatFriends
            .Where(f => seen.Ids.Contains(f.UserId))
            .ToDictionaryAsync(f => f.UserId, StringComparer.Ordinal, ct).ConfigureAwait(false);

        foreach (var id in seen.Ids)
        {
            if (!known.TryGetValue(id, out var row))
            {
                row = new VRChatFriend { UserId = id };
                _db.VRChatFriends.Add(row);
            }

            row.IsFriend = true;
            row.CheckedAt = seen.At;
            row.LearnedFrom = VRChatFriendSources.SignIn;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _db.ChangeTracker.Clear();

        _log.Information("Noted {Count} friends of Modbot's VRChat account from its sign-in", seen.Ids.Count);
    }

    /// <summary>Why this person is left out now, checked again just before the send: banned since, or already there.</summary>
    private async Task<string?> LeftOutAsync(CalendarInvite row, VRChatInstance? instance, CancellationToken ct)
    {
        var (bannedVRChat, bannedDiscord) = await BannedAsync(
            row.VRChatUserId is { } v ? [v] : [],
            row.DiscordUserId is { } d ? [d] : [],
            ct).ConfigureAwait(false);

        if (bannedVRChat.Count > 0)
            return CalendarInvites.BannedFromGroup;

        if (bannedDiscord.Count > 0)
            return CalendarInvites.BannedFromServer;

        // Known only while a moderator's client is in the instance to say so. Unknown is not "absent":
        // nobody watching means the invite goes out.
        if (instance is not null && row.VRChatUserId is { } vrchat)
        {
            var people = await new InstancePeopleReader(_db).ForInstancesAsync([instance], ct).ConfigureAwait(false);

            if (people.TryGetValue(instance.Id, out var here)
                && here.Here.Any(p => string.Equals(p.UserId, vrchat, StringComparison.Ordinal)))
            {
                return CalendarInvites.AlreadyThere;
            }
        }

        return null;
    }

    private async Task<int> SendAsync(
        CalendarInvite row,
        CalendarOpening opening,
        CalendarEvent calendarEvent,
        VRChatInstance? instance,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var userId = row.VRChatUserId!;

        // Written first: a crash between this and the answer leaves an invite remembered that may
        // not have gone out, never one sent twice.
        Mark(row, CalendarInviteStates.Sending, problem: null, now);
        row.TriedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // VRChat calls the full location the invite's "instanceId".
        var request = new InviteRequest(instanceId: opening.Location!);

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.InvitesSend, Operation: "InviteUser"),
            (client, token) => client.Invites.InviteUserWithHttpInfoAsync(userId, request, token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (result.Success)
        {
            Mark(row, CalendarInviteStates.Invited, problem: null, now);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // VRChat only takes an invite to a friend.
            await LearnAsync(userId, isFriend: true, VRChatFriendSources.Invite, now, ct).ConfigureAwait(false);

            await _invites.RecordAsync(
                row, calendarEvent.Title, sent: true, via: "vrchat", now,
                instance?.VRChatInstanceId, instance?.WorldId ?? calendarEvent.WorldId, ct).ConfigureAwait(false);

            return 1;
        }

        // Nothing reached VRChat: the class is cold-stopped, or there is no session yet. The row
        // waits for the class to open, and the turn it took is given back. Not a retry -- there was
        // no first try.
        if (result.WasNotSent && result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting or VRChatFailureKind.NotConfigured)
        {
            Mark(row, CalendarInviteStates.Waiting, problem: null, now);
            row.TriedAt = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return 0;
        }

        // A 403 is VRChat's "You need to be friends with that user first": the one refusal its
        // description of this call lists. Remembered, so the next event goes straight to Discord.
        if (result.StatusCode == 403)
        {
            await LearnAsync(userId, isFriend: false, VRChatFriendSources.Refused, now, ct).ConfigureAwait(false);
            await NotFriendsAsync(row, calendarEvent, instance, now, ct).ConfigureAwait(false);
            return 0;
        }

        var problem = CalendarInvites.Short(result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.");

        if (row.DiscordUserId is { Length: > 0 })
        {
            // The calendar's Discord loop takes it from here.
            Mark(row, CalendarInviteStates.ToMessage, problem, now);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return 0;
        }

        Mark(row, CalendarInviteStates.CouldNotReach, problem, now);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _invites.RecordAsync(
            row, calendarEvent.Title, sent: false, via: "vrchat", now,
            instance?.VRChatInstanceId, instance?.WorldId ?? calendarEvent.WorldId, ct).ConfigureAwait(false);

        _log.Information(
            "An invite for the event {EventId} was refused: VRChat answered {Status}", calendarEvent.Id, result.StatusCode);

        return 0;
    }

    private static void Mark(CalendarInvite row, string state, string? problem, DateTimeOffset now)
    {
        row.State = state;
        row.Problem = problem;
        row.UpdatedAt = now;
    }
}
