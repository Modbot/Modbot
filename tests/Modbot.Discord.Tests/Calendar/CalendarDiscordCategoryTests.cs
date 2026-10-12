using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>The categories made in Modbot, as Discord's channel post and server event say them.</summary>
public class CalendarDiscordCategoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(-1);

    private static CalendarEvent Event(params string[] categories)
    {
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Friday night",
            Description = "Plain words",
            StartsAt = Start,
            EndsAt = Start.AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
        };

        e.Categories.AddRange(categories.Select(name => new CalendarCategory { Id = Guid.CreateVersion7(), Name = name }));
        CalendarTimeline.Advance(e, Now);
        return e;
    }

    [Fact]
    public void TheChannelPostListsTheCategoriesInAlphabeticalOrder()
    {
        var e = Event("Open mic", "Karaoke");

        var card = CalendarCard.For(e, CalendarRepeat.Current(e), null, CalendarCardState.Scheduled, null);

        var field = Assert.Single(card.Fields, f => f.Name == "Categories");
        Assert.Equal("Karaoke, Open mic", field.Value);
    }

    [Fact]
    public void OneCategoryIsCalledCategory()
    {
        var e = Event("Karaoke");

        var card = CalendarCard.For(e, CalendarRepeat.Current(e), null, CalendarCardState.Scheduled, null);

        Assert.Equal("Karaoke", Assert.Single(card.Fields, f => f.Name == "Category").Value);
    }

    [Fact]
    public void WithNoCategoriesThePostHasNoCategoryField()
    {
        var e = Event();

        var card = CalendarCard.For(e, CalendarRepeat.Current(e), null, CalendarCardState.Scheduled, null);

        Assert.DoesNotContain(card.Fields, f => f.Name is "Category" or "Categories");
    }

    [Fact]
    public void TheServerEventPutsTheCategoriesFirstInItsDescription()
    {
        var e = Event("Movie night", "Karaoke");

        var details = CalendarDiscordPublisher.ServerEventDetails(e, null, null, null, Now);

        Assert.Equal("Categories: Karaoke, Movie night\n\nPlain words", details.Description);
    }

    [Fact]
    public void WithNoCategoriesTheServerEventSaysOnlyTheDescription()
    {
        var details = CalendarDiscordPublisher.ServerEventDetails(Event(), null, null, null, Now);

        Assert.Equal("Plain words", details.Description);
    }

    [Fact]
    public void ACategoryNameCannotMarkUpTheCard()
    {
        var e = Event("**Loud**");

        var line = CalendarCard.CategoryNames(e);

        Assert.NotEqual("**Loud**", line);
        Assert.Contains("Loud", line, StringComparison.Ordinal);
    }
}
