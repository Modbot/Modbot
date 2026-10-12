using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using NodaTime;

namespace Modbot.Core.Tests.Posts;

/// <summary>
/// The event lists a person inserts into a post (posts design §5.3): written once, sent to each
/// site in the form it can show, and found again by the marks that wrap them.
/// </summary>
public class PostEventTextTests
{
    // Sunday 18 October 2026, 19:00 UTC: 20:00 in London on summer time, 15:00 in New York.
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 19, 0, 0, TimeSpan.Zero);

    private static readonly Guid MovieNight = Guid.Parse("01923456-7890-7abc-8def-0123456789ab");

    private static PostEventLine Line(string title = "Movie night", string zone = "Europe/London", string? link = "https://discord.com/events/1/2") =>
        new(MovieNight, title, Start, zone, link);

    [Fact]
    public void ADateIsWrittenInTheEventsOwnZone()
    {
        Assert.Equal("{{time 1792350000 Sun 18 Oct, 20:00 BST}}", PostEventText.TimeMark(Start, "Europe/London"));
        Assert.Equal("{{time 1792350000 Sun 18 Oct, 15:00 EDT}}", PostEventText.TimeMark(Start, "America/New_York"));
    }

    [Fact]
    public void ADateOfAZoneNobodyKnowsIsWrittenInUtc()
    {
        Assert.Equal("{{time 1792350000 Sun 18 Oct, 19:00 UTC}}", PostEventText.TimeMark(Start, "Not/AZone"));
    }

    [Fact]
    public void DiscordGetsATimestampEachReaderSeesInTheirOwnZone()
    {
        var text = PostEventText.ForSite(PostNetworks.Discord, "Tonight: {{time 1792350000 Sun 18 Oct, 20:00 BST}}");

        Assert.Equal("Tonight: <t:1792350000:F>", text);
    }

    [Theory]
    [InlineData(PostNetworks.VRChat)]
    [InlineData(PostNetworks.Bluesky)]
    public void VRChatAndBlueskyGetTheEventsOwnTimeAndZone(string network)
    {
        var text = PostEventText.ForSite(network, "Tonight: {{time 1792350000 Sun 18 Oct, 20:00 BST}}");

        Assert.Equal("Tonight: Sun 18 Oct, 20:00 BST", text);
    }

    [Fact]
    public void TheMarksAroundAListAreShownNowhere()
    {
        var text = PostEventText.EventList(Line());

        foreach (var network in PostNetworks.All)
        {
            var sent = PostEventText.ForSite(network, "Hello\n" + text + "\nSee you there");

            Assert.DoesNotContain("{{", sent, StringComparison.Ordinal);
            Assert.StartsWith("Hello\nMovie night - ", sent, StringComparison.Ordinal);
            Assert.EndsWith("https://discord.com/events/1/2\nSee you there", sent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AMarkJoinedOntoALineIsTakenOutWithoutTakingTheLine()
    {
        Assert.Equal("Movie night", PostEventText.ForSite(PostNetworks.Discord, "{{event 1}}Movie night{{/event}}"));
    }

    [Fact]
    public void TextWithNoMarksIsOnlyTidied()
    {
        Assert.Equal("one\ntwo", PostEventText.ForSite(PostNetworks.Discord, "  one\r\ntwo \r\n"));
        Assert.Equal(string.Empty, PostEventText.ForSite(PostNetworks.VRChat, null));
    }

    [Fact]
    public void ADiscordTimestampTheWriterTypedIsLeftAlone()
    {
        Assert.Equal("<t:1792350000:R>", PostEventText.ForSite(PostNetworks.VRChat, "<t:1792350000:R>"));
    }

    [Fact]
    public void AnEventIsItsNameItsTimeAndItsLink()
    {
        Assert.Equal(
            "Movie night - {{time 1792350000 Sun 18 Oct, 20:00 BST}}\nhttps://discord.com/events/1/2",
            PostEventText.Lines(Line()));
    }

    [Fact]
    public void AnEventWithNoLinkIsTwoThingsNotThree()
    {
        Assert.Equal("Movie night - {{time 1792350000 Sun 18 Oct, 20:00 BST}}", PostEventText.Lines(Line(link: null)));
    }

    [Fact]
    public void WhatsOnIsEveryEventWithABlankLineBetweenAndTheRangeItWasBuiltFrom()
    {
        var text = PostEventText.WhatsOnList(
            new LocalDate(2026, 10, 1),
            new LocalDate(2026, 10, 31),
            "Europe/London",
            [Line(), Line("Quiz", link: null)]);

        Assert.StartsWith("{{whats-on 2026-10-01 to 2026-10-31 Europe/London}}\nMovie night - ", text, StringComparison.Ordinal);
        Assert.Contains("\n\nQuiz - {{time 1792350000 Sun 18 Oct, 20:00 BST}}\n{{/whats-on}}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMonthWithNoEventsSaysSo()
    {
        var text = PostEventText.WhatsOnList(new LocalDate(2026, 11, 1), new LocalDate(2026, 11, 30), "UTC", []);

        Assert.Equal("{{whats-on 2026-11-01 to 2026-11-30 UTC}}\nNothing is planned yet.\n{{/whats-on}}", text);
        Assert.Equal("Nothing is planned yet.", PostEventText.ForSite(PostNetworks.Bluesky, text));
    }

    [Fact]
    public void ALaterStepFindsEachListAndWhatItWasBuiltFrom()
    {
        var one = PostEventText.EventList(Line());
        var many = PostEventText.WhatsOnList(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), "Europe/London", [Line()]);
        var text = $"Intro\n{one}\nMiddle\n{many}\nEnd";

        var found = PostEventText.ListsIn(text);

        Assert.Collection(
            found,
            first =>
            {
                Assert.Equal(PostEventText.EventWord, first.Kind);
                Assert.Equal(MovieNight.ToString("D"), first.Argument);
                Assert.Equal(one, text.Substring(first.Start, first.Length));
            },
            second =>
            {
                Assert.Equal(PostEventText.WhatsOnWord, second.Kind);
                Assert.Equal("2026-10-01 to 2026-10-31 Europe/London", second.Argument);
                Assert.Equal(many, text.Substring(second.Start, second.Length));
            });
    }

    [Fact]
    public void EachSitesTextForAPostIsWrittenForThatSite()
    {
        var post = new Post { Text = "Tonight {{time 1792350000 Sun 18 Oct, 20:00 BST}}" };

        Assert.Equal("Tonight <t:1792350000:F>", PostTexts.TextFor(post, new PostDestination { Network = PostNetworks.Discord }));
        Assert.Equal("Tonight Sun 18 Oct, 20:00 BST", PostTexts.TextFor(post, new PostDestination { Network = PostNetworks.VRChat }));
        Assert.Equal("Tonight Sun 18 Oct, 20:00 BST", PostTexts.Bluesky(post, new PostDestination { Network = PostNetworks.Bluesky }));
    }

    [Fact]
    public void ADestinationsOwnTextIsWrittenForItsSiteToo()
    {
        var post = new Post { Text = "plain" };
        var discord = new PostDestination { Network = PostNetworks.Discord, TextOverride = "{{time 1792350000 Sun 18 Oct, 20:00 BST}}" };

        Assert.Equal("<t:1792350000:F>", PostTexts.TextFor(post, discord));
    }
}
