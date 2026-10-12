using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// A site's own title and description, and how a date's own words, the shared words and the links
/// in a Google description fit with them (calendar design, per-site text).
/// </summary>
public class CalendarSiteTextTests
{
    private static readonly TimeSpan Two = TimeSpan.FromHours(2);
    private static readonly DateTimeOffset Sep20 = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> NoNames = [];

    private static CalendarEvent Event(string repeat = CalendarRepeats.None, params string[] days)
    {
        var e = CalendarRepeatTests.Event(Sep20, Two, repeat, "Europe/London", null, days);
        e.Visibility = "public";
        e.PublishToGoogle = true;
        e.Title = "Thy Kingdom-No Kids Hangout";
        e.Description = "Come along";
        return e;
    }

    private static CalendarDateChange Change(CalendarEvent e, DateTimeOffset planned) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = e.Id,
        PlannedStartsAt = planned,
        CreatedAt = Sep20,
        UpdatedAt = Sep20,
    };

    [Theory]
    [InlineData(CalendarSite.VRChat)]
    [InlineData(CalendarSite.Discord)]
    [InlineData(CalendarSite.Google)]
    public void ASiteWithNoOwnWordsSaysTheSharedOnes(CalendarSite site)
    {
        var e = Event();

        Assert.Equal("Thy Kingdom-No Kids Hangout", CalendarSiteText.Title(e, site));
        Assert.Equal("Come along", CalendarSiteText.Description(e, site));
        Assert.Null(CalendarSiteText.OwnTitle(e, site));
        Assert.Null(CalendarSiteText.OwnDescription(e, site));
    }

    [Fact]
    public void ASiteSaysItsOwnWords_AndOnlyThatSiteDoes()
    {
        var e = Event();
        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";
        e.DiscordDescription = "[Group Link](https://vrc.group/KINGDOM.1234)";

        Assert.Equal("18+ Hangout w/ Thy Kingdom", CalendarSiteText.Title(e, CalendarSite.VRChat));
        Assert.Equal("Come along", CalendarSiteText.Description(e, CalendarSite.VRChat));
        Assert.Equal("Thy Kingdom-No Kids Hangout", CalendarSiteText.Title(e, CalendarSite.Discord));
        Assert.Equal("[Group Link](https://vrc.group/KINGDOM.1234)", CalendarSiteText.Description(e, CalendarSite.Discord));
        Assert.Equal("Thy Kingdom-No Kids Hangout", CalendarSiteText.Title(e, CalendarSite.Google));
    }

    [Fact]
    public void BlankOwnWordsAreTheSharedOnes()
    {
        var e = Event();
        e.GoogleTitle = "   ";
        e.GoogleDescription = "";

        Assert.Equal("Thy Kingdom-No Kids Hangout", CalendarSiteText.Title(e, CalendarSite.Google));
        Assert.Equal("Come along", CalendarSiteText.Description(e, CalendarSite.Google));
    }

    [Fact]
    public void ADatesOwnWordsWinOnEverySite()
    {
        var e = Event(CalendarRepeats.Weekly, "SU");
        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";
        e.DiscordDescription = "Discord words";

        var planned = Sep20.AddDays(7);
        var change = Change(e, planned);
        change.Title = "Halloween special";
        var date = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e));

        Assert.Equal("Halloween special", CalendarRepeat.TitleOf(e, date, CalendarSite.VRChat));
        Assert.Equal("Halloween special", CalendarRepeat.TitleOf(e, date, CalendarSite.Discord));
        Assert.Equal("Halloween special", CalendarRepeat.TitleOf(e, date, CalendarSite.Google));

        // The description was not changed on the date, so each site keeps its own.
        Assert.Equal("Come along", CalendarRepeat.DescriptionOf(e, date, CalendarSite.VRChat));
        Assert.Equal("Discord words", CalendarRepeat.DescriptionOf(e, date, CalendarSite.Discord));

        // The shared overloads are unchanged: a date's own words, else the event's.
        Assert.Equal("Halloween special", CalendarRepeat.TitleOf(e, date));
        Assert.Equal("Come along", CalendarRepeat.DescriptionOf(e, date));
    }

    [Fact]
    public void GoogleSendsItsOwnTitleAndDescription_WithLinksMadeClickable()
    {
        var e = Event();
        e.GoogleTitle = "Thy Kingdom-No Kids Hangout [VRChat, Group Public]";
        e.GoogleDescription = "Join us: [Group Link](https://vrc.group/KINGDOM.1234) & more";

        var body = CalendarGoogleBody.For(e, NoNames, "Group");

        Assert.Equal("Thy Kingdom-No Kids Hangout [VRChat, Group Public]", body.Summary);
        Assert.Equal("Join us: <a href=\"https://vrc.group/KINGDOM.1234\">Group Link</a> &amp; more", body.Description);
    }

    [Fact]
    public void TheSharedDescriptionIsStillOnlyEscaped()
    {
        var e = Event();
        e.Description = "See https://example.com/a and [x](https://example.com/b) <b>bold</b>";

        var body = CalendarGoogleBody.For(e, NoNames, null);

        Assert.Equal("See https://example.com/a and [x](https://example.com/b) &lt;b&gt;bold&lt;/b&gt;", body.Description);
    }

    [Fact]
    public void ChangingOneSitesWordsChangesOnlyThatSitesFingerprint()
    {
        var e = Event();
        var before = CalendarGoogleBody.For(e, NoNames, null).Fingerprint("cal");

        e.DiscordTitle = "Something else";
        e.VRChatDescription = "Plain words";
        Assert.Equal(before, CalendarGoogleBody.For(e, NoNames, null).Fingerprint("cal"));

        e.GoogleTitle = "Google's title";
        Assert.NotEqual(before, CalendarGoogleBody.For(e, NoNames, null).Fingerprint("cal"));
    }

    [Fact]
    public void ADatesOwnDescriptionOnGoogleIsEscapedNotLinked()
    {
        var e = Event(CalendarRepeats.Weekly, "SU");
        e.GoogleDescription = "[Own](https://example.com/own)";

        var plain = Change(e, Sep20.AddDays(7));
        plain.StartsAt = Sep20.AddDays(7).AddHours(1);

        Assert.Equal("<a href=\"https://example.com/own\">Own</a>", CalendarGoogleBody.DateFor(e, plain, NoNames).Description);

        plain.Description = "[Typed on the date](https://example.com/date)";
        Assert.Equal("[Typed on the date](https://example.com/date)", CalendarGoogleBody.DateFor(e, plain, NoNames).Description);
    }

    [Theory]
    [InlineData("[words](https://example.com/a?x=1&y=2)", "<a href=\"https://example.com/a?x=1&amp;y=2\">words</a>")]
    [InlineData("Go to https://example.com/page.", "Go to <a href=\"https://example.com/page\">https://example.com/page</a>.")]
    [InlineData("[words](javascript:alert(1))", "[words](javascript:alert(1))")]
    [InlineData("[words](ftp://example.com/file)", "[words](ftp://example.com/file)")]
    [InlineData("<a href=\"javascript:alert(1)\">raw</a>", "&lt;a href=\"javascript:alert(1)\"&gt;raw&lt;/a&gt;")]
    [InlineData("<img src=x onerror=alert(1)>", "&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("[<b>bold</b>](https://example.com)", "<a href=\"https://example.com\">&lt;b&gt;bold&lt;/b&gt;</a>")]
    [InlineData("[a](https://example.com/\"onmouseover=\"x)", "[a](<a href=\"https://example.com/\">https://example.com/</a>\"onmouseover=\"x)")]
    [InlineData("no links & <script>", "no links &amp; &lt;script&gt;")]
    public void OnlyWebLinksBecomeLinks_AndEverythingElseIsEscaped(string typed, string expected) =>
        Assert.Equal(expected, CalendarGoogleLinks.Html(typed));

    [Fact]
    public void ThePreviewShowsALinksWordsAndReadsTheEscapesBack()
    {
        var html = CalendarGoogleLinks.Html("Join [Group Link](https://vrc.group/KINGDOM.1234) & <you>");

        Assert.Equal("Join Group Link & <you>", CalendarGoogleLinks.Shown(html));
    }
}
