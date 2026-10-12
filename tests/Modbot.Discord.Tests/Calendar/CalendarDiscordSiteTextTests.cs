using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>Discord's own title and description on an event (calendar design, per-site text).</summary>
public class CalendarDiscordSiteTextTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(-1);

    private static CalendarEvent Event() => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "18+ Hangout w/ Thy Kingdom",
        Description = "Plain words",
        StartsAt = Start,
        EndsAt = Start.AddHours(2),
        TimeZone = "UTC",
        State = CalendarEventStates.Scheduled,
    };

    [Fact]
    public void TheServerEventAndTheChannelPostSayDiscordsOwnWords()
    {
        var e = Event();
        e.DiscordTitle = "Thy Kingdom-No Kids Hangout [VRChat, Group Public]";
        e.DiscordDescription = "[Group Link](https://vrc.group/KINGDOM.1234)";
        CalendarTimeline.Advance(e, Now);

        var details = CalendarDiscordPublisher.ServerEventDetails(e, null, null, null, Now);
        Assert.Equal("Thy Kingdom-No Kids Hangout [VRChat, Group Public]", details.Name);
        Assert.Equal("[Group Link](https://vrc.group/KINGDOM.1234)", details.Description);

        var card = CalendarCard.For(e, CalendarRepeat.Current(e), null, CalendarCardState.Scheduled, null);
        Assert.Equal("Thy Kingdom-No Kids Hangout [VRChat, Group Public]", card.Title);
        Assert.Equal("[Group Link](https://vrc.group/KINGDOM.1234)", card.Description);
    }

    [Fact]
    public void WithNoOwnWordsDiscordSaysTheSharedOnes()
    {
        var e = Event();
        CalendarTimeline.Advance(e, Now);

        var details = CalendarDiscordPublisher.ServerEventDetails(e, null, null, null, Now);
        Assert.Equal("18+ Hangout w/ Thy Kingdom", details.Name);
        Assert.Equal("Plain words", details.Description);
    }

    [Fact]
    public void OtherSitesWordsAreNotDiscords()
    {
        var e = Event();
        e.VRChatTitle = "Only on VRChat";
        e.GoogleDescription = "Only on Google";
        CalendarTimeline.Advance(e, Now);

        var details = CalendarDiscordPublisher.ServerEventDetails(e, null, null, null, Now);
        Assert.Equal("18+ Hangout w/ Thy Kingdom", details.Name);
        Assert.Equal("Plain words", details.Description);
    }

    [Fact]
    public void ACancelNoticeNamesTheEventTheWayDiscordSaysIt()
    {
        var e = Event();
        e.DiscordTitle = "Discord's name";

        Assert.Contains("Discord's name", CalendarCard.CancelNotice(e, Start), StringComparison.Ordinal);
    }

    [Fact]
    public void ADatesOwnTitleWinsOverDiscordsOwn()
    {
        var e = Event();
        e.DiscordTitle = "Discord's name";
        var change = new CalendarDateChange { PlannedStartsAt = Start, Title = "Halloween special" };
        var date = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e));

        Assert.Equal("Halloween special", CalendarRepeat.TitleOf(e, date, CalendarSite.Discord));
    }
}
