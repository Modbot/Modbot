using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// The monthly event summary: one post in the chosen channel for the month before, with each
/// event's averages, claimed before it is posted so it goes out once.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventSummaryPostTests(PostgresFixture db)
{
    private const string Channel = "444444444444444444";

    // The clock starts on 1 January 2026, so the month before is December 2025.
    private static readonly DateTimeOffset Dec = new(2025, 12, 5, 20, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> PostAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new EventSummaryPost(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    /// <summary>Turned on, with the clock at 06:00 UTC on 1 January, when the summary may go out.</summary>
    private static Task TurnOnAsync(TestServices services, DateOnly? last = null)
    {
        services.Clock.Advance(TimeSpan.FromHours(6));

        return services.ConfigureAsync(s =>
        {
            s.EventSummaryOn = true;
            s.EventSummaryChannelId = Channel;
            s.EventSummaryLastMonth = last ?? new DateOnly(2025, 11, 1);
        }, Ct);
    }

    private static async Task AddEventWithCountsAsync(TestServices services, string title = "Movie night")
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            StartsAt = Dec,
            EndsAt = Dec.AddHours(2),
            TimeZone = "UTC",
            Repeat = CalendarRepeats.Weekly,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);

        var store = new CalendarInterestStore(context);
        await store.RecordDiscordAsync(e.Id, Dec, 10, now, Ct);
        await store.RecordDiscordAsync(e.Id, Dec.AddDays(7), 20, now, Ct);
        await store.RecordVRChatAsync(e.Id, Dec, 3, now, Ct);
    }

    [Fact]
    public async Task TheMonthBeforeIsPostedOnce_WithEachEventsAverages()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await AddEventWithCountsAsync(services);

        Assert.Equal(1, await PostAsync(services, gateway));
        Assert.Equal(0, await PostAsync(services, gateway));

        var post = Assert.Single(gateway.Messages);
        Assert.Equal(Channel, post.ChannelId);

        var card = Assert.Single(post.Embeds);
        Assert.Equal("Events in December 2025", card.Title);

        var field = Assert.Single(card.Fields);
        Assert.Equal("Movie night", field.Name);
        Assert.Contains("2 dates", field.Value);
        Assert.Contains("Average interested on Discord: 15", field.Value);
        Assert.Contains("Average interested on VRChat: 3", field.Value);
        Assert.DoesNotContain("Average came", field.Value);

        await using var context = services.Database.NewContext();
        Assert.Equal(new DateOnly(2025, 12, 1), (await context.GetSettingsAsync(Ct)).EventSummaryLastMonth);
    }

    [Fact]
    public async Task NothingIsPostedBefore0600UtcOnThe1st_ThenItGoesOut()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await AddEventWithCountsAsync(services);

        services.Clock.Advance(TimeSpan.FromHours(-1));

        Assert.Equal(0, await PostAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        services.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, await PostAsync(services, gateway));
        Assert.Single(gateway.Messages);
    }

    [Fact]
    public async Task NothingIsPosted_WhileItIsOff_OrForAMonthAlreadyPosted()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await AddEventWithCountsAsync(services);
        Assert.Equal(0, await PostAsync(services, gateway));

        await TurnOnAsync(services, last: new DateOnly(2025, 12, 1));
        Assert.Equal(0, await PostAsync(services, gateway));

        Assert.Empty(gateway.Messages);
    }

    [Fact]
    public async Task AMonthWithNothingToSayGetsNoPost_AndIsNotLookedAtAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);

        Assert.Equal(0, await PostAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        await using var context = services.Database.NewContext();
        Assert.Equal(new DateOnly(2025, 12, 1), (await context.GetSettingsAsync(Ct)).EventSummaryLastMonth);
    }

    [Fact]
    public async Task ARefusalThatMayPassIsTriedAgainOnTheNextPass()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await AddEventWithCountsAsync(services);

        gateway.FailNextPost("Discord is busy.");

        Assert.Equal(0, await PostAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        Assert.Equal(1, await PostAsync(services, gateway));
        Assert.Single(gateway.Messages);
    }

    [Fact]
    public async Task AFinalRefusalIsNotTriedAgain_AndTheCardShowsWhy()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await AddEventWithCountsAsync(services);

        gateway.FailNextPost("Could not post to Discord: the bot may not post in that channel.", permanent: true);

        Assert.Equal(0, await PostAsync(services, gateway));
        Assert.Equal(0, await PostAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        await using var context = services.Database.NewContext();
        Assert.Contains("may not post", (await context.GetSettingsAsync(Ct)).EventSummaryProblem);
    }

    [Fact]
    public async Task TwoCopiesOfModbotRunningAtOnce_PostTheSummaryOnce()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await AddEventWithCountsAsync(services);

        var posted = await Task.WhenAll(PostAsync(services, gateway), PostAsync(services, gateway));

        Assert.Equal(1, posted.Sum());
        Assert.Single(gateway.Messages);
    }

    [Fact]
    public void ALongListIsCutShort_AndSaysHowManyMore()
    {
        var lines = Enumerable.Range(0, 27)
            .Select(i => new EventMonthLine(Guid.CreateVersion7(), $"Event {i}", 1, 5m, null, null))
            .ToList();

        var card = EventSummaryPost.Embed(new DateOnly(2025, 12, 1), lines);

        Assert.Equal(24, card.Fields.Count);
        Assert.Equal("and 3 more", card.Description);
    }
}
