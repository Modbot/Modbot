using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Commands;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// The pass that sends members' <c>/remindme</c> messages (Discord commands design §3.5): once, across
/// a crash; following a date that moved and skipping one that was cancelled; never more than 15
/// minutes late; two seconds between messages; a Join link only for an event anyone can join whose
/// instance is open; and old finished rows deleted after 30 days.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventReminderMessagesTests
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";

    private readonly PostgresFixture _db;

    public EventReminderMessagesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unix(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

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

    /// <summary>One pass, with no real waiting; the pauses it asked for are put in <paramref name="delays"/>.</summary>
    private static async Task<int> PassAsync(TestServices services, FakeGateway gateway, List<TimeSpan>? delays = null)
    {
        await using var context = services.Database.NewContext();

        return await new EventReminderMessages(
                context,
                services.Clock,
                delay: (by, _) =>
                {
                    delays?.Add(by);
                    return Task.CompletedTask;
                })
            .RunOnceAsync(gateway, Ct);
    }

    private static async Task<CalendarEvent> AddEventAsync(
        TestServices services, string title, TimeSpan startsIn, Action<CalendarEvent>? shape = null)
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
            ChannelId = Channel,
            AccessType = "members",
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);
        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static async Task<EventReminder> AddReminderAsync(
        TestServices services,
        CalendarEvent e,
        string user = "999",
        int minutes = 60,
        string state = EventReminderStates.Waiting,
        Action<EventReminder>? shape = null)
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

        shape?.Invoke(reminder);

        await using var context = services.Database.NewContext();
        context.EventReminders.Add(reminder);
        await context.SaveChangesAsync(Ct);
        return reminder;
    }

    private static async Task<EventReminder> ReminderAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        return await context.EventReminders.AsNoTracking().SingleAsync(r => r.Id == id, Ct);
    }

    private static async Task ChangeDateAsync(TestServices services, CalendarEvent e, Action<CalendarDateChange> change)
    {
        var now = services.Clock.UtcNow;
        var row = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = e.Id,
            PlannedStartsAt = e.StartsAt,
            CreatedAt = now,
            UpdatedAt = now,
        };

        change(row);

        await using var context = services.Database.NewContext();
        context.CalendarDateChanges.Add(row);
        await context.SaveChangesAsync(Ct);
    }

    private static async Task SetEventAsync(TestServices services, Guid id, Action<CalendarEvent> change)
    {
        await using var context = services.Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>What the VRChat side leaves when it opens the instance: the instance and the opening row.</summary>
    private static async Task<string> OpenInstanceAsync(TestServices services, CalendarEvent e, bool closed = false)
    {
        var number = e.Id.ToString("N")[..10];
        var location = $"{World}:{number}~group({Group})~groupAccessType({e.AccessType})~region(us)";
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = location,
            WorldId = World,
            VRChatInstanceId = number,
            GroupId = Group,
            OpenedAt = now,
            LastSeenAt = now,
            ClosedAt = closed ? now : null,
        };

        context.VRChatInstances.Add(instance);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.OccurrenceStartsAt ?? e.StartsAt,
            AttemptedAt = now,
            Location = location,
            InstanceId = instance.Id,
        });

        await context.SaveChangesAsync(Ct);
        return InstanceJoinLink.For(location)!;
    }

    // ── Sending ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADueReminder_IsSentOnce_WithTheTitleAndTheStart_AndNoJoinForAMembersOnlyEvent()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, await PassAsync(services, gateway));

        // "**Title** starts <t:f> (<t:R>)." and nothing else: no Stop (it has nothing left to stop).
        var message = Assert.Single(gateway.DirectMessages);
        Assert.Equal("999", message.UserId);
        Assert.Equal($"**Movie night** starts <t:{Unix(e.StartsAt)}:f> (<t:{Unix(e.StartsAt)}:R>).", message.Text);
        Assert.Empty(message.Links);
        Assert.Empty(gateway.ActionMessages);

        var sent = await ReminderAsync(services, reminder.Id);
        Assert.Equal(EventReminderStates.Sent, sent.State);
        Assert.Equal(services.Clock.UtcNow, sent.SentAt);
        Assert.Equal(services.Clock.UtcNow, sent.UpdatedAt);

        // And not again, however many passes follow.
        Assert.Equal(0, await PassAsync(services, gateway));
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task TheTitleIsShownAsText_NotAsMarkdown()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "*loud* @everyone", TimeSpan.FromHours(2));
        await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromHours(1));
        await PassAsync(services, gateway);

        Assert.StartsWith("**\\*loud\\* @everyone** starts", Assert.Single(gateway.DirectMessages).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReminderNotDueYet_IsLeftWaiting_AndSendsNothing()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromMinutes(59));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Waiting, (await ReminderAsync(services, reminder.Id)).State);
    }

    [Fact]
    public async Task ACrashAfterTheRowWasMarkedSending_NeverSendsItAgain()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway { DirectMessageCrash = new InvalidOperationException("the process died") };

        services.Clock.Advance(TimeSpan.FromHours(1));

        // The pass blows up after the row was written, as a crash would...
        await Assert.ThrowsAsync<InvalidOperationException>(() => PassAsync(services, gateway));
        Assert.Equal(EventReminderStates.Sending, (await ReminderAsync(services, reminder.Id)).State);

        // ...and the next pass, after the restart, finds it sending and leaves it alone: counted as
        // sent, never twice.
        gateway.DirectMessageCrash = null;
        Assert.Equal(0, await PassAsync(services, gateway));
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await PassAsync(services, gateway));

        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Sending, (await ReminderAsync(services, reminder.Id)).State);
    }

    [Fact]
    public async Task ARowAlreadySending_IsNotPickedUp()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        await AddReminderAsync(services, e, state: EventReminderStates.Sending);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
    }

    [Fact]
    public async Task AStoppedReminder_IsNeverSent()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        await AddReminderAsync(services, e, state: EventReminderStates.Stopped);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
    }

    [Fact]
    public async Task ADirectMessageThatDiscordRefuses_IsFailed_AndNotTriedAgain()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Equal(EventReminderStates.Failed, (await ReminderAsync(services, reminder.Id)).State);

        gateway.DirectMessagesClosed = false;
        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
    }

    // ── The date as it stands now ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ADateMovedLater_IsFollowed_TheReminderWaitsForTheNewTime_AndSaysTheNewStartAndTitle()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        // Moved an hour later, with a title of its own, after the reminder was asked for.
        var movedTo = e.StartsAt.AddHours(1);
        await ChangeDateAsync(services, e, c => (c.StartsAt, c.Title) = (movedTo, "Movie night, later"));

        // The time it was first worked out for: not due any more.
        services.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);

        var waiting = await ReminderAsync(services, reminder.Id);
        Assert.Equal(EventReminderStates.Waiting, waiting.State);
        Assert.Equal(movedTo - TimeSpan.FromHours(1), waiting.RemindAt);

        // An hour on it is due, and says where the date is now.
        services.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await PassAsync(services, gateway));
        Assert.Equal(
            $"**Movie night, later** starts <t:{Unix(movedTo)}:f> (<t:{Unix(movedTo)}:R>).",
            Assert.Single(gateway.DirectMessages).Text);
    }

    [Fact]
    public async Task ADateMovedEarlier_BringsTheReminderForward()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(3));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        // Three hours away became ninety minutes away: an hour before is thirty minutes from now,
        // long before the time the reminder was stored with (two hours from now).
        var movedTo = e.StartsAt.AddMinutes(-90);
        await ChangeDateAsync(services, e, c => c.StartsAt = movedTo);

        services.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, await PassAsync(services, gateway));

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await PassAsync(services, gateway));

        Assert.Contains($"<t:{Unix(movedTo)}:f>", Assert.Single(gateway.DirectMessages).Text, StringComparison.Ordinal);
        Assert.Equal(EventReminderStates.Sent, (await ReminderAsync(services, reminder.Id)).State);
    }

    [Fact]
    public async Task ADateMovedToBeforeNow_IsSkipped_ItHasStartedAlready()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        await ChangeDateAsync(services, e, c => c.StartsAt = services.Clock.UtcNow.AddMinutes(-5));
        services.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Skipped, (await ReminderAsync(services, reminder.Id)).State);
    }

    [Fact]
    public async Task ACancelledDate_IsSkipped_AndSendsNothing()
    {
        await using var services = await SetUpAsync(_db);
        var weekly = await AddEventAsync(services, "Weekly", TimeSpan.FromHours(2), e => e.Repeat = CalendarRepeats.Weekly);
        var first = await AddReminderAsync(services, weekly);
        var second = await AddReminderAsync(services, weekly, "888", shape: r =>
        {
            // The next week's date, which is not cancelled.
            r.OccurrenceStartsAt = weekly.StartsAt.AddDays(7);
            r.RemindAt = r.OccurrenceStartsAt - TimeSpan.FromHours(1);
        });
        var gateway = new FakeGateway();

        await ChangeDateAsync(services, weekly, c => c.Cancelled = true);
        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Skipped, (await ReminderAsync(services, first.Id)).State);
        Assert.Equal(EventReminderStates.Waiting, (await ReminderAsync(services, second.Id)).State);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("finished")]
    [InlineData("draft")]
    [InlineData("deleted")]
    public async Task AnEventThatWasCancelledFinishedUnpublishedOrDeleted_IsSkipped(string what)
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        await SetEventAsync(services, e.Id, x =>
        {
            switch (what)
            {
                case "cancelled":
                    (x.State, x.CancelledAt) = (CalendarEventStates.Cancelled, services.Clock.UtcNow);
                    break;
                case "finished":
                    x.State = CalendarEventStates.Finished;
                    break;
                case "draft":
                    x.State = CalendarEventStates.Draft;
                    break;
                default:
                    x.DeletedAt = services.Clock.UtcNow;
                    break;
            }
        });

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Skipped, (await ReminderAsync(services, reminder.Id)).State);
    }

    // ── Late ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    [InlineData(120, false)]
    public async Task AReminderMoreThan15MinutesLate_IsNotSent_AndOneWithinItIs(int minutesLate, bool sent)
    {
        await using var services = await SetUpAsync(_db);

        // A day before a start 25 hours away is an hour from now. The start is a long way off, so
        // lateness is the only reason it could be held back.
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(25));
        var reminder = await AddReminderAsync(services, e, minutes: 1440);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(minutesLate));

        Assert.Equal(sent ? 1 : 0, await PassAsync(services, gateway));
        Assert.Equal(sent ? 1 : 0, gateway.DirectMessages.Count);
        Assert.Equal(
            sent ? EventReminderStates.Sent : EventReminderStates.Skipped,
            (await ReminderAsync(services, reminder.Id)).State);
    }

    [Fact]
    public async Task ABotThatWasOffForHours_SendsNothingForWhatItMissed()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(3));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        // Due two hours ago, and the event has since started.
        services.Clock.Advance(TimeSpan.FromHours(4));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Skipped, (await ReminderAsync(services, reminder.Id)).State);
    }

    // ── Pacing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMessagesArePacedTwoSecondsApart_TheSameAsTheCalendarsInvites()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), EventReminderMessages.Between);
        Assert.Equal(CalendarInviteMessages.Between, EventReminderMessages.Between);
        Assert.Equal(5, EventReminderMessages.PerPass);
    }

    [Fact]
    public async Task TwoSecondsAreWaitedBetweenMessages_AndNotBeforeTheFirst()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));

        foreach (var user in new[] { "100", "200", "300" })
            await AddReminderAsync(services, e, user);

        var gateway = new FakeGateway();
        var delays = new List<TimeSpan>();

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(3, await PassAsync(services, gateway, delays));
        Assert.Equal(3, gateway.DirectMessages.Count);

        // Three messages, two pauses of two seconds: none before the first.
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task AtMostFiveGoOutInAPass_TheRestInTheNext()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));

        for (var i = 0; i < 7; i++)
            await AddReminderAsync(services, e, (1000 + i).ToString(CultureInfo.InvariantCulture));

        var gateway = new FakeGateway();
        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(5, await PassAsync(services, gateway));
        Assert.Equal(2, await PassAsync(services, gateway));
        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Equal(7, gateway.DirectMessages.Select(m => m.UserId).Distinct().Count());
    }

    // ── The Join link ───────────────────────────────────────────────────────────────────────

    /// <summary>An event whose instance opens half an hour early, so it is open though it has not started.</summary>
    private static Task<CalendarEvent> OpenSoonAsync(TestServices services, string access)
        => AddEventAsync(services, "Movie night", TimeSpan.FromMinutes(20), e =>
        {
            e.AutoOpen = true;
            e.OpenMinutesBefore = 30;
            e.AccessType = access;
        });

    [Fact]
    public async Task AnEventAnyoneCanJoin_WithItsInstanceOpen_GetsAJoinLink()
    {
        await using var services = await SetUpAsync(_db);
        var e = await OpenSoonAsync(services, "public");
        Assert.Equal(CalendarEventStates.Open, e.State);
        var link = await OpenInstanceAsync(services, e);
        await AddReminderAsync(services, e, minutes: 15);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, await PassAsync(services, gateway));

        var button = Assert.Single(Assert.Single(gateway.DirectMessages).Links);
        Assert.Equal("Join", button.Label);
        Assert.Equal(link, button.Url);
        Assert.StartsWith("https://vrchat.com/home/launch?", button.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("plus")]
    public async Task AnEventOnlyMembersCanJoin_GetsNoJoinLink_EvenWithItsInstanceOpen(string access)
    {
        await using var services = await SetUpAsync(_db);
        var e = await OpenSoonAsync(services, access);
        await OpenInstanceAsync(services, e);
        await AddReminderAsync(services, e, minutes: 15);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, await PassAsync(services, gateway));
        Assert.Empty(Assert.Single(gateway.DirectMessages).Links);
    }

    [Fact]
    public async Task APublicEvent_WithNoInstanceYet_OrOneThatClosed_GetsNoJoinLink()
    {
        await using var services = await SetUpAsync(_db);

        // Open by the clock, but Modbot opened nothing.
        var nothing = await OpenSoonAsync(services, "public");
        await AddReminderAsync(services, nothing, "100", minutes: 15);

        // Opened, then closed.
        var closed = await OpenSoonAsync(services, "public");
        await OpenInstanceAsync(services, closed, closed: true);
        await AddReminderAsync(services, closed, "200", minutes: 15);

        // Not open yet: it opens at its start, which is still ahead.
        var notYet = await AddEventAsync(services, "Not yet", TimeSpan.FromMinutes(20), e => e.AccessType = "public");
        Assert.Equal(CalendarEventStates.Scheduled, notYet.State);
        await AddReminderAsync(services, notYet, "300", minutes: 15);

        var gateway = new FakeGateway();
        services.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(3, await PassAsync(services, gateway));
        Assert.All(gateway.DirectMessages, m => Assert.Empty(m.Links));
    }

    // ── The operator's switch ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhileTheCommandIsOff_NothingIsSent_AndTurnedOnAgainItSendsWhatIsStillInTime()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromHours(2));
        var reminder = await AddReminderAsync(services, e);
        var gateway = new FakeGateway();

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await PassAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(EventReminderStates.Waiting, (await ReminderAsync(services, reminder.Id)).State);

        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.RemindMe, true), Ct);
        services.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, await PassAsync(services, gateway));
    }

    // ── Keeping them ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SentStoppedSkippedAndFailedRows_AreDeletedAfter30Days_AndTheOthersAreKept()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(60));
        var now = services.Clock.UtcNow;

        var old = new List<EventReminder>();
        var user = 0;

        foreach (var state in new[] { EventReminderStates.Sent, EventReminderStates.Stopped, EventReminderStates.Skipped, EventReminderStates.Failed })
        {
            old.Add(await AddReminderAsync(services, e, (user++).ToString(CultureInfo.InvariantCulture), state: state, shape: r => r.UpdatedAt = now - TimeSpan.FromDays(31)));
        }

        var recent = await AddReminderAsync(services, e, "500", state: EventReminderStates.Sent, shape: r => r.UpdatedAt = now - TimeSpan.FromDays(29));

        // Not finished: kept however old.
        var waiting = await AddReminderAsync(services, e, "600", shape: r => r.UpdatedAt = now - TimeSpan.FromDays(90));
        var sending = await AddReminderAsync(services, e, "700", state: EventReminderStates.Sending, shape: r => r.UpdatedAt = now - TimeSpan.FromDays(90));

        await PassAsync(services, new FakeGateway());

        await using var context = services.Database.NewContext();
        var left = await context.EventReminders.AsNoTracking().Select(r => r.Id).ToListAsync(Ct);

        Assert.DoesNotContain(left, id => old.Any(o => o.Id == id));
        Assert.Contains(recent.Id, left);
        Assert.Contains(waiting.Id, left);
        Assert.Contains(sending.Id, left);
        Assert.Equal(3, left.Count);
        Assert.Equal(TimeSpan.FromDays(30), EventReminderMessages.KeptFor);
    }

    [Fact]
    public async Task OldRowsAreDeleted_EvenWhileTheCommandIsOff()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(60));
        var now = services.Clock.UtcNow;
        await AddReminderAsync(services, e, state: EventReminderStates.Stopped, shape: r => r.UpdatedAt = now - TimeSpan.FromDays(31));

        await PassAsync(services, new FakeGateway());

        await using var context = services.Database.NewContext();
        Assert.Empty(await context.EventReminders.ToListAsync(Ct));
    }

    [Fact]
    public async Task DeletingTheEvent_TakesItsRemindersWithIt()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(3));
        await AddReminderAsync(services, e);

        await using var context = services.Database.NewContext();
        await context.CalendarEvents.Where(x => x.Id == e.Id).ExecuteDeleteAsync(Ct);

        Assert.Empty(await context.EventReminders.ToListAsync(Ct));
    }
}
