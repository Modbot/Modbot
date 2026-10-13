using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Automatic links in an event's texts (calendar templates plan, step 4): which addresses exist,
/// how each text is filled, and that a link with no address is left out without leaving half a sentence.
/// </summary>
public class CalendarAutoLinksTests
{
    private static readonly DateTimeOffset Sep20 = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> NoNames = [];

    private static readonly CalendarLinks All = new(
        "https://www.google.com/calendar/event?eid=abc",
        "https://discord.com/events/1/2",
        "https://calendar.google.com/calendar/r?cid=x",
        "https://vrchat.com/home/group/grp_1");

    private static CalendarEvent Event() =>
        CalendarRepeatTests.Event(Sep20, TimeSpan.FromHours(2), CalendarRepeats.None, "Europe/London");

    private static Settings SettingsWith() => new()
    {
        DiscordGuildId = "1",
        GoogleCalendarId = "events@group.calendar.google.com",
        GooglePublic = "all",
        ManagedGroupId = "grp_1",
    };

    private static CalendarEventPlace Place(
        CalendarEvent e, string place, string? externalId = null, string? link = null, string state = CalendarPlaceStates.Published) => new()
    {
        EventId = e.Id,
        Place = place,
        State = state,
        ExternalId = externalId,
        GoogleLink = link,
        OccurrenceStartsAt = CalendarRepeat.Current(e).PlannedStartsAt,
    };

    [Fact]
    public void EachLinkIsReplacedByItsAddress_InAnyCapitalsOrSpacing()
    {
        var text = "G: {{Google event}} D: {{ discord  EVENT }} C: {{Add our Google calendar}} V: {{VRChat group page}}";

        Assert.Equal(
            "G: https://www.google.com/calendar/event?eid=abc D: https://discord.com/events/1/2 C: https://calendar.google.com/calendar/r?cid=x V: https://vrchat.com/home/group/grp_1",
            CalendarAutoLinks.Fill(text, All));
    }

    [Fact]
    public void ABlankThatIsNotALinkIsLeftAlone()
    {
        Assert.Equal("Hosted by {{Host}}: https://discord.com/events/1/2", CalendarAutoLinks.Fill("Hosted by {{Host}}: {{Discord event}}", All));
        Assert.Equal("No markers here", CalendarAutoLinks.Fill("No markers here", All));
    }

    [Fact]
    public void ALinkWithNoAddressIsLeftOutWithTheSpacesBeforeIt()
    {
        Assert.Equal("Join us", CalendarAutoLinks.Fill("Join us {{Google event}}", CalendarLinks.None));
        Assert.Equal("Join now", CalendarAutoLinks.Fill("Join {{Discord event}} now", null));
        Assert.Equal("Join:", CalendarAutoLinks.Fill("Join: {{Google event}}", All with { GoogleEvent = null }));
    }

    [Fact]
    public void ALineLeftWithNothingGoesToo_AndABlankLineIsNotLeftDoubled()
    {
        Assert.Equal("Come along\n\nSee you", CalendarAutoLinks.Fill("Come along\n\n{{Google event}}\n\nSee you", CalendarLinks.None));
        Assert.Equal("Come along\nSee you", CalendarAutoLinks.Fill("Come along\n{{Google event}}\nSee you", CalendarLinks.None));
        Assert.Equal(string.Empty, CalendarAutoLinks.Fill("{{Google event}}", CalendarLinks.None));

        // A line that was empty to begin with stays.
        Assert.Equal("a\n\nb", CalendarAutoLinks.Fill("a\n\nb{{Google event}}", CalendarLinks.None));
    }

    [Fact]
    public void TheNamesAreTheFourTheWebPageOffers()
    {
        Assert.Equal(["Google event", "Discord event", "Add our Google calendar", "VRChat group page"], CalendarAutoLinks.Names);
        Assert.True(CalendarAutoLinks.IsLink("  vrchat GROUP page"));
        Assert.False(CalendarAutoLinks.IsLink("Host"));
    }

    [Fact]
    public void EveryAddressExistsOnceItsSourceIsKnown()
    {
        var e = Event();

        var links = CalendarAutoLinks.For(
            e,
            SettingsWith(),
            [Place(e, CalendarPlaces.Google, "id1", "https://www.google.com/calendar/event?eid=abc"), Place(e, CalendarPlaces.DiscordEvent, "2")]);

        Assert.Equal("https://www.google.com/calendar/event?eid=abc", links.GoogleEvent);
        Assert.Equal("https://discord.com/events/1/2", links.DiscordEvent);
        Assert.Equal("https://calendar.google.com/calendar/r?cid=events%40group.calendar.google.com", links.AddGoogleCalendar);
        Assert.Equal("https://vrchat.com/home/group/grp_1", links.VRChatGroupPage);
    }

    [Fact]
    public void BeforeTheFirstPublishTheEventsOwnAddressesDoNotExist()
    {
        var e = Event();

        var links = CalendarAutoLinks.For(
            e,
            SettingsWith(),
            [Place(e, CalendarPlaces.Google, "id1", state: CalendarPlaceStates.Waiting), Place(e, CalendarPlaces.DiscordEvent)]);

        Assert.Null(links.GoogleEvent);
        Assert.Null(links.DiscordEvent);
        Assert.NotNull(links.AddGoogleCalendar);
        Assert.NotNull(links.VRChatGroupPage);
    }

    [Fact]
    public void ARemovedPlaceOrAnotherDatesDiscordEventGivesNoAddress()
    {
        var e = Event();
        var another = Place(e, CalendarPlaces.DiscordEvent, "2");
        another.OccurrenceStartsAt = Sep20.AddDays(-7);

        var links = CalendarAutoLinks.For(
            e,
            SettingsWith(),
            [Place(e, CalendarPlaces.Google, "id1", "https://www.google.com/calendar/event?eid=abc", CalendarPlaceStates.Removed), another]);

        Assert.Null(links.GoogleEvent);
        Assert.Null(links.DiscordEvent);
    }

    [Fact]
    public void NoPublicGoogleCalendarOrNoGroupLeavesThoseLinksOut()
    {
        var e = Event();

        var settings = SettingsWith();
        settings.GooglePublic = "no";
        settings.ManagedGroupId = null;
        settings.DiscordGuildId = null;

        var links = CalendarAutoLinks.For(e, settings, [Place(e, CalendarPlaces.DiscordEvent, "2")]);

        Assert.Equal(CalendarLinks.None, links);
    }

    [Fact]
    public void EverySiteIsSentTheTextsFilled_AndAnEventNobodyResolvedSendsNoCode()
    {
        var e = Event();
        e.Title = "Movie night";
        e.Description = "More: {{Google event}}\nJoin: {{VRChat group page}}";
        e.DiscordDescription = "Chat: {{Discord event}}";

        // Nothing resolved: every link is left out, none is sent as code.
        Assert.Equal("More:\nJoin:", CalendarSiteText.Description(e, CalendarSite.VRChat));
        Assert.Equal("Chat:", CalendarSiteText.Description(e, CalendarSite.Discord));

        e.AutoLinks = All;

        Assert.Equal(
            "More: https://www.google.com/calendar/event?eid=abc\nJoin: https://vrchat.com/home/group/grp_1",
            CalendarSiteText.Description(e, CalendarSite.VRChat));
        Assert.Equal("Chat: https://discord.com/events/1/2", CalendarSiteText.Description(e, CalendarSite.Discord));

        // The words as typed are still the markers.
        Assert.Equal("Chat: {{Discord event}}", CalendarSiteText.OwnDescription(e, CalendarSite.Discord));
    }

    [Fact]
    public void AGoogleDescriptionMakesTheFilledAddressALink()
    {
        var e = Event();
        e.GoogleDescription = "Also on Discord: {{Discord event}}";
        e.AutoLinks = All;

        var body = CalendarGoogleBody.For(e, NoNames, "Group");

        Assert.Contains("<a href=\"https://discord.com/events/1/2\">https://discord.com/events/1/2</a>", body.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressThatAppearsLaterChangesWhatGoogleIsSent_SoTheOrdinaryUpdateSendsItAgain()
    {
        var e = Event();
        e.GoogleDescription = "Discord: {{Discord event}}";

        var before = CalendarGoogleBody.For(e, NoNames, "Group").Fingerprint("cal");

        e.AutoLinks = All;
        var after = CalendarGoogleBody.For(e, NoNames, "Group").Fingerprint("cal");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void TheFeedSaysTheFilledTexts()
    {
        var e = Event();
        e.Description = "Add us: {{Add our Google calendar}}";
        e.AutoLinks = All;

        Assert.Equal("Add us: https://calendar.google.com/calendar/r?cid=x", CalendarFeedWriter.Entry(e, NoNames).Notes);
    }
}
