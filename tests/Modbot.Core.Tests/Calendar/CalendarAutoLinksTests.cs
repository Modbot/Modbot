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
    public void ARemovedPlaceGivesNoAddress()
    {
        var e = Event();

        var links = CalendarAutoLinks.For(
            e,
            SettingsWith(),
            [
                Place(e, CalendarPlaces.Google, "id1", "https://www.google.com/calendar/event?eid=abc", CalendarPlaceStates.Removed),
                Place(e, CalendarPlaces.DiscordEvent, "2", state: CalendarPlaceStates.Removed),
            ]);

        Assert.Null(links.GoogleEvent);
        Assert.Null(links.DiscordEvent);
    }

    [Fact]
    public void WhenTheDateMovesOnTheDiscordAddressStaysTheLastOneUntilTheNewEventExists()
    {
        // A weekly event whose Discord event is still the one made for the week before: the date has
        // moved on but Discord has not yet ended that event and made the next. VRChat and Google must
        // keep the address they have, not be sent a text without it and then another with the new one.
        var e = CalendarRepeatTests.Event(Sep20, TimeSpan.FromHours(2), CalendarRepeats.Weekly, "UTC", null, "sunday");
        e.OccurrenceStartsAt = Sep20.AddDays(7);

        var old = Place(e, CalendarPlaces.DiscordEvent, "2");
        old.OccurrenceStartsAt = Sep20;

        var during = CalendarAutoLinks.For(e, SettingsWith(), [old]);
        Assert.Equal("https://discord.com/events/1/2", during.DiscordEvent);

        // Discord has made the new date's event: the address moves on, once.
        old.ExternalId = "3";
        old.OccurrenceStartsAt = Sep20.AddDays(7);

        Assert.Equal("https://discord.com/events/1/3", CalendarAutoLinks.For(e, SettingsWith(), [old]).DiscordEvent);
    }

    [Fact]
    public void DiscordsOwnTextsLeaveTheDiscordEventOut_TheOtherSitesKeepIt()
    {
        var e = Event();
        e.Description = "Come: {{Discord event}}\nMore: {{Google event}}";
        e.AutoLinks = All;

        Assert.Equal("Come:\nMore: https://www.google.com/calendar/event?eid=abc", CalendarSiteText.Description(e, CalendarSite.Discord));
        Assert.Equal("Come: https://discord.com/events/1/2\nMore: https://www.google.com/calendar/event?eid=abc", CalendarSiteText.Description(e, CalendarSite.VRChat));
        Assert.Equal("Come: https://discord.com/events/1/2\nMore: https://www.google.com/calendar/event?eid=abc", CalendarSiteText.Description(e, CalendarSite.Google));

        // A date's own description, on Discord, too.
        var occurrence = new CalendarOccurrence(Sep20, Sep20.AddHours(2), Sep20, new CalendarDateChange { PlannedStartsAt = Sep20, Description = "Own: {{Discord event}}" });
        Assert.Equal("Own:", CalendarRepeat.DescriptionOf(e, occurrence, CalendarSite.Discord));
    }

    [Fact]
    public void ADateHeldApartHoldsNoDiscordAddress_SoItsFingerprintDoesNotMoveWhenTheDateDoes()
    {
        var e = CalendarRepeatTests.Event(Sep20, TimeSpan.FromHours(2), CalendarRepeats.Weekly, "UTC", null, "sunday");
        e.Description = "Join: {{Discord event}} {{Google event}}";

        var planned = Sep20.AddDays(14);
        var change = new CalendarDateChange { PlannedStartsAt = planned, Description = "Own: {{Discord event}} {{Google event}}" };
        var plain = new CalendarDateChange { PlannedStartsAt = planned.AddDays(7), StartsAt = planned.AddDays(7).AddHours(1) };

        var google = new Dictionary<string, string>();
        string Fingerprint(CalendarDateChange c) => CalendarGoogleBody.DateFor(e, c, google).Fingerprint;

        e.AutoLinks = All with { DiscordEvent = "https://discord.com/events/1/2" };
        var first = (Fingerprint(change), Fingerprint(plain));

        // The date moves on: Discord has made the next date's event.
        e.AutoLinks = All with { DiscordEvent = "https://discord.com/events/1/3" };
        Assert.Equal(first, (Fingerprint(change), Fingerprint(plain)));

        // The text is still sent, with the links that do not move.
        var date = CalendarGoogleBody.DateFor(e, change, google);
        Assert.Contains("https://www.google.com/calendar/event?eid=abc", date.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("discord.com", date.Description, StringComparison.Ordinal);

        // The series as a whole does carry the address, so it is sent once per date.
        e.AutoLinks = All with { DiscordEvent = "https://discord.com/events/1/2" };
        var before = CalendarGoogleBody.For(e, google, "Group").Fingerprint("cal");
        e.AutoLinks = All with { DiscordEvent = "https://discord.com/events/1/3" };
        Assert.NotEqual(before, CalendarGoogleBody.For(e, google, "Group").Fingerprint("cal"));
    }

    [Fact]
    public void TitlesNeverHoldALink_AndAreSentAsTyped()
    {
        var e = Event();
        e.Title = "Movie {{Host}} {{Google event}}";
        e.GoogleTitle = "Google {{Discord event}}";
        e.AutoLinks = All;

        Assert.Equal("Movie {{Host}} {{Google event}}", CalendarSiteText.Title(e, CalendarSite.VRChat));
        Assert.Equal("Google {{Discord event}}", CalendarSiteText.Title(e, CalendarSite.Google));
        Assert.Equal("Movie {{Host}} {{Google event}}", CalendarRepeat.TitleOf(e, CalendarRepeat.Current(e)));
    }

    [Fact]
    public void HasLinkFindsALinkNameInAnyCapitalsOrSpacing_AndNotAFillIn()
    {
        Assert.True(CalendarAutoLinks.HasLink("Movie {{Google event}}"));
        Assert.True(CalendarAutoLinks.HasLink("{{ vrchat  GROUP page }}"));
        Assert.False(CalendarAutoLinks.HasLink("Movie {{Host}}"));
        Assert.False(CalendarAutoLinks.HasLink("Google event"));
        Assert.False(CalendarAutoLinks.HasLink(null));
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

        // Discord's own text never links to itself.
        Assert.Equal("Chat:", CalendarSiteText.Description(e, CalendarSite.Discord));

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
