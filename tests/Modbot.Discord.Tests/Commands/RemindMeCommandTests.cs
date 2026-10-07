using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/remindme</c> (Discord commands design §3.5 and §4, step 6): off until the operator turns it on,
/// open to every member, one confirmation direct message with Stop, nothing stored when direct
/// messages are closed, only what <c>/events</c> would show, one waiting reminder per date and ten
/// per member, and Stop from the direct message.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RemindMeCommandTests
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    private readonly PostgresFixture _db;

    public RemindMeCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordCommandCall Call(string caller, params (string Name, string Value)[] options)
        => new(
            caller,
            "someone",
            DiscordCommands.RemindMe,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            (_, _) => Task.CompletedTask);

    private static DiscordCommandCall HelpCall(string caller)
        => new(caller, "someone", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, bool on = true)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.SwitchCommand(DiscordCommands.RemindMe, on);
        }, Ct);
        return services;
    }

    /// <summary>Runs it as the bot does: through the switch, as a member with no Modbot account.</summary>
    private static async Task<DiscordReply?> AskAsync(
        TestServices services, FakeGateway? gateway, string caller = "999", params (string Name, string Value)[] options)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Call(caller, options), Ct, gateway);
    }

    private static (string, string) Event(Guid id) => (DiscordCommands.RemindEventOption, id.ToString("D"));

    private static (string, string) Before(string choice) => (DiscordCommands.RemindBeforeOption, choice);

    private static string Unix(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>A scheduled event published to this Discord, as the calendar leaves it once its post is up.</summary>
    private static async Task<CalendarEvent> AddEventAsync(
        TestServices services,
        string title,
        TimeSpan startsIn,
        Action<CalendarEvent>? shape = null,
        string? place = CalendarPlaces.DiscordEvent,
        string placeState = CalendarPlaceStates.Published)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Description = "Bring snacks",
            StartsAt = now + startsIn,
            EndsAt = now + startsIn + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            PublishToDiscord = true,
            PostToChannel = false,
            ChannelId = Channel,
            AccessType = "members",
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);
        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);

        if (place is not null)
        {
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = e.Id,
                Place = place,
                State = placeState,
                ExternalId = "5551234",
                ChannelId = Channel,
                OccurrenceStartsAt = e.OccurrenceStartsAt,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static async Task<List<EventReminder>> RemindersAsync(TestServices services, string? user = null)
    {
        await using var context = services.Database.NewContext();
        return await context.EventReminders.AsNoTracking()
            .Where(r => user == null || r.DiscordUserId == user)
            .OrderBy(r => r.RemindAt).ThenBy(r => r.Id)
            .ToListAsync(Ct);
    }

    private static async Task<EventReminder> AddReminderAsync(
        TestServices services, CalendarEvent e, string user, string state = EventReminderStates.Waiting, int minutes = 60)
    {
        var now = services.Clock.UtcNow;
        var reminder = new EventReminder
        {
            DiscordUserId = user,
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            MinutesBefore = minutes,
            RemindAt = e.StartsAt - TimeSpan.FromMinutes(minutes),
            State = state,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var context = services.Database.NewContext();
        context.EventReminders.Add(reminder);
        await context.SaveChangesAsync(Ct);
        return reminder;
    }

    private static async Task<DiscordReply> PressAsync(TestServices services, string user, string buttonId)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleButtonAsync(
            new DiscordButtonPress(user, "someone", buttonId, (_, _) => Task.CompletedTask), null, Ct);
    }

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RemindMe_IsForEveryone_OffByDefault_AndRepliesInPrivate()
    {
        var remind = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.RemindMe);

        Assert.Equal("remindme", remind.Name);
        Assert.Equal("Get a message before an event", remind.Description);
        Assert.Equal(DiscordShownTo.Everyone, remind.ShownTo);
        Assert.False(remind.StaffOnly);
        Assert.Equal(DiscordReplyKind.Private, remind.Reply);
        Assert.False(remind.RepliesInPublic(new Dictionary<string, string>()));
        Assert.Null(remind.PublicOncePer);

        var which = remind.Options[0];
        Assert.Equal(("event", "Which event", DiscordOptionKind.Text, false, true), (which.Name, which.Description, which.Kind, which.Required, which.Suggests));

        var before = remind.Options[1];
        Assert.Equal(("before", "How long before", DiscordOptionKind.Choice, false), (before.Name, before.Description, before.Kind, before.Required));
        Assert.Equal(["15 minutes", "1 hour", "1 day"], before.Choices!.Select(c => c.Name));
        Assert.Equal(["15m", "1h", "1d"], before.Choices!.Select(c => c.Value));

        // Off until the operator turns it on (decision 6): not even registered.
        Assert.False(DiscordCommandSwitches.Find(DiscordCommands.RemindMe)?.OnByDefault);
        Assert.False(DiscordCommandSwitches.IsOn(null, DiscordCommands.RemindMe));
        Assert.DoesNotContain(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.RemindMe);
        Assert.Contains(
            DiscordCommands.For(DiscordCommandSwitches.With(null, DiscordCommands.RemindMe, true)),
            c => c.Name == DiscordCommands.RemindMe);

        // Any member: no Modbot account, no permission.
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.RemindMe));
        Assert.Null(DiscordCommands.Requires(DiscordCommands.RemindMe));
        Assert.False(DiscordCommands.Writes(DiscordCommands.RemindMe));
        Assert.Equal(["`/remindme event: before:` — Get a message before an event"], DiscordCommandHandler.HelpLines(remind));
    }

    [Fact]
    public async Task WhileOff_ItIsTold_AndRecordedAsOff_AndStoresNothing()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var gateway = new FakeGateway();

        var reply = await AskAsync(services, gateway, "999", Event(e.Id));

        Assert.Equal("/remindme is turned off on this server.", reply?.Text);
        Assert.Equal("off", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
        Assert.Empty(await RemindersAsync(services));
        Assert.Empty(gateway.DirectMessages);
    }

    [Fact]
    public async Task Help_ListsItForAnyMember_OnlyWhileItIsOn()
    {
        await using var services = await SetUpAsync(_db);

        using (var scope = services.Scope())
        {
            var on = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(HelpCall("999"), Ct);
            Assert.Contains("`/remindme event: before:` — Get a message before an event", on.Text, StringComparison.Ordinal);
        }

        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.RemindMe, false), Ct);

        using var again = services.Scope();
        var off = await again.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(HelpCall("999"), Ct);
        Assert.DoesNotContain("/remindme", off.Text, StringComparison.Ordinal);
    }

    // ── Signing up ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMemberWithNoModbotAccount_GetsOneConfirmationWithStop_AndTheReminderIsStored()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var gateway = new FakeGateway();
        var remindAt = e.StartsAt - TimeSpan.FromHours(1);

        var reply = await AskAsync(services, gateway, "999", Event(e.Id));

        // The private reply, and one direct message: "I'll remind you about **Title** <t:f>."
        Assert.Equal($"I'll remind you <t:{Unix(remindAt)}:f>.", reply?.Text);

        var message = Assert.Single(gateway.ActionMessages);
        Assert.Equal("999", message.UserId);
        Assert.Equal($"I'll remind you about **Movie night** <t:{Unix(remindAt)}:f>.", message.Text);

        var stop = Assert.Single(message.Actions);
        var reminder = Assert.Single(await RemindersAsync(services));
        Assert.Equal("Stop", stop.Label);
        Assert.Equal($"modbot:remind:stop:{reminder.Id:D}@{Guild}", stop.Id);
        Assert.True(Modbot.Discord.Interactions.StaffMenus.Fits(stop.Id));

        Assert.Equal("999", reminder.DiscordUserId);
        Assert.Equal(e.Id, reminder.EventId);
        Assert.Equal(e.StartsAt, reminder.OccurrenceStartsAt);
        Assert.Equal(60, reminder.MinutesBefore);
        Assert.Equal(remindAt, reminder.RemindAt);
        Assert.Equal(EventReminderStates.Waiting, reminder.State);
        Assert.Equal(services.Clock.UtcNow, reminder.CreatedAt);

        // Recorded like every command: the caller's Discord id, no Modbot account.
        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal("999", fact.SubjectId);
        Assert.Null(fact.ActorId);
        Assert.Equal("remindme", JsonDocument.Parse(fact.Data).RootElement.GetProperty("command").GetString());
        Assert.Equal("answered", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("15m", 15)]
    [InlineData("1h", 60)]
    [InlineData("1d", 1440)]
    [InlineData(null, 60)]
    public async Task BeforeDefaultsToOneHour_AndTheOtherTwoChoicesAreFifteenMinutesAndADay(string? choice, int minutes)
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));

        (string, string)[] options = choice is null ? [Event(e.Id)] : [Event(e.Id), Before(choice)];
        await AskAsync(services, new FakeGateway(), "999", options);

        var reminder = Assert.Single(await RemindersAsync(services));
        Assert.Equal(minutes, reminder.MinutesBefore);
        Assert.Equal(e.StartsAt - TimeSpan.FromMinutes(minutes), reminder.RemindAt);
    }

    [Fact]
    public async Task WithDirectMessagesClosed_TheMemberIsToldSo_AndNothingIsStored()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        var reply = await AskAsync(services, gateway, "999", Event(e.Id));

        // 50007: the private reply says so, and not a row is left behind.
        Assert.Equal("Your direct messages are closed, so Modbot can't remind you.", reply?.Text);
        Assert.Empty(await RemindersAsync(services));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal("dms-closed", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        // And the member can try again once they open them, as if nothing had happened.
        gateway.DirectMessagesClosed = false;
        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "999", Event(e.Id)))?.Text, StringComparison.Ordinal);
        Assert.Single(await RemindersAsync(services));
    }

    [Fact]
    public async Task WhenTheBotIsNotConnected_NothingIsStored_Either()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));

        var reply = await AskAsync(services, gateway: null, "999", Event(e.Id));

        Assert.Equal("Modbot could not send you a message.", reply?.Text);
        Assert.Empty(await RemindersAsync(services));
    }

    [Fact]
    public async Task ARepeatingEvent_IsRemindedForItsNextDateOnly()
    {
        await using var services = await SetUpAsync(_db);
        var weekly = await AddEventAsync(services, "Weekly", TimeSpan.FromDays(2), e => e.Repeat = CalendarRepeats.Weekly);
        var gateway = new FakeGateway();

        await AskAsync(services, gateway, "999", Event(weekly.Id));

        // Decision 9: one request, one message, for the next date.
        var reminder = Assert.Single(await RemindersAsync(services));
        Assert.Equal(weekly.StartsAt, reminder.OccurrenceStartsAt);
        Assert.Equal(weekly.StartsAt - TimeSpan.FromHours(1), reminder.RemindAt);
    }

    [Fact]
    public async Task ARunningDateIsNotTheNextOne_TheFollowingDateIs()
    {
        await using var services = await SetUpAsync(_db);

        // Started 10 minutes ago and repeating every day: the next date is tomorrow's.
        var daily = await AddEventAsync(services, "Daily", TimeSpan.FromMinutes(-10), e => e.Repeat = CalendarRepeats.Daily);

        await AskAsync(services, new FakeGateway(), "999", Event(daily.Id), Before("1d"));

        // A day before tomorrow's start is 10 minutes ago, which is within the 15 minutes' grace.
        var reminder = Assert.Single(await RemindersAsync(services));
        Assert.Equal(daily.StartsAt.AddDays(1), reminder.OccurrenceStartsAt);
    }

    [Fact]
    public async Task OnlyEventsThatEventsWouldShow_CanBePicked()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var draft = await AddEventAsync(services, "Draft", TimeSpan.FromDays(3), e => e.State = CalendarEventStates.Draft);
        var cancelled = await AddEventAsync(services, "Cancelled", TimeSpan.FromDays(3), e => (e.State, e.CancelledAt) = (CalendarEventStates.Cancelled, services.Clock.UtcNow));
        var deleted = await AddEventAsync(services, "Deleted", TimeSpan.FromDays(3), e => e.DeletedAt = services.Clock.UtcNow);
        var unposted = await AddEventAsync(services, "Never posted", TimeSpan.FromDays(3), place: null);
        var waiting = await AddEventAsync(services, "Waiting to post", TimeSpan.FromDays(3), placeState: CalendarPlaceStates.Waiting);
        var switchedOff = await AddEventAsync(services, "Switched off", TimeSpan.FromDays(3), e => e.PublishToDiscord = false);

        // A different member each time: five runs a minute is a limit of its own.
        var member = 100;

        foreach (var e in new[] { draft, cancelled, deleted, unposted, waiting, switchedOff })
        {
            Assert.Equal(
                "Modbot can't find that event.",
                (await AskAsync(services, gateway, (member++).ToString(CultureInfo.InvariantCulture), Event(e.Id)))?.Text);
        }

        Assert.Empty(await RemindersAsync(services));
        Assert.Empty(gateway.DirectMessages);

        // Not an event at all.
        Assert.Equal(
            "Modbot can't find that event.",
            (await AskAsync(services, gateway, "200", (DiscordCommands.RemindEventOption, "nonsense")))?.Text);
        Assert.Equal("Modbot can't find that event.", (await AskAsync(services, gateway, "200", Event(Guid.NewGuid())))?.Text);
    }

    [Fact]
    public async Task AnEventTypedByItsTitle_IsFound_UnlessTwoShareTheTitle()
    {
        await using var services = await SetUpAsync(_db);
        var movie = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        await AddEventAsync(services, "Quiz", TimeSpan.FromDays(3));
        await AddEventAsync(services, "Quiz", TimeSpan.FromDays(4));
        var gateway = new FakeGateway();

        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "999", (DiscordCommands.RemindEventOption, " movie NIGHT ")))?.Text, StringComparison.Ordinal);
        Assert.Equal(movie.Id, Assert.Single(await RemindersAsync(services)).EventId);

        Assert.Equal("Modbot can't find that event.", (await AskAsync(services, gateway, "999", (DiscordCommands.RemindEventOption, "Quiz")))?.Text);
    }

    [Fact]
    public async Task TooCloseToTheStart_IsRefused_ButUpTo15MinutesLateIsTaken()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        // Starts in 30 minutes; an hour before was 30 minutes ago, and that would never be sent.
        var close = await AddEventAsync(services, "Close", TimeSpan.FromMinutes(30));
        Assert.Equal(
            "That is too close to the start. Pick a shorter time.",
            (await AskAsync(services, gateway, "999", Event(close.Id)))?.Text);
        Assert.Empty(await RemindersAsync(services));
        Assert.Empty(gateway.DirectMessages);

        // The shorter time works.
        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "999", Event(close.Id), Before("15m")))?.Text, StringComparison.Ordinal);

        // 10 minutes late is inside the grace: it is sent at the next pass, so the reply says now.
        var late = await AddEventAsync(services, "Late", TimeSpan.FromMinutes(50));
        var reply = await AskAsync(services, gateway, "999", Event(late.Id));
        Assert.Equal($"I'll remind you <t:{Unix(services.Clock.UtcNow)}:f>.", reply?.Text);
        Assert.Equal(2, (await RemindersAsync(services)).Count);
    }

    [Fact]
    public async Task OneWaitingReminderPerMemberPerDate_AndStoppingItLetsThemAskAgain()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var gateway = new FakeGateway();

        await AskAsync(services, gateway, "999", Event(e.Id));
        var again = await AskAsync(services, gateway, "999", Event(e.Id), Before("1d"));

        Assert.Equal("You already have a reminder for that date.", again?.Text);
        Assert.Single(await RemindersAsync(services));
        Assert.Single(gateway.DirectMessages);
        Assert.Equal("duplicate", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        // Somebody else may ask for the same date.
        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "888", Event(e.Id)))?.Text, StringComparison.Ordinal);

        // Stopped: not waiting, so a new one is allowed.
        await PressAsync(services, "999", Assert.Single(gateway.ActionMessages, m => m.UserId == "999").Actions[0].Id);
        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "999", Event(e.Id), Before("1d")))?.Text, StringComparison.Ordinal);
        Assert.Equal(3, (await RemindersAsync(services)).Count);
    }

    [Fact]
    public async Task AtMostTenWaiting_PerMember_AnElevenIsRefused_AndAStoppedOneDoesNotCount()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var events = new List<CalendarEvent>();
        for (var i = 0; i < 12; i++)
            events.Add(await AddEventAsync(services, "Event " + i, TimeSpan.FromDays(3 + i)));

        for (var i = 0; i < EventReminder.MostWaiting; i++)
            await AddReminderAsync(services, events[i], "999");

        // A stopped one and somebody else's do not count.
        await AddReminderAsync(services, events[10], "999", EventReminderStates.Stopped);
        await AddReminderAsync(services, events[10], "888");

        var refused = await AskAsync(services, gateway, "999", Event(events[10].Id));

        Assert.Equal("You already have 10 reminders.", refused?.Text);
        Assert.Equal(EventReminder.MostWaiting, (await RemindersAsync(services, "999")).Count(r => r.State == EventReminderStates.Waiting));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal("limit", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        // Somebody with none is not held back by it.
        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "777", Event(events[11].Id)))?.Text, StringComparison.Ordinal);

        // Stopping one makes room.
        var first = (await RemindersAsync(services, "999")).First(r => r.State == EventReminderStates.Waiting);
        await PressAsync(services, "999", RemindMeCommand.StopFor(first.Id, Guild).Id);
        Assert.StartsWith("I'll remind you", (await AskAsync(services, gateway, "999", Event(events[10].Id)))?.Text, StringComparison.Ordinal);
    }

    // ── Event invites stay separate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AReminderSignUp_DoesNotSignAnybodyUpForEventInvites_AndStoppingInvitesDoesNotStopIt()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));

        await AskAsync(services, new FakeGateway(), "999", Event(e.Id));

        await using (var context = services.Database.NewContext())
        {
            Assert.Empty(await context.EventInviteChoices.ToListAsync(Ct));

            // They stopped event invites some other time: the reminder is not an invite.
            context.EventInviteChoices.Add(new EventInviteChoice { DiscordUserId = "999", Wants = false, ChangedAt = services.Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        var reminder = Assert.Single(await RemindersAsync(services));
        Assert.Equal(EventReminderStates.Waiting, reminder.State);
    }

    // ── The list ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoEvent_ItListsTheMembersOwnReminders_EachWithStop()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        var first = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var second = await AddEventAsync(services, "Quiz *night*", TimeSpan.FromDays(5));

        // Nothing yet.
        Assert.Equal("You have no reminders.", (await AskAsync(services, gateway, "999"))?.Text);

        await AskAsync(services, gateway, "999", Event(first.Id));
        await AskAsync(services, gateway, "999", Event(second.Id), Before("1d"));
        await AskAsync(services, gateway, "888", Event(second.Id));

        var reply = await AskAsync(services, gateway, "999");

        // Soonest reminder first; the date in the reader's own zone; how long before.
        Assert.Equal(
            $"1. **Movie night** · <t:{Unix(first.StartsAt)}:f> · 1 hour before\n"
            + $"2. **Quiz \\*night\\*** · <t:{Unix(second.StartsAt)}:f> · 1 day before",
            reply?.Text);

        var reminders = await RemindersAsync(services, "999");
        Assert.Equal(["Stop 1", "Stop 2"], reply!.Actions!.Select(a => a.Label));
        Assert.Equal(
            reminders.Select(r => $"modbot:remind:stop:{r.Id:D}@{Guild}"),
            reply.Actions!.Select(a => a.Id));

        // Listing writes nothing but the access record.
        Assert.Equal("listed", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AListOfOne_IsNotNumbered_AndItsButtonIsJustStop()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        await AskAsync(services, new FakeGateway(), "999", Event(e.Id), Before("15m"));

        var reply = await AskAsync(services, new FakeGateway(), "999");

        Assert.Equal($"**Movie night** · <t:{Unix(e.StartsAt)}:f> · 15 minutes before", reply?.Text);
        Assert.Equal("Stop", Assert.Single(reply!.Actions!).Label);
    }

    [Fact]
    public async Task AStoppedOrSentReminder_IsNotListed()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        await AddReminderAsync(services, e, "999", EventReminderStates.Stopped);
        await AddReminderAsync(services, e, "999", EventReminderStates.Sent);

        Assert.Equal("You have no reminders.", (await AskAsync(services, new FakeGateway(), "999"))?.Text);
    }

    // ── Stop ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopInTheConfirmation_StopsIt()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var gateway = new FakeGateway();
        await AskAsync(services, gateway, "999", Event(e.Id));
        var stop = Assert.Single(gateway.ActionMessages).Actions[0];

        var reply = await PressAsync(services, "999", stop.Id);

        Assert.Equal("Reminder stopped.", reply.Text);

        var reminder = Assert.Single(await RemindersAsync(services));
        Assert.Equal(EventReminderStates.Stopped, reminder.State);
        Assert.Equal(services.Clock.UtcNow, reminder.StoppedAt);
        Assert.Equal(services.Clock.UtcNow, reminder.UpdatedAt);

        // The same button again is the same answer.
        Assert.Equal("Reminder stopped.", (await PressAsync(services, "999", stop.Id)).Text);
    }

    [Fact]
    public async Task StopFromTheList_Works_AndSoDoesItAfterTheCommandWasSwitchedOff()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        await AskAsync(services, new FakeGateway(), "999", Event(e.Id));
        var listed = await AskAsync(services, new FakeGateway(), "999");

        // Nobody should be left unable to stop something they asked for.
        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.RemindMe, false), Ct);

        Assert.Equal("Reminder stopped.", (await PressAsync(services, "999", listed!.Actions![0].Id)).Text);
        Assert.Equal(EventReminderStates.Stopped, Assert.Single(await RemindersAsync(services)).State);
    }

    [Fact]
    public async Task StopOnlyStopsTheMembersOwnReminder()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var reminder = await AddReminderAsync(services, e, "999");

        var reply = await PressAsync(services, "888", RemindMeCommand.StopFor(reminder.Id, Guild).Id);

        Assert.Equal("Nothing to stop.", reply.Text);
        Assert.Equal(EventReminderStates.Waiting, Assert.Single(await RemindersAsync(services)).State);
    }

    [Theory]
    [InlineData(EventReminderStates.Sending)]
    [InlineData(EventReminderStates.Sent)]
    [InlineData(EventReminderStates.Skipped)]
    [InlineData(EventReminderStates.Failed)]
    public async Task StopAfterTheMessageWentOut_HasNothingToStop_AndChangesNothing(string state)
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        var reminder = await AddReminderAsync(services, e, "999", state);

        var reply = await PressAsync(services, "999", RemindMeCommand.StopFor(reminder.Id, Guild).Id);

        Assert.Equal("Nothing to stop.", reply.Text);
        Assert.Equal(state, Assert.Single(await RemindersAsync(services)).State);
    }

    [Theory]
    [InlineData("modbot:remind:stop:")]
    [InlineData("modbot:remind:stop:not-a-guid")]
    [InlineData("modbot:remind:stop:00000000-0000-0000-0000-000000000000@111111111111111111")]
    public async Task AStopButtonWithNoReminderBehindIt_HasNothingToStop(string id)
    {
        await using var services = await SetUpAsync(_db);

        Assert.Equal("Nothing to stop.", (await PressAsync(services, "999", id)).Text);
    }

    // ── The Stop button's server mark ───────────────────────────────────────────────────────

    [Fact]
    public void TheStopButton_CarriesTheServersMark_SoOnlyTheRightModbotAnswersADirectMessage()
    {
        var id = Guid.CreateVersion7();
        var button = RemindMeCommand.StopFor(id, Guild);

        Assert.Equal($"modbot:remind:stop:{id:D}{DiscordActionButton.ServerMark}{Guild}", button.Id);
        Assert.True(button.Id.Length <= 100);
        Assert.True(RemindMeCommand.IsStopButton(button.Id));
        Assert.True(RemindMeCommand.IsStopButton(RemindMeCommand.StopButton + id.ToString("D")));
        Assert.False(RemindMeCommand.IsStopButton("modbot:gate:letin:123"));

        // A press in a direct message names no server: this Modbot takes it by the mark, and no
        // other Modbot sharing the bot does.
        Assert.True(DiscordNetGateway.IsOurButton(Guild, null, button.Id));
        Assert.False(DiscordNetGateway.IsOurButton("999999999999999999", null, button.Id));
        Assert.False(DiscordNetGateway.IsOurButton(Guild, null, RemindMeCommand.StopButton + id.ToString("D")));

        // In the server it is ours by the server alone, and another server's is not.
        Assert.True(DiscordNetGateway.IsOurButton(Guild, ulong.Parse(Guild, CultureInfo.InvariantCulture), button.Id));
        Assert.False(DiscordNetGateway.IsOurButton(Guild, 123456UL, button.Id));

        // Acknowledged the moment it arrives: it never shows a form.
        Assert.False(DiscordNetGateway.AnswersInPlace(button.Id));
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    private static DiscordSuggestionAsk Ask(string typed, string command = "remindme", string option = "event")
        => new("999", command, option, typed, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Suggestions_AreTheEventsEventsWouldShow_SoonestFirst_WithTheIdAsTheValue()
    {
        await using var services = await SetUpAsync(_db);
        var later = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(5));
        var sooner = await AddEventAsync(services, "Movie marathon", TimeSpan.FromDays(2));
        await AddEventAsync(services, "Quiz", TimeSpan.FromDays(1));
        await AddEventAsync(services, "Movie draft", TimeSpan.FromDays(1), e => e.State = CalendarEventStates.Draft);
        await AddEventAsync(services, "Movie unposted", TimeSpan.FromDays(1), place: null);
        await AddEventAsync(services, "Movie cancelled", TimeSpan.FromDays(1), e => (e.State, e.CancelledAt) = (CalendarEventStates.Cancelled, services.Clock.UtcNow));

        using var scope = services.Scope();
        var handler = scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>();

        var movies = await handler.SuggestAsync(Ask("movie"), Ct);
        Assert.Equal(
            [("Movie marathon", sooner.Id.ToString("D")), ("Movie night", later.Id.ToString("D"))],
            movies.Select(s => (s.Name, s.Value)));

        // Typed nothing: everything it would show.
        Assert.Equal(3, (await handler.SuggestAsync(Ask(string.Empty), Ct)).Count);

        // Discord's caps: 36-character values, at most 100 characters.
        Assert.All(movies, s => Assert.True(s.Value.Length <= DiscordSuggestion.Longest && s.Name.Length <= DiscordSuggestion.Longest));
    }

    [Fact]
    public async Task Suggestions_AreAtMost25_AndNothingForAnotherOption_OrWhileOff()
    {
        await using var services = await SetUpAsync(_db);
        for (var i = 0; i < 30; i++)
            await AddEventAsync(services, "Event " + i, TimeSpan.FromDays(1 + i));

        using (var scope = services.Scope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>();

            Assert.Equal(DiscordSuggestion.Most, (await handler.SuggestAsync(Ask(string.Empty), Ct)).Count);
            Assert.Empty(await handler.SuggestAsync(Ask(string.Empty, option: "before"), Ct));
        }

        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.RemindMe, false), Ct);

        using var again = services.Scope();
        Assert.Empty(await again.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(Ask(string.Empty), Ct));

        // Suggestions are not a look at anybody: no access record.
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
    }

    // ── The member limit ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneMemberMayRunItFiveTimesAMinute_AndASixthIsToldToWait_UnrecordedAndThenAllowedAgain()
    {
        await using var services = await SetUpAsync(_db);

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.Equal("You have no reminders.", (await AskAsync(services, new FakeGateway(), "999"))?.Text);

        Assert.Equal("Slow down. Try again in a minute.", (await AskAsync(services, new FakeGateway(), "999"))?.Text);
        Assert.Equal(MemberCommandLimits.PerMinute, (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct)).Count);

        // Somebody else is not held up, and a minute later neither is the first.
        Assert.Equal("You have no reminders.", (await AskAsync(services, new FakeGateway(), "888"))?.Text);

        services.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("You have no reminders.", (await AskAsync(services, new FakeGateway(), "999"))?.Text);
    }

    [Fact]
    public async Task ItsLimitIsItsOwn_SoMeEventsAndVerifyDoNotUseItUp()
    {
        await using var services = await SetUpAsync(_db);

        using var scope = services.Scope();
        var me = scope.ServiceProvider.GetRequiredService<MemberCommandLimits>();
        var events = scope.ServiceProvider.GetRequiredKeyedService<MemberCommandLimits>(EventsCommand.LimitsKey);

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
        {
            Assert.True(me.TryUse("999", services.Clock.UtcNow));
            Assert.True(events.TryUse("999", services.Clock.UtcNow));
        }

        Assert.Equal("You have no reminders.", (await AskAsync(services, new FakeGateway(), "999"))?.Text);
    }
}
