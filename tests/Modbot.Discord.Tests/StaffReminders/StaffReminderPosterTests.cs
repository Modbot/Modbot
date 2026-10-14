using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.StaffReminders;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.StaffReminders;

/// <summary>
/// The day-30 reminder for the Council and the 30-day check-in for quiet staff: when each is due, that each goes out
/// once, and that nothing goes out while no channel is chosen.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffReminderPosterTests
{
    private const string Channel = "1234567890";

    private readonly PostgresFixture _db;

    public StaffReminderPosterTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task ChooseChannelAsync(TestServices services)
        => services.ConfigureAsync(s => s.StaffRemindersChannelId = Channel, Ct);

    /// <summary>An account that signed in <paramref name="signedInDaysAgo"/> days ago, so it is quiet from the 30th day on.</summary>
    private static async Task<ModbotUser> AddUserAsync(TestServices services, string name, int signedInDaysAgo = 0)
    {
        var at = services.Clock.UtcNow.AddDays(-signedInDaysAgo);

        var user = new ModbotUser
        {
            Username = name,
            UsernameNormalized = name.ToUpperInvariant(),
            PasswordHash = "x",
            CreatedAt = at.AddDays(-100),
            LastLoginAt = at,
        };

        await using var context = services.Database.NewContext();
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private static async Task SignInAsync(TestServices services, Guid userId)
    {
        await using var context = services.Database.NewContext();
        var user = await context.Users.SingleAsync(u => u.Id == userId, Ct);
        user.LastLoginAt = services.Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<StaffTrial> AddTrialAsync(
        TestServices services, ModbotUser account, ModbotUser buddy, int startedDaysAgo, Action<StaffTrial>? change = null)
    {
        var started = services.Clock.UtcNow.AddDays(-startedDaysAgo);

        var trial = new StaffTrial
        {
            AccountId = account.Id,
            BuddyId = buddy.Id,
            StartedAt = started,
            BuddyNote = "Quick to learn the door.",
            Items =
            [
                new StaffTrialItem { Kind = StaffTrialItemKinds.Task, Text = "Read the rules", Position = 0, Answer = "Done", AnsweredAt = started },
                new StaffTrialItem { Kind = StaffTrialItemKinds.Task, Text = "Find the logs", Position = 1 },
                new StaffTrialItem { Kind = StaffTrialItemKinds.SignOff, Text = "Joined a shift", Position = 0, SignedOffAt = started },
            ],
        };

        change?.Invoke(trial);

        await using var context = services.Database.NewContext();
        context.StaffTrials.Add(trial);
        await context.SaveChangesAsync(Ct);
        return trial;
    }

    /// <summary>One date of an event, with the account on it in the given role.</summary>
    private static async Task WorkEventAsync(
        TestServices services, ModbotUser account, int startedDaysAgo, string role = CalendarInviteRoles.Staff)
    {
        var at = services.Clock.UtcNow.AddDays(-startedDaysAgo);

        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Friday night",
            Description = "Plain words",
            StartsAt = at,
            EndsAt = at.AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
        };

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(calendarEvent);
        context.CalendarInvites.Add(new CalendarInvite
        {
            EventId = calendarEvent.Id,
            OccurrenceStartsAt = at,
            Role = role,
            StaffUserId = account.Id,
            PersonKey = $"staff:{account.Id}",
            QueuedAt = at,
            UpdatedAt = at,
        });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<StaffReminderPass> RunAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new StaffReminderPoster(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    private static async Task<StaffTrial> ReadAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        return await context.StaffTrials.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
    }

    private static string Field(DiscordEmbedContent card, string name)
        => Assert.Single(card.Fields, f => f.Name == name).Value;

    [Fact]
    public async Task TheCouncilReminder_IsNotDueBeforeTheThirtiethDay()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var person = await AddUserAsync(services, "wanda");
        var buddy = await AddUserAsync(services, "casey");
        await AddTrialAsync(services, person, buddy, startedDaysAgo: 29);

        var pass = await RunAsync(services, gateway);

        Assert.Equal(0, pass.Council);
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task TheCouncilReminder_IsPostedOnceOnTheThirtiethDay_WithTheChecklistEventsAndBuddyNote()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var person = await AddUserAsync(services, "wanda");
        var buddy = await AddUserAsync(services, "casey");
        var trial = await AddTrialAsync(services, person, buddy, startedDaysAgo: 30);

        // Two dates worked since the trial started, as host and as staff; one before it started, and one still to come.
        await WorkEventAsync(services, person, startedDaysAgo: 10, CalendarInviteRoles.Host);
        await WorkEventAsync(services, person, startedDaysAgo: 3, CalendarInviteRoles.Staff);
        await WorkEventAsync(services, person, startedDaysAgo: 40);
        await WorkEventAsync(services, person, startedDaysAgo: -2);

        Assert.Equal(1, (await RunAsync(services, gateway)).Council);
        Assert.Equal(0, (await RunAsync(services, gateway)).Council);

        var (channel, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(Channel, channel);

        var card = Assert.Single(embeds);
        Assert.Equal("Day 30: wanda", card.Title);
        Assert.Equal("1 of 2", Field(card, "Tasks answered"));
        Assert.Equal("1 of 1", Field(card, "Lines signed off"));
        Assert.Equal("2", Field(card, "Events worked"));
        Assert.Equal("casey", Field(card, "Buddy"));
        Assert.Equal("Quick to learn the door.", Field(card, "Buddy note"));
        Assert.Contains(person.Id.ToString(), card.Url);

        Assert.NotNull((await ReadAsync(services, trial.Id)).CouncilRemindedAt);
    }

    [Fact]
    public async Task TheCouncilReminder_IsSkippedForAClosedTrial()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var person = await AddUserAsync(services, "wanda");
        var buddy = await AddUserAsync(services, "casey");
        await AddTrialAsync(services, person, buddy, startedDaysAgo: 40, t => t.ClosedAt = services.Clock.UtcNow.AddDays(-1));

        var pass = await RunAsync(services, gateway);

        Assert.Equal(0, pass.Council);
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task WithNoChannel_NothingIsPostedOrMarked_AndItGoesOutOnceOneIsChosen()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var person = await AddUserAsync(services, "wanda");
        var buddy = await AddUserAsync(services, "casey");
        await AddUserAsync(services, "robin", signedInDaysAgo: 45);
        var trial = await AddTrialAsync(services, person, buddy, startedDaysAgo: 31);

        var off = await RunAsync(services, gateway);

        Assert.Equal(new StaffReminderPass(0, 0, null), off);
        Assert.Empty(gateway.Posts);
        Assert.Null((await ReadAsync(services, trial.Id)).CouncilRemindedAt);

        await ChooseChannelAsync(services);
        var on = await RunAsync(services, gateway);

        Assert.Equal(1, on.Council);
        Assert.Equal(1, on.CheckIns);
        Assert.Equal(2, gateway.Posts.Count);
    }

    [Fact]
    public async Task ARefusedPost_StopsThePass_AndIsTriedAgainNextTime()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var person = await AddUserAsync(services, "wanda");
        var buddy = await AddUserAsync(services, "casey");
        var trial = await AddTrialAsync(services, person, buddy, startedDaysAgo: 30);

        gateway.FailNextPost("Missing Access");
        var refused = await RunAsync(services, gateway);

        Assert.Equal(0, refused.Council);
        Assert.Equal("Missing Access", refused.Error);
        Assert.Null((await ReadAsync(services, trial.Id)).CouncilRemindedAt);

        var again = await RunAsync(services, gateway);

        Assert.Equal(1, again.Council);
        Assert.Null(again.Error);
        Assert.Single(gateway.Posts);
    }

    [Fact]
    public async Task ACheckIn_IsDueAfterThirtyQuietDays_WithTheNameAndTheLastActiveDate()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        await AddUserAsync(services, "robin", signedInDaysAgo: 29);
        await AddUserAsync(services, "jessie", signedInDaysAgo: 31);

        var pass = await RunAsync(services, gateway);

        Assert.Equal(1, pass.CheckIns);

        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.Equal("Check-in: 1 person", card.Title);
        Assert.Contains("jessie: last active 1 Dec 2025", card.Description);
        Assert.DoesNotContain("robin", card.Description);
    }

    [Fact]
    public async Task CheckIns_AreOneCombinedPost_ForEveryoneDue()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        await AddUserAsync(services, "robin", signedInDaysAgo: 400);
        await AddUserAsync(services, "jessie", signedInDaysAgo: 90);
        await AddUserAsync(services, "casey", signedInDaysAgo: 45);
        await AddUserAsync(services, "wanda", signedInDaysAgo: 1);

        var pass = await RunAsync(services, gateway);

        Assert.Equal(3, pass.CheckIns);

        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.Equal("Check-in: 3 people", card.Title);
        Assert.Equal(3, card.Description!.Split('\n').Length);
        Assert.DoesNotContain("wanda", card.Description);

        Assert.Equal(0, (await RunAsync(services, gateway)).CheckIns);
        Assert.Single(gateway.Posts);
    }

    [Fact]
    public async Task ACheckIn_IsNotRepeatedUntilTheAccountIsActiveAndThenQuietAgain()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var robin = await AddUserAsync(services, "robin", signedInDaysAgo: 31);

        Assert.Equal(1, (await RunAsync(services, gateway)).CheckIns);

        // Still quiet a month on: no second reminder for the same stretch.
        services.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(0, (await RunAsync(services, gateway)).CheckIns);

        // Signs in, then goes quiet for 30 days again.
        await SignInAsync(services, robin.Id);
        services.Clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, (await RunAsync(services, gateway)).CheckIns);

        services.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await RunAsync(services, gateway)).CheckIns);
        Assert.Equal(2, gateway.Posts.Count);
    }

    [Fact]
    public async Task AModerationActionInModbot_CountsAsActive()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var robin = await AddUserAsync(services, "robin", signedInDaysAgo: 60);

        await services.WriteFactAsync(
            new FactRecord
            {
                Type = FactType.MemberBanned,
                OccurredAt = services.Clock.UtcNow.AddDays(-3),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = "usr_00000000-0000-0000-0000-000000000001",
                ActorPlatform = FactPlatform.Modbot,
                ActorId = robin.Id.ToString(),
                Source = FactSource.Manual,
            }, Ct);

        var pass = await RunAsync(services, gateway);

        Assert.Equal(0, pass.CheckIns);
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task AnEventWorkedAsHostOrStaff_CountsAsActive_ButOneStillToComeDoesNot()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var worked = await AddUserAsync(services, "robin", signedInDaysAgo: 60);
        var comingUp = await AddUserAsync(services, "jessie", signedInDaysAgo: 60);

        await WorkEventAsync(services, worked, startedDaysAgo: 3, CalendarInviteRoles.Host);
        await WorkEventAsync(services, comingUp, startedDaysAgo: -3);

        var pass = await RunAsync(services, gateway);

        Assert.Equal(1, pass.CheckIns);

        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.Contains("jessie", card.Description);
        Assert.DoesNotContain("robin", card.Description);
    }

    [Fact]
    public async Task ACheckInThatWouldBeTooLongForACard_ListsWhatFits_AndMarksOnlyThosePeople()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await services.ConfigureAsync(s =>
        {
            s.StaffRemindersChannelId = Channel;
            s.PublicAddress = "https://modbot.example.com";
        }, Ct);

        // Forty linked lines of about 150 characters each: more than the 4096 a card's description holds.
        for (var i = 0; i < 45; i++)
            await AddUserAsync(services, $"{new string('a', 40)}{i:00}", signedInDaysAgo: 100 + i);

        var first = await RunAsync(services, gateway);

        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.InRange(card.Description!.Length, 1, 4096);
        Assert.InRange(first.CheckIns, 1, StaffReminderPoster.CheckInsPerPost - 1);
        Assert.Equal($"Check-in: {first.CheckIns} people", card.Title);
        Assert.Equal(first.CheckIns, card.Description.Split('\n').Count(l => l != "…"));
        Assert.Equal(first.CheckIns, await MarkedAsync(services));

        // The people left off are listed in the next posts, and nobody is listed twice.
        var listed = first.CheckIns;
        for (var pass = 0; pass < 10 && listed < 45; pass++)
            listed += (await RunAsync(services, gateway)).CheckIns;

        Assert.Equal(45, listed);
        Assert.Equal(45, await MarkedAsync(services));
        Assert.All(gateway.Posts, p => Assert.InRange(Assert.Single(p.Embeds).Description!.Length, 1, 4096));
    }

    private static async Task<int> MarkedAsync(TestServices services)
    {
        await using var context = services.Database.NewContext();
        return await context.Users.CountAsync(u => u.CheckInRemindedAt != null, Ct);
    }

    [Fact]
    public async Task ACouncilCardDiscordRefusesForGood_LeavesTheOtherTrialsAndTheCheckInsAGo()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var buddy = await AddUserAsync(services, "casey");
        var first = await AddTrialAsync(services, await AddUserAsync(services, "wanda"), buddy, startedDaysAgo: 35);
        var second = await AddTrialAsync(services, await AddUserAsync(services, "robin"), buddy, startedDaysAgo: 31);
        await AddUserAsync(services, "jessie", signedInDaysAgo: 50);

        gateway.FailNextPost("Cannot send this card", permanent: true);
        var pass = await RunAsync(services, gateway);

        Assert.Equal("Cannot send this card", pass.Error);
        Assert.Equal(1, pass.Council);
        Assert.Equal(1, pass.CheckIns);
        Assert.Null((await ReadAsync(services, first.Id)).CouncilRemindedAt);
        Assert.NotNull((await ReadAsync(services, second.Id)).CouncilRemindedAt);
    }

    [Fact]
    public async Task ARefusalThatIsNotFinal_StopsThePass_ForEveryKind()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await ChooseChannelAsync(services);
        var buddy = await AddUserAsync(services, "casey");
        await AddTrialAsync(services, await AddUserAsync(services, "wanda"), buddy, startedDaysAgo: 35);
        var second = await AddTrialAsync(services, await AddUserAsync(services, "robin"), buddy, startedDaysAgo: 31);
        await AddUserAsync(services, "jessie", signedInDaysAgo: 50);

        gateway.FailNextPost("Discord is busy");
        var pass = await RunAsync(services, gateway);

        Assert.Equal(new StaffReminderPass(0, 0, "Discord is busy"), pass);
        Assert.Empty(gateway.Posts);
        Assert.Null((await ReadAsync(services, second.Id)).CouncilRemindedAt);
    }
}
