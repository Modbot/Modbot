using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Calendar;

/// <summary>How far an occurrence's invites have got (calendar auto-invite design §10).</summary>
/// <param name="Total">Everybody on the queue who was not skipped: the M in "Invited N of M".</param>
/// <param name="VRChat">VRChat invites that went out, or were going out when Modbot stopped.</param>
/// <param name="Discord">Direct messages that went out, or were going out.</param>
/// <param name="CouldNotReach">Tried, and neither VRChat nor Discord took it.</param>
/// <param name="NoWay">Not a friend of the group's VRChat account and no Discord account.</param>
/// <param name="Waiting">Still to send.</param>
/// <param name="Stopped">Not sent before the instance closed or the time ended.</param>
/// <param name="Skipped">Banned, or already in the instance. Not in <paramref name="Total"/>.</param>
/// <param name="NotAsked">Did not ask for event invites, or stopped them. In <paramref name="Total"/>.</param>
public sealed record CalendarInviteCounts(
    int Total,
    int VRChat,
    int Discord,
    int CouldNotReach,
    int NoWay,
    int Waiting,
    int Stopped,
    int Skipped,
    int NotAsked = 0)
{
    /// <summary>The N in "Invited N of M".</summary>
    public int Invited => VRChat + Discord;

    public static CalendarInviteCounts From(IEnumerable<string> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        int vrchat = 0, discord = 0, couldNotReach = 0, noWay = 0, waiting = 0, stopped = 0, skipped = 0, notAsked = 0;

        foreach (var state in states)
        {
            switch (state)
            {
                case CalendarInviteStates.Invited or CalendarInviteStates.Sending:
                    vrchat++;
                    break;
                case CalendarInviteStates.Messaged or CalendarInviteStates.Messaging:
                    discord++;
                    break;
                case CalendarInviteStates.CouldNotReach:
                    couldNotReach++;
                    break;
                case CalendarInviteStates.NoWay:
                    noWay++;
                    break;
                case CalendarInviteStates.Waiting or CalendarInviteStates.ToMessage:
                    waiting++;
                    break;
                case CalendarInviteStates.Stopped:
                    stopped++;
                    break;
                case CalendarInviteStates.Skipped:
                    skipped++;
                    break;
                case CalendarInviteStates.NotAsked:
                    notAsked++;
                    break;
            }
        }

        var total = vrchat + discord + couldNotReach + noWay + waiting + stopped + notAsked;
        return new CalendarInviteCounts(total, vrchat, discord, couldNotReach, noWay, waiting, stopped, skipped, notAsked);
    }
}

/// <summary>
/// What both of the calendar's invite senders share: when an occurrence's invites stop, and the
/// facts they write (calendar auto-invite design §6, §8).
/// </summary>
/// <remarks>
/// The VRChat invites go out from the calendar's VRChat loop and the direct messages from its
/// Discord loop. Each asks <see cref="StopAsync"/> before it sends, so neither sends into an
/// instance that has closed, whichever happens to run first.
/// </remarks>
public sealed class CalendarInvites
{
    /// <summary>How much of VRChat's or Discord's answer is kept on the row and in the fact.</summary>
    public const int MaxProblemLength = 500;

    public const string NotFriends = "Not a friend of the group's VRChat account.";
    public const string NoAccount = "No VRChat or Discord account to reach.";
    public const string BannedFromGroup = "Banned from the group.";
    public const string BannedFromServer = "Banned from the Discord server.";
    public const string AlreadyThere = "Already in the instance.";
    public const string InstanceClosed = "The instance closed.";
    public const string TimeEnded = "The event ended.";
    public const string EventCancelled = "The event was cancelled.";
    public const string DidNotAsk = "Did not ask for event invites.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public CalendarInvites(ModbotContext db, IFactWriter facts, EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _facts = facts;
        _partitions = partitions;
    }

    /// <summary>
    /// Marks everything still to send as stopped, for each occurrence whose instance has closed,
    /// whose time has ended, or whose event was cancelled or deleted.
    /// </summary>
    /// <returns>How many rows were stopped.</returns>
    public async Task<int> StopAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var open = await _db.CalendarInvites
            .Where(i => CalendarInviteStates.Open.Contains(i.State))
            .Select(i => new { i.EventId, i.OccurrenceStartsAt })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        if (open.Count == 0)
            return 0;

        var stopped = 0;

        foreach (var occurrence in open)
        {
            if (await WhyStoppedAsync(occurrence.EventId, occurrence.OccurrenceStartsAt, now, ct).ConfigureAwait(false) is not { } why)
                continue;

            stopped += await _db.CalendarInvites
                .Where(i => i.EventId == occurrence.EventId
                    && i.OccurrenceStartsAt == occurrence.OccurrenceStartsAt
                    && CalendarInviteStates.Open.Contains(i.State))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(i => i.State, CalendarInviteStates.Stopped)
                        .SetProperty(i => i.Problem, why)
                        .SetProperty(i => i.UpdatedAt, now),
                    ct).ConfigureAwait(false);
        }

        return stopped;
    }

    /// <summary>
    /// Of these people, the ones who asked for event invites with <c>/me</c> and have not stopped
    /// them, by whichever id their choice was made under (calendar auto-invite design §2.1).
    /// </summary>
    public async Task<(HashSet<string> VRChat, HashSet<string> Discord)> AskedAsync(
        IReadOnlyCollection<string> vrchatIds, IReadOnlyCollection<string> discordIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vrchatIds);
        ArgumentNullException.ThrowIfNull(discordIds);

        if (vrchatIds.Count == 0 && discordIds.Count == 0)
            return ([], []);

        var asked = await _db.EventInviteChoices.AsNoTracking()
            .Where(c => c.Wants
                && (discordIds.Contains(c.DiscordUserId) || (c.VRChatUserId != null && vrchatIds.Contains(c.VRChatUserId))))
            .Select(c => new { c.DiscordUserId, c.VRChatUserId })
            .ToListAsync(ct).ConfigureAwait(false);

        return (
            [.. asked.Where(c => c.VRChatUserId != null).Select(c => c.VRChatUserId!)],
            [.. asked.Select(c => c.DiscordUserId)]);
    }

    /// <summary>
    /// Whether this person still wants the invite, asked again just before it is sent: a staff
    /// account's own switch, or a member's "Get event invites". Somebody who stopped invites after
    /// the queue was written gets nothing.
    /// </summary>
    public async Task<bool> StillWantedAsync(CalendarInvite invite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);

        if (invite.StaffUserId is { } staff)
        {
            return await _db.Users.AsNoTracking()
                .AnyAsync(u => u.Id == staff && u.GetsEventInvites && !u.IsDisabled && u.DeletedAt == null, ct)
                .ConfigureAwait(false);
        }

        var (vrchat, discord) = await AskedAsync(
            invite.VRChatUserId is { } v ? [v] : [],
            invite.DiscordUserId is { } d ? [d] : [],
            ct).ConfigureAwait(false);

        return vrchat.Count > 0 || discord.Count > 0;
    }

    /// <summary>
    /// Why an occurrence's invites must stop now, or null while they may still go out.
    /// </summary>
    public async Task<string?> WhyStoppedAsync(Guid eventId, DateTimeOffset occurrence, DateTimeOffset now, CancellationToken ct = default)
    {
        var calendarEvent = await _db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);

        if (calendarEvent is null || calendarEvent.DeletedAt is not null || calendarEvent.State == CalendarEventStates.Cancelled)
            return EventCancelled;

        if (now >= occurrence + (calendarEvent.EndsAt - calendarEvent.StartsAt))
            return TimeEnded;

        var opening = await _db.CalendarOpenings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.OccurrenceStartsAt == occurrence, ct).ConfigureAwait(false);

        if (opening?.Location is null)
            return InstanceClosed;

        if (opening.InstanceId is { } instanceId
            && await _db.VRChatInstances.AsNoTracking().AnyAsync(i => i.Id == instanceId && i.ClosedAt != null, ct).ConfigureAwait(false))
        {
            return InstanceClosed;
        }

        return null;
    }

    /// <summary>
    /// Writes the fact for one person: reached, or not. The subject is the person, so a purge
    /// erases it with the rest of them; there is no actor, because Modbot did this on its own.
    /// </summary>
    /// <param name="via"><c>vrchat</c> or <c>discord</c>: what went out, or what was last tried.</param>
    public async Task RecordAsync(
        CalendarInvite invite,
        string title,
        bool sent,
        string via,
        DateTimeOffset now,
        string? instanceId = null,
        string? worldId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);

        // A staff account with no VRChat or Discord account is nobody a fact could be about.
        if (invite.VRChatUserId is not { Length: > 0 } && invite.DiscordUserId is not { Length: > 0 })
            return;

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["eventId"] = invite.EventId.ToString(),
            ["title"] = title,
            ["occurrenceStartsAt"] = invite.OccurrenceStartsAt.ToString("O"),
            ["via"] = via,
            ["role"] = invite.Role,
        };

        if (!sent && invite.Problem is { Length: > 0 } problem)
            data["problem"] = problem;

        var onVRChat = invite.VRChatUserId is { Length: > 0 };

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = sent ? FactType.PlannedEventInviteSent : FactType.PlannedEventInviteFailed,
                OccurredAt = now,
                SubjectPlatform = onVRChat ? FactPlatform.VRChat : FactPlatform.Discord,
                SubjectId = onVRChat ? invite.VRChatUserId! : invite.DiscordUserId ?? string.Empty,
                WorldId = worldId,
                InstanceId = instanceId,
                Source = FactSource.Modbot,
                Data = data,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>Trims VRChat's or Discord's words to what a row and a fact keep.</summary>
    public static string Short(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= MaxProblemLength ? text : text[..MaxProblemLength];
    }
}
