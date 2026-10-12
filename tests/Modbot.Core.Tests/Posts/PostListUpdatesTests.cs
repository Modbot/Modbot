using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;
using NodaTime;

namespace Modbot.Core.Tests.Posts;

/// <summary>
/// Keep updated (posts design §8): which event lists in a post read differently now, and how the
/// new words go into the text a site already has, against real PostgreSQL for the lists and without
/// it for the swap.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostListUpdatesTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Friday = new(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CalendarEvent EventAt(string title, DateTimeOffset startsAt, string state = CalendarEventStates.Scheduled) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        StartsAt = startsAt,
        EndsAt = startsAt.AddHours(1),
        TimeZone = "Europe/London",
        AccessType = "members",
        State = state,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static PostEventLine Line(CalendarEvent e) => new(e.Id, e.Title, e.StartsAt, e.TimeZone, null);

    private async Task WithDatabaseAsync(Func<ModbotContext, Task> test)
    {
        var connectionString = await fixture.CreateMigratedDatabaseAsync(Ct);

        try
        {
            var options = new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(connectionString).Options;
            await using var db = new ModbotContext(options);
            await test(db);
        }
        finally
        {
            await TestDatabases.DropAsync(fixture.ConnectionString, connectionString);
        }
    }

    private static string October(params PostEventLine[] lines) =>
        PostEventText.WhatsOnList(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), "Europe/London", lines);

    [Fact]
    public Task AWhatsOnListThatGainedAnEventIsRewrittenOverItsRange() => WithDatabaseAsync(async db =>
    {
        var movies = EventAt("Movie night", Friday);
        db.CalendarEvents.Add(movies);
        await db.SaveChangesAsync(Ct);

        var text = $"Come along!\n\n{October(Line(movies))}\n\nSee you there.";

        var games = EventAt("Games night", Friday.AddDays(1));
        db.CalendarEvents.Add(games);
        await db.SaveChangesAsync(Ct);

        var changes = await PostListUpdates.ChangesAsync(db, text, Now, Ct);

        var change = Assert.Single(changes);
        Assert.Equal(October(Line(movies)), change.Old);
        Assert.Equal(October(Line(movies), Line(games)), change.New);
        Assert.Equal($"Come along!\n\n{October(Line(movies), Line(games))}\n\nSee you there.", PostListUpdates.Swap(text, changes));
    });

    [Fact]
    public Task AListThatReadsTheSameIsNotAChange() => WithDatabaseAsync(async db =>
    {
        var movies = EventAt("Movie night", Friday);
        db.CalendarEvents.Add(movies);
        await db.SaveChangesAsync(Ct);

        Assert.Empty(await PostListUpdates.ChangesAsync(db, October(Line(movies)), Now, Ct));
    });

    [Fact]
    public Task ACancelledEventLeavesTheMonthWithoutIt() => WithDatabaseAsync(async db =>
    {
        var movies = EventAt("Movie night", Friday);
        var games = EventAt("Games night", Friday.AddDays(1));
        db.CalendarEvents.AddRange(movies, games);
        await db.SaveChangesAsync(Ct);
        var text = October(Line(movies), Line(games));

        movies.State = CalendarEventStates.Cancelled;
        await db.SaveChangesAsync(Ct);

        var change = Assert.Single(await PostListUpdates.ChangesAsync(db, text, Now, Ct));
        Assert.Equal(October(Line(games)), change.New);
    });

    [Fact]
    public Task AnEventListForAnEventThatWasCancelledSaysNothingIsPlanned() => WithDatabaseAsync(async db =>
    {
        var movies = EventAt("Movie night", Friday);
        db.CalendarEvents.Add(movies);
        await db.SaveChangesAsync(Ct);
        var text = PostEventText.EventList(Line(movies));

        movies.State = CalendarEventStates.Cancelled;
        await db.SaveChangesAsync(Ct);

        var change = Assert.Single(await PostListUpdates.ChangesAsync(db, text, Now, Ct));
        Assert.Equal(PostEventText.EmptyEventList(movies.Id), change.New);
        Assert.Contains(PostEventText.Nothing, change.New);

        // And once it says so, it is not a change again.
        Assert.Empty(await PostListUpdates.ChangesAsync(db, change.New, Now, Ct));
    });

    [Fact]
    public Task AnEventListFollowsTheEventToItsNewDate() => WithDatabaseAsync(async db =>
    {
        var movies = EventAt("Movie night", Friday);
        db.CalendarEvents.Add(movies);
        await db.SaveChangesAsync(Ct);
        var text = PostEventText.EventList(Line(movies));

        movies.StartsAt = Friday.AddDays(1);
        movies.EndsAt = movies.StartsAt.AddHours(1);
        await db.SaveChangesAsync(Ct);

        var change = Assert.Single(await PostListUpdates.ChangesAsync(db, text, Now, Ct));
        Assert.Contains(PostEventText.TimeMark(Friday.AddDays(1), "Europe/London"), change.New);
    });

    [Fact]
    public Task AnEventListIsLeftAloneOnceItsDateHasStarted() => WithDatabaseAsync(async db =>
    {
        var movies = EventAt("Movie night", Friday);
        db.CalendarEvents.Add(movies);
        await db.SaveChangesAsync(Ct);
        var text = PostEventText.EventList(Line(movies));

        movies.State = CalendarEventStates.Cancelled;
        await db.SaveChangesAsync(Ct);

        Assert.Empty(await PostListUpdates.ChangesAsync(db, text, Friday.AddHours(1), Ct));
    });

    [Fact]
    public Task AWhatsOnListIsLeftAloneOnceItsLastDayHasPassed() => WithDatabaseAsync(async db =>
    {
        var text = October();
        db.CalendarEvents.Add(EventAt("Movie night", Friday));
        await db.SaveChangesAsync(Ct);

        Assert.Single(await PostListUpdates.ChangesAsync(db, text, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), Ct));
        Assert.Empty(await PostListUpdates.ChangesAsync(db, text, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), Ct));
    });

    [Fact]
    public Task MarksThatCannotBeReadAreLeftAlone() => WithDatabaseAsync(async db =>
    {
        const string text = "{{event not-an-id}}\nx\n{{/event}}\n{{whats-on soon}}\ny\n{{/whats-on}}";

        Assert.Empty(await PostListUpdates.ChangesAsync(db, text, Now, Ct));
    });

    [Fact]
    public void TheNewListGoesIntoTheWordsASiteWasSent_AndNothingElseChanges()
    {
        var movies = new PostEventLine(Guid.NewGuid(), "Movie night", Friday, "Europe/London", "https://example.test/m");
        var games = new PostEventLine(Guid.NewGuid(), "Games night", Friday.AddDays(1), "Europe/London", null);
        var change = new PostListChange(October(movies), October(movies, games));
        var text = $"Come along!\n\n{change.Old}\n\nSee you there.";

        var discord = PostTexts.Discord("Events", PostEventText.ForSite(PostNetworks.Discord, text), roleId: "123");
        var vrchat = PostEventText.ForSite(PostNetworks.VRChat, text);

        var discordNow = PostListUpdates.ForSite(PostNetworks.Discord, discord, [change]);
        var vrchatNow = PostListUpdates.ForSite(PostNetworks.VRChat, vrchat, [change]);

        Assert.Equal(PostTexts.Discord("Events", PostEventText.ForSite(PostNetworks.Discord, $"Come along!\n\n{change.New}\n\nSee you there."), "123"), discordNow);
        Assert.Equal(PostEventText.ForSite(PostNetworks.VRChat, $"Come along!\n\n{change.New}\n\nSee you there."), vrchatNow);
        Assert.StartsWith("<@&123>\n**Events**\nCome along!", discordNow, StringComparison.Ordinal);
        Assert.EndsWith("See you there.", vrchatNow, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextWhoseListWasChangedByHandIsLeftAsItIs()
    {
        var movies = new PostEventLine(Guid.NewGuid(), "Movie night", Friday, "Europe/London", null);
        var change = new PostListChange(October(movies), October());
        var byHand = "Movie night is tonight, come early";

        Assert.Equal(byHand, PostListUpdates.ForSite(PostNetworks.VRChat, byHand, [change]));
    }

    [Fact]
    public void AListAlreadyRewrittenOnASiteIsNotRewrittenAgain()
    {
        var movies = new PostEventLine(Guid.NewGuid(), "Movie night", Friday, "Europe/London", null);
        var change = new PostListChange(October(movies), October());
        var done = PostEventText.ForSite(PostNetworks.VRChat, change.New);

        Assert.Equal(done, PostListUpdates.ForSite(PostNetworks.VRChat, done, [change]));
    }
}
