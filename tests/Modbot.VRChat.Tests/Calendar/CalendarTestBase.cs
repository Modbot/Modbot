using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Sync;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// The calendar's VRChat side over a real database, a real gate and limiter, and a scripted VRChat.
/// </summary>
/// <remarks>
/// Each pass gets a fresh context, the way a scoped service does, so nothing passes on entities the
/// change tracker still remembered from the pass before -- which is exactly what a restart forgets.
/// </remarks>
public abstract class CalendarTestBase(PostgresFixture fixture) : SyncTestBase(fixture)
{
    protected const string WorldId = "wrld_calendar";

    protected async Task<CalendarPublishResult> PublishAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarVRChatPublisher(Gate, context, Clock, Facts(context)).RunOnceAsync(Ct);
    }

    /// <summary>What was read of VRChat's calendar and when, shared across reads the way the app shares it.</summary>
    protected CalendarVRChatReadMemory ReadMemory { get; } = new();

    /// <summary>Reads VRChat's calendar for the week from now, the way the calendar page does.</summary>
    protected async Task<CalendarReadResult> ReadAsync(bool refresh = false)
    {
        await using var context = Database.NewContext();
        var now = Clock.UtcNow;
        return await new CalendarVRChatReader(Gate, context, Clock, Facts(context), ReadMemory).ReadAsync(now, now.AddDays(7), refresh, Ct);
    }

    protected async Task<List<CalendarEvent>> EventsAsync()
    {
        await using var context = Database.NewContext();
        return await context.CalendarEvents.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync(Ct);
    }

    protected async Task<CalendarOpenerResult> OpenAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarOpener(Gate, context, new PlaceStore(context, Clock), Clock, Facts(context)).RunOnceAsync(Ct);
    }

    /// <summary>The friends ids a sign-in brought, shared across passes the way the app shares it.</summary>
    protected SignInFriends SignInFriends { get; } = new();

    /// <summary>One pass of the invite loop, in its own scope, the way the hosted service runs it.</summary>
    protected async Task<CalendarInviterResult> InviteAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarInviter(
            Gate,
            context,
            Clock,
            new GiveawayRuleChecker(context, Clock),
            Invites(context),
            SignInFriends).RunOnceAsync(Ct);
    }

    /// <summary>One pass of the VRChat group post for the first person, in its own scope.</summary>
    protected async Task<int> PostFirstJoinAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarFirstJoinVRChatPost(Gate, context, Clock).RunOnceAsync(Ct);
    }

    protected CalendarInvites Invites(ModbotContext context) =>
        new(context, new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock));

    /// <summary>The invite rows for an event, in the order they are sent.</summary>
    protected async Task<List<CalendarInvite>> InviteRowsAsync(Guid eventId)
    {
        await using var context = Database.NewContext();
        return await context.CalendarInvites.AsNoTracking()
            .Where(i => i.EventId == eventId)
            .OrderBy(i => i.Position)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// These people pressed "Get event invites" (or, with <paramref name="wants"/> false, "Stop event
    /// invites") under /me, from a Discord account linked to their VRChat one.
    /// </summary>
    protected async Task AskForInvitesAsync(IEnumerable<string> vrchatUserIds, bool wants = true)
    {
        await using var context = Database.NewContext();

        foreach (var id in vrchatUserIds)
        {
            var discord = $"discord-of-{id}";
            var choice = await context.EventInviteChoices.FirstOrDefaultAsync(c => c.DiscordUserId == discord, Ct);

            if (choice is null)
            {
                choice = new EventInviteChoice { DiscordUserId = discord };
                context.EventInviteChoices.Add(choice);
            }

            choice.VRChatUserId = id;
            choice.Wants = wants;
            choice.ChangedAt = Clock.UtcNow;
        }

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>A staff account with the accounts it linked.</summary>
    protected async Task<ModbotUser> AddStaffAsync(
        string name, string? vrchatUserId = null, string? discordUserId = null, bool getsEventInvites = true)
    {
        var user = new ModbotUser
        {
            GetsEventInvites = getsEventInvites,
            Username = name,
            UsernameNormalized = name.ToUpperInvariant(),
            PasswordHash = "x",
            VRChatUserId = vrchatUserId,
            DiscordUserId = discordUserId,
            DiscordVerifiedAt = discordUserId is null ? null : Clock.UtcNow,
            CreatedAt = Clock.UtcNow,
        };

        await using var context = Database.NewContext();
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    /// <summary>A saved list everybody in the group is on, every one of whom asked for event invites.</summary>
    protected Task<SavedList> AddEverybodyListAsync(params string[] groupMembers) =>
        AddEverybodyListAsync(asked: true, groupMembers);

    /// <summary>
    /// A saved list everybody in the group is on: no rules. <paramref name="asked"/> says whether they
    /// all pressed "Get event invites" under /me.
    /// </summary>
    protected async Task<SavedList> AddEverybodyListAsync(bool asked, params string[] groupMembers)
    {
        if (asked)
            await AskForInvitesAsync(groupMembers);

        await using var context = Database.NewContext();

        foreach (var id in groupMembers)
        {
            context.GroupMembers.Add(new Core.Data.Entities.GroupMember
            {
                GroupId = GroupId,
                UserId = id,
                FirstSeenAt = Clock.UtcNow,
                LastSeenAt = Clock.UtcNow,
            });
        }

        var list = new SavedList
        {
            Id = Guid.CreateVersion7(),
            Name = "Everybody",
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow,
        };

        context.SavedLists.Add(list);
        await context.SaveChangesAsync(Ct);
        return list;
    }

    /// <summary>Somebody pressing Open now on an event, in its own scope the way a request has one.</summary>
    protected async Task<CalendarOpenNowResult> OpenNowAsync(Guid eventId, Guid actor)
    {
        await using var context = Database.NewContext();
        return await new CalendarOpener(Gate, context, new PlaceStore(context, Clock), Clock, Facts(context))
            .OpenNowAsync(eventId, actor, Ct);
    }

    protected async Task<CalendarOpening> OpeningAsync(Guid eventId)
    {
        await using var context = Database.NewContext();
        return await context.CalendarOpenings.AsNoTracking().SingleAsync(o => o.EventId == eventId, Ct);
    }

    protected async Task<int> ScheduleAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarScheduler(context, Clock, Facts(context)).RunOnceAsync(Ct);
    }

    protected CalendarFacts Facts(ModbotContext context) =>
        new(new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock), Clock);

    /// <summary>A scheduled event starting <paramref name="startsIn"/> from now, saved.</summary>
    protected async Task<CalendarEvent> AddEventAsync(TimeSpan startsIn, Action<CalendarEvent>? shape = null)
    {
        var now = Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + startsIn,
            EndsAt = now + startsIn + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = WorldId,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);

        await using var context = Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    /// <summary>Changes a saved event the way the API does: the change, a new version, a new change time.</summary>
    protected async Task EditAsync(Guid id, Action<CalendarEvent> change)
    {
        await using var context = Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        e.Version++;
        e.UpdatedAt = Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    protected async Task<CalendarEventPlace?> PlaceAsync(Guid id, string place)
    {
        await using var context = Database.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking().SingleOrDefaultAsync(p => p.EventId == id && p.Place == place, Ct);
    }
}
