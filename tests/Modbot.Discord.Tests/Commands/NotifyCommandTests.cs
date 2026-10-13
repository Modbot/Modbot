using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/notify</c> and the Notify me button (event alerts design §2): a form of the Modbot categories
/// with the member's own ticked, the way only when both ways are on, sign-ups replaced by what is
/// ticked, the button signing up for an event's categories or the event and undoing on a second
/// press, private replies, and one per-person limit.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class NotifyCommandTests
{
    private const string Channel = "222222222222222222";
    private const string Member = "999";

    private readonly PostgresFixture _db;

    public NotifyCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, bool on = true, bool direct = true, bool ping = false)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.EventAlertsOn = on;
            s.EventAlertsDirectMessage = direct;
            s.EventAlertsPing = ping;
            s.EventAlertsChannelId = ping ? Channel : null;
        }, Ct);
        return services;
    }

    private static async Task<CalendarCategory> AddCategoryAsync(TestServices services, string name)
    {
        await using var context = services.Database.NewContext();
        var category = new CalendarCategory
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        };
        context.CalendarCategories.Add(category);
        await context.SaveChangesAsync(Ct);
        return category;
    }

    /// <summary>A scheduled event with its post up, as <c>/events</c> would list it, carrying these categories.</summary>
    private static async Task<CalendarEvent> AddEventAsync(
        TestServices services, string title, bool listed = true, params Guid[] categories)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            StartsAt = now.AddHours(5),
            EndsAt = now.AddHours(7),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            PublishToDiscord = false,
            PostToChannel = true,
            ChannelId = Channel,
            AccessType = "members",
            CreatedAt = now,
            UpdatedAt = now,
        };
        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();

        if (categories.Length > 0)
            e.Categories = await context.CalendarCategories.Where(c => categories.Contains(c.Id)).ToListAsync(Ct);

        context.CalendarEvents.Add(e);

        if (listed)
        {
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = e.Id,
                Place = CalendarPlaces.ChannelPost,
                State = CalendarPlaceStates.Published,
                ExternalId = "5551234",
                ChannelId = Channel,
                OccurrenceStartsAt = e.OccurrenceStartsAt,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static async Task<List<EventAlertSignup>> SignupsAsync(TestServices services, string user = Member)
    {
        await using var context = services.Database.NewContext();
        return await context.EventAlertSignups.AsNoTracking().Where(s => s.DiscordUserId == user).ToListAsync(Ct);
    }

    private static async Task AddSignupAsync(TestServices services, Guid? category, Guid? eventId, string user = Member)
    {
        await using var context = services.Database.NewContext();
        context.EventAlertSignups.Add(new EventAlertSignup
        {
            DiscordUserId = user,
            CategoryId = category,
            EventId = eventId,
            CreatedAt = services.Clock.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    private static DiscordCommandCall Call(string user, Action<DiscordForm>? shown = null)
        => new(
            user,
            "someone",
            DiscordCommands.Notify,
            new Dictionary<string, string>(),
            (_, _) => Task.CompletedTask,
            (form, _) =>
            {
                shown?.Invoke(form);
                return Task.CompletedTask;
            });

    private static async Task<(DiscordReply? Reply, DiscordForm? Form)> RunAsync(TestServices services, string user = Member)
    {
        DiscordForm? shown = null;
        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<NotifyCommand>()
            .RunAsync(Call(user, f => shown = f), Ct);
        return (reply, shown);
    }

    private static async Task<DiscordReply> SubmitAsync(
        TestServices services, IEnumerable<Guid> categories, string? way = null, string user = Member)
    {
        var values = new Dictionary<string, IReadOnlyList<string>>
        {
            [NotifyCommand.CategoriesField] = [.. categories.Select(c => c.ToString("D"))],
        };

        if (way is not null)
            values[NotifyCommand.WayField] = [way];

        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<NotifyCommand>().HandleFormAsync(
            new DiscordFormSubmit(user, "someone", NotifyCommand.FormId, values, (_, _) => Task.CompletedTask), Ct);
    }

    private static async Task<DiscordReply> PressAsync(TestServices services, Guid eventId, string user = Member)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<NotifyCommand>().HandleButtonAsync(
            new DiscordButtonPress(user, "someone", NotifyCommand.ButtonFor(eventId).Id, (_, _) => Task.CompletedTask), Ct);
    }

    // ── /notify ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithAlertsOff_NoFormIsShown_AndTheMemberIsTold()
    {
        await using var services = await SetUpAsync(_db, on: false);
        await AddCategoryAsync(services, "Karaoke");

        var (reply, form) = await RunAsync(services);

        Assert.Equal(NotifyCommand.OffMessage, reply?.Text);
        Assert.Null(form);
    }

    [Fact]
    public async Task WithNoCategories_NoFormIsShown_AndTheMemberIsTold()
    {
        await using var services = await SetUpAsync(_db);

        var (reply, form) = await RunAsync(services);

        Assert.Equal(NotifyCommand.NoCategoriesMessage, reply?.Text);
        Assert.Null(form);
    }

    [Fact]
    public async Task TheForm_ListsTheCategoriesByName_WithTheMembersOwnTicked_AndNoWayWhenOnlyOneIsOn()
    {
        await using var services = await SetUpAsync(_db);
        var movie = await AddCategoryAsync(services, "Movie night");
        var karaoke = await AddCategoryAsync(services, "Karaoke");
        await AddSignupAsync(services, movie.Id, null);

        var (reply, form) = await RunAsync(services);

        Assert.Null(reply);
        Assert.NotNull(form);
        Assert.Equal(NotifyCommand.FormId, form.Id);

        var field = Assert.Single(form.Fields);
        Assert.Equal(NotifyCommand.CategoriesField, field.Id);
        Assert.Equal(["Karaoke", "Movie night"], field.Choices!.Select(c => c.Label));
        Assert.Equal([false, true], field.Choices!.Select(c => c.IsDefault));
        Assert.Equal(karaoke.Id.ToString("D"), field.Choices![0].Value);
    }

    [Fact]
    public async Task WithBothWaysOn_TheFormAlsoAsksForTheWay_WithTheMembersOwnChosen()
    {
        await using var services = await SetUpAsync(_db, direct: true, ping: true);
        await AddCategoryAsync(services, "Karaoke");

        await using (var context = services.Database.NewContext())
        {
            context.EventAlertMembers.Add(new EventAlertMember
            {
                DiscordUserId = Member,
                Way = EventAlertSettings.Ping,
                UpdatedAt = services.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var (_, form) = await RunAsync(services);

        Assert.Equal([NotifyCommand.CategoriesField, NotifyCommand.WayField], form!.Fields.Select(f => f.Id));
        var way = form.Fields[1];
        Assert.True(way.Required);
        Assert.Equal([EventAlertSettings.Direct, EventAlertSettings.Ping], way.Choices!.Select(c => c.Value));
        Assert.Equal([false, true], way.Choices!.Select(c => c.IsDefault));
    }

    [Fact]
    public async Task ANewMember_GetsTheDirectWayTicked()
    {
        await using var services = await SetUpAsync(_db, direct: true, ping: true);
        await AddCategoryAsync(services, "Karaoke");

        var (_, form) = await RunAsync(services);

        Assert.Equal([true, false], form!.Fields[1].Choices!.Select(c => c.IsDefault));
    }

    [Fact]
    public async Task OnlyTheFirst25CategoriesByName_AreOffered()
    {
        await using var services = await SetUpAsync(_db);
        for (var i = 0; i < 27; i++)
            await AddCategoryAsync(services, $"Category {i:00}");

        var (_, form) = await RunAsync(services);

        var choices = form!.Fields[0].Choices!;
        Assert.Equal(NotifyCommand.MostCategories, choices.Count);
        Assert.Equal("Category 00", choices[0].Label);
        Assert.Equal("Category 24", choices[^1].Label);
    }

    [Fact]
    public async Task SendingTheForm_ReplacesTheCategorySignUps_WithWhatIsTicked_AndLeavesSingleEventsAlone()
    {
        await using var services = await SetUpAsync(_db);
        var karaoke = await AddCategoryAsync(services, "Karaoke");
        var movie = await AddCategoryAsync(services, "Movie night");
        var quiz = await AddCategoryAsync(services, "Quiz");
        var single = await AddEventAsync(services, "Game night");
        await AddSignupAsync(services, karaoke.Id, null);
        await AddSignupAsync(services, null, single.Id);
        await AddSignupAsync(services, quiz.Id, null, user: "somebody else");

        var reply = await SubmitAsync(services, [movie.Id]);

        var mine = await SignupsAsync(services);
        Assert.Equal(
            new HashSet<Guid?> { movie.Id, null },
            mine.Select(s => s.CategoryId).ToHashSet());
        Assert.Contains(mine, s => s.EventId == single.Id);
        Assert.Single(await SignupsAsync(services, "somebody else"));
        Assert.Contains("Movie night", reply.Text);
        Assert.DoesNotContain("Karaoke", reply.Text);
    }

    [Fact]
    public async Task SendingTheFormWithNothingTicked_TakesEveryCategoryOff_AndSaysSo()
    {
        await using var services = await SetUpAsync(_db);
        var karaoke = await AddCategoryAsync(services, "Karaoke");
        await AddSignupAsync(services, karaoke.Id, null);

        var reply = await SubmitAsync(services, []);

        Assert.Empty(await SignupsAsync(services));
        Assert.Equal(NotifyCommand.NothingMessage, reply.Text);
    }

    [Fact]
    public async Task SendingTheForm_IgnoresAnIdThatWasNotOnOffer()
    {
        await using var services = await SetUpAsync(_db);
        var karaoke = await AddCategoryAsync(services, "Karaoke");

        await SubmitAsync(services, [karaoke.Id, Guid.CreateVersion7()]);

        var mine = await SignupsAsync(services);
        Assert.Equal(karaoke.Id, Assert.Single(mine).CategoryId);
    }

    [Fact]
    public async Task SendingTheFormTwice_DoesNotDoubleTheSignUps()
    {
        await using var services = await SetUpAsync(_db);
        var karaoke = await AddCategoryAsync(services, "Karaoke");

        await SubmitAsync(services, [karaoke.Id]);
        await SubmitAsync(services, [karaoke.Id]);

        Assert.Single(await SignupsAsync(services));
    }

    [Fact]
    public async Task SendingTheFormWithAWay_StoresIt_WhenBothWaysAreOn()
    {
        await using var services = await SetUpAsync(_db, direct: true, ping: true);
        var karaoke = await AddCategoryAsync(services, "Karaoke");

        var first = await SubmitAsync(services, [karaoke.Id], EventAlertSettings.Ping);
        var second = await SubmitAsync(services, [karaoke.Id], EventAlertSettings.Direct);

        await using var context = services.Database.NewContext();
        var row = await context.EventAlertMembers.AsNoTracking().SingleAsync(m => m.DiscordUserId == Member, Ct);
        Assert.Equal(EventAlertSettings.Direct, row.Way);
        Assert.Contains("ping", first.Text);
        Assert.Contains("direct message", second.Text);
    }

    [Fact]
    public async Task SendingTheFormWithAWay_StoresNothing_WhenOnlyOneWayIsOn()
    {
        await using var services = await SetUpAsync(_db, direct: true, ping: false);
        var karaoke = await AddCategoryAsync(services, "Karaoke");

        await SubmitAsync(services, [karaoke.Id], EventAlertSettings.Ping);

        await using var context = services.Database.NewContext();
        Assert.False(await context.EventAlertMembers.AnyAsync(Ct));
    }

    [Fact]
    public async Task SendingTheForm_WithAlertsOff_ChangesNothing()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var karaoke = await AddCategoryAsync(services, "Karaoke");

        var reply = await SubmitAsync(services, [karaoke.Id]);

        Assert.Equal(NotifyCommand.OffMessage, reply.Text);
        Assert.Empty(await SignupsAsync(services));
    }

    // ── The Notify me button ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheButton_SignsUpForTheEventsCategories_AndASecondPressTakesThemOff()
    {
        await using var services = await SetUpAsync(_db);
        var karaoke = await AddCategoryAsync(services, "Karaoke");
        var movie = await AddCategoryAsync(services, "Movie night");
        var e = await AddEventAsync(services, "Friday fun", true, karaoke.Id, movie.Id);

        var first = await PressAsync(services, e.Id);

        Assert.Equal(
            new[] { karaoke.Id, movie.Id }.Order(),
            (await SignupsAsync(services)).Select(s => s.CategoryId!.Value).Order());
        Assert.All(await SignupsAsync(services), s => Assert.Null(s.EventId));
        Assert.Equal("You will be told before events in Karaoke, Movie night.", first.Text);

        var second = await PressAsync(services, e.Id);

        Assert.Empty(await SignupsAsync(services));
        Assert.Equal("You will no longer be told before events in Karaoke, Movie night.", second.Text);
    }

    [Fact]
    public async Task TheButton_OnAnEventWithNoCategories_SignsUpForTheEvent_AndASecondPressTakesItOff()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Game night");

        var first = await PressAsync(services, e.Id);

        var signup = Assert.Single(await SignupsAsync(services));
        Assert.Equal(e.Id, signup.EventId);
        Assert.Null(signup.CategoryId);
        Assert.Equal("You will be told before Game night.", first.Text);

        var second = await PressAsync(services, e.Id);

        Assert.Empty(await SignupsAsync(services));
        Assert.Equal("You will no longer be told before Game night.", second.Text);
    }

    [Fact]
    public async Task TheButton_SignsUpForTheCategoriesStillMissing_WhenSomeAreAlreadyHeld()
    {
        await using var services = await SetUpAsync(_db);
        var karaoke = await AddCategoryAsync(services, "Karaoke");
        var movie = await AddCategoryAsync(services, "Movie night");
        var e = await AddEventAsync(services, "Friday fun", true, karaoke.Id, movie.Id);
        await AddSignupAsync(services, karaoke.Id, null);

        var reply = await PressAsync(services, e.Id);

        Assert.Equal(2, (await SignupsAsync(services)).Count);
        Assert.StartsWith("You will be told before events in", reply.Text);
    }

    [Fact]
    public async Task TheButton_OnAnEventThatIsNotListed_SignsNobodyUp()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Hidden", listed: false);

        var reply = await PressAsync(services, e.Id);

        Assert.Equal(NotifyCommand.NoEventMessage, reply.Text);
        Assert.Empty(await SignupsAsync(services));
    }

    [Fact]
    public async Task TheButton_WithAlertsOff_SignsNobodyUp()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var e = await AddEventAsync(services, "Game night");

        var reply = await PressAsync(services, e.Id);

        Assert.Equal(NotifyCommand.OffMessage, reply.Text);
        Assert.Empty(await SignupsAsync(services));
    }

    [Fact]
    public async Task TheButtonId_IsTheNotifyPrefixAndTheEventsId()
    {
        var id = Guid.CreateVersion7();

        var button = NotifyCommand.ButtonFor(id);

        Assert.Equal("modbot:notify:" + id.ToString("D"), button.Id);
        Assert.Equal("Notify me", button.Label);
        Assert.True(NotifyCommand.IsButton(button.Id));
        Assert.False(NotifyCommand.IsButton("modbot:remind:stop:" + id.ToString("D")));
        Assert.Equal("modbot:form:notify", NotifyCommand.FormId);
    }

    // ── The limit ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCommandAndTheButton_ShareOneLimitPerPerson_ApartFromOtherPeople()
    {
        await using var services = await SetUpAsync(_db);
        await AddCategoryAsync(services, "Karaoke");
        var e = await AddEventAsync(services, "Game night");

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.NotEqual(NotifyCommand.TooFastMessage, (await RunAsync(services)).Reply?.Text);

        Assert.Equal(NotifyCommand.TooFastMessage, (await RunAsync(services)).Reply?.Text);
        Assert.Equal(NotifyCommand.TooFastMessage, (await PressAsync(services, e.Id)).Text);

        // Somebody else is not held up by it.
        Assert.NotEqual(NotifyCommand.TooFastMessage, (await RunAsync(services, "another member")).Reply?.Text);
    }
}
