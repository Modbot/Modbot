using System.Globalization;
using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using NodaTime;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// The week's line-up on Bluesky: which dates go in (every one everyone can see in the next seven
/// days, cancelled ones out) and what the post says (as many dates as fit in 300 characters, then
/// +N more, times in the line-up's zone).
/// </summary>
public class CalendarBlueskyLineupTests
{
    private static readonly TimeSpan Two = TimeSpan.FromHours(2);

    // A Wednesday.
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Public(
        string title, DateTimeOffset startsAt, string repeat = CalendarRepeats.None, string zone = "UTC", params string[] days)
    {
        var e = CalendarRepeatTests.Event(startsAt, Two, repeat, zone, null, days);
        e.Id = Guid.CreateVersion7();
        e.Title = title;
        e.PublishToVRChat = true;
        e.Visibility = "public";
        e.PublishToBluesky = false;
        return e;
    }

    private static IReadOnlyList<string> Titles(params CalendarEvent[] events) =>
        [.. CalendarBlueskyLineup.DatesOf(events, Now).Select(d => d.Title)];

    // ── Which dates ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWeekWithNoDatesHasNone() =>
        Assert.Empty(CalendarBlueskyLineup.DatesOf([Public("Karaoke", Now.AddDays(8))], Now));

    [Fact]
    public void AnEventNeedsNoBlueskySwitch()
    {
        var e = Public("Karaoke", Now.AddDays(1));
        e.PublishToBluesky = false;

        Assert.Equal(["Karaoke"], Titles(e));
    }

    [Fact]
    public void AMembersOnlyEventNeverGoesIn()
    {
        var group = Public("Group only", Now.AddDays(1));
        group.Visibility = "group";

        var roles = Public("Role only", Now.AddDays(1));
        roles.VRChatRoleIds = ["grol_1"];

        // A Discord-only event that was never shown "Visible to" holds the form's default, group.
        var discordOnlyMembers = Public("Discord only, members", Now.AddDays(1));
        discordOnlyMembers.PublishToVRChat = false;
        discordOnlyMembers.PublishToDiscord = true;
        discordOnlyMembers.Visibility = "group";

        Assert.Empty(Titles(group, roles, discordOnlyMembers));
    }

    [Fact]
    public void AnEventEveryoneCanSeeGoesInWhateverPlacesItGoesTo()
    {
        var discordOnly = Public("Discord only", Now.AddDays(1));
        discordOnly.PublishToVRChat = false;
        discordOnly.PublishToDiscord = true;

        var nowhere = Public("Calendar only", Now.AddDays(2));
        nowhere.PublishToVRChat = false;

        Assert.Equal(["Discord only", "Calendar only"], Titles(discordOnly, nowhere));
    }

    [Fact]
    public void ACancelledOrDeletedEventIsLeftOut()
    {
        var cancelled = Public("Cancelled", Now.AddDays(1));
        cancelled.State = CalendarEventStates.Cancelled;

        var deleted = Public("Deleted", Now.AddDays(1));
        deleted.DeletedAt = Now;

        Assert.Empty(Titles(cancelled, deleted));
    }

    [Fact]
    public void ACancelledDateOfARepeatingEventIsLeftOut()
    {
        var e = Public("Movie night", Now.AddDays(1), CalendarRepeats.Daily);
        e.DateChanges.Add(new CalendarDateChange { Id = Guid.CreateVersion7(), EventId = e.Id, PlannedStartsAt = Now.AddDays(3), Cancelled = true });

        var dates = CalendarBlueskyLineup.DatesOf([e], Now);

        // Thursday to Tuesday at 12:00 is six dates, less the cancelled one.
        Assert.Equal(5, dates.Count);
        Assert.DoesNotContain(dates, d => d.StartsAt == Now.AddDays(3));
    }

    [Fact]
    public void ARepeatingEventGivesEachDateInTheWeekInOrder()
    {
        var weekly = Public("Karaoke", new DateTimeOffset(2026, 9, 7, 20, 0, 0, TimeSpan.Zero), CalendarRepeats.Weekly, "UTC", "MO");
        var once = Public("Build social", new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero));

        var dates = CalendarBlueskyLineup.DatesOf([once, weekly], Now);

        Assert.Equal(["Build social", "Karaoke"], dates.Select(d => d.Title));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero), dates[1].StartsAt);
    }

    [Fact]
    public void TheListUsesTheEventsPlainTitle_NotTheBlueskyOneThatMayBeAOneOffPromoLine()
    {
        var e = Public("Karaoke", Now.AddDays(1));
        e.BlueskyTitle = "Tonight only: free snacks!";

        Assert.Equal(["Karaoke"], Titles(e));
    }

    [Fact]
    public void ALinkInATitleIsLeftOut_SoTheCalendarPageIsTheOnlyLinkAndTheCard()
    {
        const string page = "https://calendar.google.com/calendar/embed?src=group";
        var e = Public("Karaoke https://evil.example/x night", Now.AddDays(1));

        var dates = CalendarBlueskyLineup.DatesOf([e], Now);
        var text = CalendarBlueskyLineup.TextOf("Group", dates, DateTimeZone.Utc, page);

        Assert.Equal(["Karaoke night"], dates.Select(d => d.Title));
        Assert.Equal(page, BlueskyText.CardLink(text));
    }

    [Fact]
    public void TheListStartsAtTheTimeGiven_SoALatePostStillCoversThePlannedWeek()
    {
        var planned = Now.AddHours(-1);
        var startedInTheGap = Public("In the gap", Now.AddMinutes(-30));
        var pastTheWeek = Public("Past the week", planned.AddDays(7).AddMinutes(30));

        Assert.Equal(["In the gap"], CalendarBlueskyLineup.DatesOf([startedInTheGap, pastTheWeek], planned).Select(d => d.Title));
    }

    [Fact]
    public void ADateMovedOnItsOwnIsWhereItWasMovedTo()
    {
        var e = Public("Karaoke", Now.AddDays(2));
        e.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = e.Id,
            PlannedStartsAt = Now.AddDays(2),
            StartsAt = Now.AddDays(5),
            EndsAt = Now.AddDays(5) + Two,
            Title = "Karaoke (late)",
        });

        var date = Assert.Single(CalendarBlueskyLineup.DatesOf([e], Now));

        Assert.Equal(Now.AddDays(5), date.StartsAt);
        Assert.Equal("Karaoke (late)", date.Title);
    }

    [Fact]
    public void TheWeekRunsFromNowUpToButNotIncludingSevenDaysOn()
    {
        var atStart = Public("At the start", Now);
        var underWay = Public("Under way", Now.AddHours(-1));
        var lastMoment = Public("Last moment", Now.AddDays(7).AddMinutes(-1));
        var exactlySevenDays = Public("Seven days on", Now.AddDays(7));

        Assert.Equal(["At the start", "Last moment"], Titles(exactlySevenDays, lastMoment, underWay, atStart));
    }

    // ── What the post says ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("UTC", "Sun 19:00")]
    [InlineData("Europe/London", "Sun 20:00")]
    [InlineData("America/New_York", "Sun 15:00")]
    [InlineData("Asia/Tokyo", "Mon 04:00")]
    public void ADatesTimeIsWrittenInTheLineupsZone(string zone, string expected)
    {
        var date = new LineupDate(new DateTimeOffset(2026, 9, 20, 19, 0, 0, TimeSpan.Zero), "Karaoke");

        Assert.Equal(expected + " Karaoke", CalendarBlueskyLineup.LineOf(date, DateTimeZoneProviders.Tzdb[zone]));
    }

    [Fact]
    public void ADateWithoutATitleIsJustItsTime()
    {
        var date = new LineupDate(new DateTimeOffset(2026, 9, 20, 9, 5, 0, TimeSpan.Zero), string.Empty);

        Assert.Equal("Sun 09:05", CalendarBlueskyLineup.LineOf(date, DateTimeZone.Utc));
    }

    [Fact]
    public void AFewDatesAreAllListedUnderTheGroupsName()
    {
        LineupDate[] dates =
        [
            new(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero), "Karaoke"),
            new(new DateTimeOffset(2026, 10, 21, 21, 0, 0, TimeSpan.Zero), "Movie night"),
        ];

        var text = CalendarBlueskyLineup.TextOf("Our group", dates, DateTimeZone.Utc, "https://calendar.example/page");

        Assert.Equal("This week at Our group\nMon 20:00 Karaoke\nWed 21:00 Movie night\nhttps://calendar.example/page", text);
    }

    [Fact]
    public void AGroupWithoutANameHasAPlainHeading()
    {
        LineupDate[] dates = [new(Now, "Karaoke")];

        var text = CalendarBlueskyLineup.TextOf(null, dates, DateTimeZone.Utc, link: null);

        Assert.Equal("This week's events\nWed 12:00 Karaoke", text);
    }

    [Fact]
    public void ManyDatesListAsManyAsFitThenSayHowManyMore()
    {
        var dates = Enumerable.Range(0, 30)
            .Select(i => new LineupDate(Now.AddHours(i * 5), "Dance night " + i.ToString(CultureInfo.InvariantCulture)))
            .ToList();

        var text = CalendarBlueskyLineup.TextOf("Our group", dates, DateTimeZone.Utc, "https://calendar.example/page");
        var lines = text.Split('\n');

        Assert.True(BlueskyText.Fits(text));
        Assert.Equal("https://calendar.example/page", lines[^1]);

        var shown = lines.Length - 3;
        Assert.InRange(shown, 1, 29);
        Assert.Equal($"+{30 - shown} more", lines[^2]);

        // One more date would not have fitted.
        var one = CalendarBlueskyLineup.LineOf(dates[shown], DateTimeZone.Utc);
        var longer = string.Join('\n', lines[..^2].Append(one).Append($"+{30 - shown - 1} more").Append(lines[^1]));
        Assert.False(BlueskyText.Fits(longer));
    }

    [Fact]
    public void TheLinkIsLeftOutWhenTheGroupHasNone()
    {
        var dates = Enumerable.Range(0, 40).Select(i => new LineupDate(Now.AddHours(i), "Hangout")).ToList();

        var text = CalendarBlueskyLineup.TextOf("Our group", dates, DateTimeZone.Utc, link: null);

        Assert.True(BlueskyText.Fits(text));
        Assert.Matches(@"\+\d+ more$", text);
        Assert.DoesNotContain("http", text);
    }

    [Fact]
    public void ATitleTooLongForEvenOneDateGivesWayAtAWholeGrapheme()
    {
        LineupDate[] dates = [new(Now, new string('x', 400) + "😀"), new(Now.AddHours(1), "Second")];

        var text = CalendarBlueskyLineup.TextOf("Our group", dates, DateTimeZone.Utc, "https://calendar.example/page");

        Assert.True(BlueskyText.Fits(text));
        Assert.Contains("…", text);
        Assert.EndsWith("+1 more\nhttps://calendar.example/page", text);
    }

    // ── The calendar page ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCalendarPageIsTheGooglePublicPageOnlyWhileTheCalendarIsPublic()
    {
        var settings = new Settings { GoogleCalendarId = "group@group.calendar.google.com", GoogleCalendarTimeZone = "Europe/London", GooglePublic = GooglePublic.All };

        Assert.Equal(
            GoogleCalendarIds.PublicPageLink("group@group.calendar.google.com", "Europe/London"),
            CalendarBlueskyLineup.PageLinkOf(settings));

        settings.GooglePublic = GooglePublic.FreeBusy;
        Assert.Null(CalendarBlueskyLineup.PageLinkOf(settings));

        settings.GooglePublic = GooglePublic.All;
        settings.GoogleCalendarId = null;
        Assert.Null(CalendarBlueskyLineup.PageLinkOf(settings));
    }
}
