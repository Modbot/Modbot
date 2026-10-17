using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// What an event's Bluesky post says for one date, and when it goes (calendar design, Bluesky step).
/// </summary>
public class CalendarBlueskyTextTests
{
    private static readonly TimeSpan Two = TimeSpan.FromHours(2);
    private static readonly DateTimeOffset Sep20 = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event()
    {
        var e = CalendarRepeatTests.Event(Sep20, Two, CalendarRepeats.None, "Europe/London");
        e.Title = "Hangout";
        e.Description = "Come along";
        e.PublishToBluesky = true;
        return e;
    }

    private static CalendarOccurrence Date(CalendarEvent e) => CalendarRepeat.Between(e, Sep20.AddDays(-1), Sep20.AddDays(1)).Single();

    [Fact]
    public void TheChoicesAreOneHourOneDayThreeDaysAndOneWeek()
    {
        Assert.Equal([60, 1440, 4320, 10080], CalendarBlueskyText.BeforeChoices);
        Assert.Contains(CalendarBlueskyText.DefaultBeforeMinutes, CalendarBlueskyText.BeforeChoices);
        Assert.Equal(1440, CalendarBlueskyText.DefaultBeforeMinutes);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(1440, true)]
    [InlineData(4320, true)]
    [InlineData(10080, true)]
    [InlineData(0, false)]
    [InlineData(120, false)]
    [InlineData(-60, false)]
    public void OnlyTheFourChoicesAreChoices(int minutes, bool expected) =>
        Assert.Equal(expected, CalendarBlueskyText.IsChoice(minutes));

    [Theory]
    [InlineData(60)]
    [InlineData(1440)]
    [InlineData(4320)]
    [InlineData(10080)]
    public void TheSendTimeIsTheStartEarlierByTheChoice(int minutes)
    {
        var e = Event();
        e.BlueskyPostBeforeMinutes = minutes;

        Assert.Equal(Sep20 - TimeSpan.FromMinutes(minutes), CalendarBlueskyText.SendAt(e, Date(e)));
    }

    [Fact]
    public void ANewEventPostsOneDayBefore()
    {
        var e = new CalendarEvent();

        Assert.Equal(1440, e.BlueskyPostBeforeMinutes);
    }

    [Fact]
    public void ThePostHoldsTheTitleTheTimeTheWorldAndTheDescription()
    {
        var e = Event();

        var text = CalendarBlueskyText.For(e, Date(e), "Cozy Cabin");
        var lines = text.Split('\n');

        Assert.Equal("Hangout", lines[0]);
        // Bluesky cannot show a time mark, so the time is written out in the event's own zone.
        Assert.Equal("Sun 20 Sep, 20:00 BST", lines[1]);
        Assert.Equal("Cozy Cabin", lines[2]);
        Assert.Equal("Come along", lines[3]);
    }

    [Fact]
    public void AnEventWithNoWorldHasNoWorldLine()
    {
        var e = Event();

        var lines = CalendarBlueskyText.For(e, Date(e), null).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("Come along", lines[2]);
    }

    [Fact]
    public void BlueskysOwnWordsReplaceTheSharedOnes()
    {
        var e = Event();
        e.BlueskyTitle = "Tonight on Bluesky";
        e.BlueskyDescription = "Bluesky words";

        var text = CalendarBlueskyText.For(e, Date(e), null);

        Assert.Contains("Tonight on Bluesky", text, StringComparison.Ordinal);
        Assert.Contains("Bluesky words", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Come along", text, StringComparison.Ordinal);
        Assert.Equal("Tonight on Bluesky", CalendarBlueskyText.TitleOf(e, Date(e)));
    }

    [Fact]
    public void TheLinkIsTheGoogleEventsPageElseTheGroupsPage()
    {
        var e = Event();
        e.AutoLinks = new CalendarLinks("https://calendar.example/event", "https://discord.example/event", null, "https://vrchat.example/group");

        Assert.Equal("https://calendar.example/event", CalendarBlueskyText.LinkOf(e));
        Assert.EndsWith("https://calendar.example/event", CalendarBlueskyText.For(e, Date(e), null), StringComparison.Ordinal);

        e.AutoLinks = new CalendarLinks(null, "https://discord.example/event", null, "https://vrchat.example/group");

        Assert.Equal("https://vrchat.example/group", CalendarBlueskyText.LinkOf(e));
    }

    [Fact]
    public void TheDiscordEventIsNeverTheLink()
    {
        var e = Event();
        e.AutoLinks = new CalendarLinks(null, "https://discord.example/event", null, null);

        Assert.Null(CalendarBlueskyText.LinkOf(e));
        Assert.DoesNotContain("discord.example", CalendarBlueskyText.For(e, Date(e), null), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongDescriptionIsCutWithAnEllipsisSoThePostFits_AndTheLinkStays()
    {
        var e = Event();
        e.Description = new string('x', 600);
        e.AutoLinks = new CalendarLinks("https://calendar.example/event", null, null, null);

        var text = CalendarBlueskyText.For(e, Date(e), "Cozy Cabin");

        Assert.True(BlueskyText.Fits(text));
        Assert.Contains("…", text, StringComparison.Ordinal);
        Assert.StartsWith("Hangout\n", text, StringComparison.Ordinal);
        Assert.Contains("Cozy Cabin", text, StringComparison.Ordinal);
        Assert.EndsWith("https://calendar.example/event", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFingerprintChangesWithTheWordsAndThePicture_AndNotOtherwise()
    {
        var picture = Guid.CreateVersion7();

        var a = CalendarBlueskyText.Fingerprint("one", null);

        Assert.Equal(a, CalendarBlueskyText.Fingerprint("one", null));
        Assert.NotEqual(a, CalendarBlueskyText.Fingerprint("two", null));
        Assert.NotEqual(a, CalendarBlueskyText.Fingerprint("one", picture));
    }

    [Fact]
    public void ANullListOfPicturesGivesTheFingerprintFromBeforePicturesHadAList()
    {
        var picture = Guid.CreateVersion7();

        Assert.Equal(CalendarFingerprint.Of("one", picture), CalendarBlueskyText.Fingerprint("one", picture, null));
        Assert.Equal(CalendarFingerprint.Of("one", null), CalendarBlueskyText.Fingerprint("one", null));
    }

    [Fact]
    public void TheFingerprintChangesWithThePicturesTheirOrderAndTheirAltText()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var one = CalendarBlueskyText.Fingerprint("one", a, [new BlueskyPostPicture(a, "x"), new BlueskyPostPicture(b, "y")]);

        Assert.Equal(one, CalendarBlueskyText.Fingerprint("one", a, [new BlueskyPostPicture(a, "x"), new BlueskyPostPicture(b, "y")]));
        Assert.NotEqual(one, CalendarBlueskyText.Fingerprint("one", a, [new BlueskyPostPicture(b, "y"), new BlueskyPostPicture(a, "x")]));
        Assert.NotEqual(one, CalendarBlueskyText.Fingerprint("one", a, [new BlueskyPostPicture(a, "x"), new BlueskyPostPicture(b, "z")]));
        Assert.NotEqual(one, CalendarBlueskyText.Fingerprint("one", a, null));
    }

    [Fact]
    public void AnEventWithNoListOfPicturesGoesWithTheCoverAlone()
    {
        var e = Event();
        e.CoverPictureId = Guid.CreateVersion7();

        Assert.Null(CalendarBlueskyText.PicturesOf(e, "Hangout"));
    }

    [Fact]
    public void AnEventsListOfPicturesKeepsItsOrder_AndABlankAltTextIsTheTitle()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var e = Event();
        e.BlueskyPictures = [new BlueskyPostPicture(a, "  A poster "), new BlueskyPostPicture(b, "   ")];

        var pictures = CalendarBlueskyText.PicturesOf(e, "Hangout")!;

        Assert.Equal([a, b], pictures.Select(p => p.PictureId));
        Assert.Equal(["A poster", "Hangout"], pictures.Select(p => p.Alt));
    }

    [Fact]
    public void AnEmptyListOfPicturesIsAPostWithNone_NotTheCover()
    {
        var e = Event();
        e.CoverPictureId = Guid.CreateVersion7();
        e.BlueskyPictures = [];

        Assert.Empty(CalendarBlueskyText.PicturesOf(e, "Hangout")!);
    }

    private static CalendarCategory Category(string name, string? tag = null) =>
        new() { Id = Guid.CreateVersion7(), Name = name, BlueskyTag = tag };

    [Fact]
    public void ThePostEndsWithItsCategoriesTags_ThenTheOnesForEveryPost()
    {
        var e = Event();
        e.Categories = [Category("Open mic"), Category("Karaoke", "Sing")];

        var tags = CalendarBlueskyText.TagsOf(e, "#VRChat");
        var text = CalendarBlueskyText.For(e, Date(e), null, tags);

        Assert.Equal(["Sing", "OpenMic", "VRChat"], tags);
        Assert.EndsWith("\n#Sing #OpenMic #VRChat", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACategoryWithNoTagAddsNone_AndAnEventWithNoTagsPostsAsBefore()
    {
        var e = Event();
        e.Categories = [Category("Karaoke", string.Empty)];

        Assert.Empty(CalendarBlueskyText.TagsOf(e, null));
        Assert.Equal(CalendarBlueskyText.For(e, Date(e), null), CalendarBlueskyText.For(e, Date(e), null, CalendarBlueskyText.TagsOf(e, null)));
    }

    [Fact]
    public void TagsThatDoNotFitAreDroppedFromTheEnd_AndTheTextStaysWhole()
    {
        var e = Event();
        e.Description = new string('x', 600);
        e.AutoLinks = new CalendarLinks("https://calendar.example/event", null, null, null);
        var without = CalendarBlueskyText.For(e, Date(e), "Cozy Cabin");

        var with = CalendarBlueskyText.For(e, Date(e), "Cozy Cabin", ["VRChat", "Karaoke"]);

        Assert.StartsWith(without, with, StringComparison.Ordinal);
        Assert.True(BlueskyText.Fits(with));
    }

    [Fact]
    public void ATagChangeChangesTheFingerprint()
    {
        var e = Event();
        var plain = CalendarBlueskyText.For(e, Date(e), null);
        var tagged = CalendarBlueskyText.For(e, Date(e), null, ["VRChat"]);

        Assert.NotEqual(CalendarBlueskyText.Fingerprint(plain, null), CalendarBlueskyText.Fingerprint(tagged, null));
    }
}
