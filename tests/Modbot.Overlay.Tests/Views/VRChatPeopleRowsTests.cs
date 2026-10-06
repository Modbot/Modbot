using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The people rows of the desktop window's look, drawn like VRChat's own Users list: cards with a
/// picture, a name and a line under it, the moderator's own card first, and a panel whose width is
/// the window's whatever is on it. The headset's rows are checked to be what they were.
/// </summary>
public class VRChatPeopleRowsTests
{
    private const double PanelWidth = 520;
    private const double PanelHeight = 720;

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);

    private static RosterMember Person(string name, string? picture = null, RosterStanding standing = RosterStanding.Ordinary)
        => new("usr_" + name, name, standing, 0, [], PictureUrl: picture);

    private static readonly RosterMember Kai = Person("Kai");
    private static readonly RosterMember Jo = Person("Jo");
    private static readonly RosterMember Ash = Person("Ash");

    private static OverlayScreen Screen(
        IReadOnlyList<RosterMember> present,
        string? moderator = null,
        OverlayPage page = OverlayPage.Instance,
        int skip = 0,
        bool canPlace = true,
        IReadOnlyList<LiveEvent>? events = null,
        ListFilters? filters = null,
        IReadOnlyList<RecentLeaver>? left = null)
        => new(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", present), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: page,
            RosterSkip: skip,
            Events: events,
            RosterFilters: filters,
            Now: Now,
            CanPlaceHeadsUps: canPlace,
            Left: left,
            ModeratorId: moderator);

    private static LiveEvent Joined(RosterMember who)
        => new(
            "1",
            "1",
            LiveEventKinds.PersonJoined,
            Now.AddMinutes(-2),
            "39911",
            new LivePerson(who.SubjectId, who.DisplayName, null, who.Standing, 0, []),
            false,
            null,
            false);

    private static OverlayLook VRChatLook()
    {
        static PaletteColour c(byte r, byte g, byte b) => new(r, g, b);

        return OverlayLook.FromColours(new OverlayColours(
            Panel: c(0x18, 0x12, 0x24),
            Bar: c(0x10, 0x0C, 0x1A),
            Button: c(0x2A, 0x20, 0x44),
            Border: c(0x38, 0x2C, 0x5A),
            Hover: c(0x30, 0x26, 0x4E),
            Selected: c(0x50, 0x3A, 0x90),
            SelectedBorder: c(0x80, 0x60, 0xE0),
            SelectedText: c(0xF5, 0xF5, 0xF5),
            Icon: c(0xA0, 0x90, 0xD0),
            Text: c(0xF0, 0xF0, 0xF8),
            Subtext: c(0xB0, 0xA8, 0xC8)));
    }

    private static T Draw<T>(OverlayScreen screen, Func<Control, T> read, Func<string?, IImage?>? picture = null, OverlayLook? look = null)
        => AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(screen, picture, look ?? VRChatLook());
            root.Measure(new Size(PanelWidth, PanelHeight));
            root.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));
            return read(root);
        });

    private static IReadOnlyList<string?> Texts(OverlayScreen screen, OverlayLook? look = null)
        => Draw<IReadOnlyList<string?>>(screen, root =>
            [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)], look: look ?? VRChatLook());

    private static IReadOnlyList<PlacedTarget> Placed(OverlayScreen screen)
        => Draw(screen, root => OverlayTargets.Find(root));

    private static IReadOnlyList<string> PeopleInOrder(OverlayScreen screen)
        => [.. Placed(screen).Where(p => p.Target is OverlayTarget.Person).OrderBy(p => p.Bounds.Top).Select(p => ((OverlayTarget.Person)p.Target).SubjectId)];

    [Fact]
    public void TheCountIsVRChatsHeadingOverTheUsersAndNotTheOldBar()
    {
        var texts = Texts(Screen([Kai, Jo]));

        Assert.Contains("Users (2)", texts);
        Assert.DoesNotContain("2 here", texts);
    }

    [Fact]
    public void AFilterSaysHowManyOfThoseHereItShows()
    {
        var texts = Texts(Screen([Kai, Jo], filters: new ListFilters(Name: "Kai")));

        Assert.Contains("Users (1 of 2)", texts);
    }

    [Fact]
    public void TheHeadsetKeepsItsOwnCountAndItsOwnRows()
    {
        var texts = Texts(Screen([Kai, Jo]), OverlayLook.Headset);

        Assert.Contains("2 here", texts);
        Assert.DoesNotContain("Users (2)", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith("Other users", StringComparison.Ordinal));
    }

    [Fact]
    public void TheModeratorsOwnCardIsFirstFollowedByTheOtherUsersLabel()
    {
        var texts = Texts(Screen([Kai, Jo, Ash], moderator: "usr_Ash"));

        Assert.Contains("Users (3)", texts);
        Assert.Contains("Other users (2)", texts);
        // The others are in the order the list always has: by name, within their band.
        Assert.Equal(["usr_Ash", "usr_Jo", "usr_Kai"], PeopleInOrder(Screen([Kai, Jo, Ash], moderator: "usr_Ash")));
    }

    [Fact]
    public void WithNoModeratorKnownThereIsNoBannerAndNoLabel()
    {
        var screen = Screen([Kai, Jo], moderator: null);

        Assert.DoesNotContain(Texts(screen), t => t is not null && t.StartsWith("Other users", StringComparison.Ordinal));
        Assert.Equal(["usr_Jo", "usr_Kai"], PeopleInOrder(screen));
    }

    [Fact]
    public void AModeratorWhoIsNotOnTheListLeavesNoBannerAndNoLabel()
    {
        var screen = Screen([Kai, Jo], moderator: "usr_Nobody");

        Assert.DoesNotContain(Texts(screen), t => t is not null && t.StartsWith("Other users", StringComparison.Ordinal));
        Assert.Equal(2, PeopleInOrder(screen).Count);
    }

    [Fact]
    public void WhenTheModeratorIsAloneThereIsNoLabelForNobody()
    {
        var texts = Texts(Screen([Kai], moderator: "usr_Kai"));

        Assert.Contains("Users (1)", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith("Other users", StringComparison.Ordinal));
    }

    [Fact]
    public void TheModeratorsOwnCardHasNoPlusAndTheOthersDo()
    {
        var targets = Placed(Screen([Kai, Jo], moderator: "usr_Kai")).Select(p => p.Target).ToList();

        Assert.DoesNotContain(new OverlayTarget.AddHeadsUp("usr_Kai"), targets);
        Assert.Contains(new OverlayTarget.AddHeadsUp("usr_Jo"), targets);
        Assert.Contains(new OverlayTarget.Person("usr_Kai"), targets);
    }

    [Fact]
    public void ScrollingMovesTheOthersAndLeavesTheModeratorsCardWhereItIs()
    {
        var screen = Screen([Kai, Jo, Ash], moderator: "usr_Kai", skip: 1);

        // Ash, Jo and then Kai by name; Kai is the moderator, so one of Ash and Jo is scrolled past.
        Assert.Equal(["usr_Kai", "usr_Jo"], PeopleInOrder(screen));
        Assert.Contains("1 more above", Texts(screen));
    }

    [Fact]
    public void TheLeftCardKeepsItsTagAndCountdownAndIsNotCounted()
    {
        var gone = new RecentLeaver(Ash, Now.AddSeconds(-18));
        var texts = Texts(Screen([Kai, Jo], moderator: "usr_Kai", left: [gone]));

        Assert.Contains("Left", texts);
        Assert.Contains("42s", texts);
        Assert.Contains("Users (2)", texts);
        Assert.Contains("Other users (1)", texts);
        Assert.DoesNotContain(
            Placed(Screen([Kai, Jo], moderator: "usr_Kai", left: [gone])).Select(p => p.Target),
            t => t is OverlayTarget.AddHeadsUp { SubjectId: "usr_Ash" });
    }

    [Fact]
    public void ACardIsTheSameHeightWithAGapBetweenAndWhateverIsOnIt()
    {
        var long_ = Person(new string('W', 80), standing: RosterStanding.Flagged) with
        {
            Flags = ["a very long reason somebody was flagged for, going on and on"],
            TrustRank = Modbot.Core.Users.TrustRank.KnownUser,
            EighteenPlus = true,
        };

        var cards = Placed(Screen([Kai, long_, Jo])).Where(p => p.Target is OverlayTarget.Person).OrderBy(p => p.Bounds.Top).ToList();

        Assert.Equal(3, cards.Count);
        Assert.All(cards, card => Assert.Equal(cards[0].Bounds.Height, card.Bounds.Height));
        Assert.Equal(cards[1].Bounds.Top - cards[0].Bounds.Bottom, cards[2].Bounds.Top - cards[1].Bounds.Bottom, 0.5);
        Assert.InRange(cards[1].Bounds.Top - cards[0].Bounds.Bottom, 6, 8);
        Assert.InRange(cards[0].Bounds.Height, 60, 80);
    }

    [Theory]
    [InlineData(OverlayPage.Instance)]
    [InlineData(OverlayPage.Events)]
    public void ThePanelIsExactlyAsWideAsItIsGivenWhateverIsOnTheTab(OverlayPage page)
    {
        var long_ = Person(new string('W', 120)) with
        {
            Flags = [new string('x', 200)],
            TrustRank = Modbot.Core.Users.TrustRank.KnownUser,
        };

        var screen = Screen([Kai, long_], moderator: "usr_Kai", page: page, events: [Joined(Kai), Joined(long_)])
            with { ModeratorArrived = Now.AddHours(-30) };

        var (desired, cards) = Draw(
            screen,
            root => (root.DesiredSize.Width, OverlayTargets.Find(root).Where(p => p.Target is OverlayTarget.Person).Select(p => p.Bounds.Right).ToList()));

        Assert.Equal(PanelWidth, desired);
        Assert.All(cards, right => Assert.True(right <= PanelWidth, $"A card runs off the right edge at {right}."));
    }

    [Fact]
    public void AnEmptyAndAFullPanelAreTheSameWidth()
    {
        var widths = new[] { Screen([]), Screen([Kai, Jo], moderator: "usr_Kai"), Screen([Kai], page: OverlayPage.Events) }
            .Select(screen => Draw(screen, root => root.DesiredSize.Width))
            .Distinct()
            .ToList();

        Assert.Equal([PanelWidth], widths);
    }

    [Fact]
    public void ThePictureTheRosterNamesIsAskedOfTheCacheAndShownOnceItIsThere()
    {
        var withPicture = Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1");
        var asked = new List<string?>();

        var picture = AvaloniaTestHost.Run(() => new RenderTargetBitmap(new PixelSize(8, 8)));

        IImage? Cache(string? address)
        {
            asked.Add(address);
            return address == withPicture.PictureUrl ? picture : null;
        }

        var shown = Draw(
            Screen([withPicture, Jo]),
            root => root.GetLogicalDescendants().OfType<Image>().Count(image => ReferenceEquals(image.Source, picture)),
            Cache);

        Assert.Equal(1, shown);
        Assert.Equal([withPicture.PictureUrl], asked.Distinct());
    }

    [Fact]
    public void WithNoPictureOrOneThatHasNotArrivedTheHeadAndShouldersStandIn()
    {
        var withPicture = Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1");

        var images = Draw(
            Screen([withPicture, Jo]),
            root => root.GetLogicalDescendants().OfType<Image>().Count(),
            _ => null);

        Assert.Equal(0, images);
    }

    [Fact]
    public void TheHeadsetNeverAsksForAPerson()
    {
        var asked = new List<string?>();

        Draw(
            Screen([Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1")]),
            _ => 0,
            address =>
            {
                asked.Add(address);
                return null;
            },
            OverlayLook.Headset);

        // The group's own icon is asked for, as it always was; no person's picture is.
        Assert.DoesNotContain(asked, address => address is not null && address.Contains("/companion/picture/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAuditLogRowsAreCardsWithTheWordsUnderTheNameAndTheTimeAtTheEnd()
    {
        var screen = Screen([Kai], page: OverlayPage.Events, events: [Joined(Kai)]);

        var texts = Texts(screen);
        var cards = Placed(screen).Where(p => p.Target is OverlayTarget.Person).ToList();

        Assert.Contains("Kai", texts);
        Assert.Contains("Joined", texts);
        Assert.Contains(Now.AddMinutes(-2).ToLocalTime().ToString("HH:mm"), texts);
        Assert.Single(cards);
        Assert.InRange(cards[0].Bounds.Height, 60, 80);
    }

    [Fact]
    public void TheAuditLogRowUsesThePictureTheRosterKnowsForThatPerson()
    {
        var withPicture = Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1");
        var picture = AvaloniaTestHost.Run(() => new RenderTargetBitmap(new PixelSize(8, 8)));

        var shown = Draw(
            Screen([withPicture], page: OverlayPage.Events, events: [Joined(withPicture)]),
            root => root.GetLogicalDescendants().OfType<Image>().Count(image => ReferenceEquals(image.Source, picture)),
            address => address == withPicture.PictureUrl ? picture : null);

        Assert.Equal(1, shown);
    }

    [Fact]
    public void ThePanelIsDrawnAgainForAnotherPictureOrAnotherModerator()
    {
        var before = Screen([Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1")]);
        var newPicture = Screen([Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=2")]);

        Assert.True(before.LooksTheSameAs(Screen([Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1")])));
        Assert.False(before.LooksTheSameAs(newPicture));
        Assert.False(before.LooksTheSameAs(before with { ModeratorId = "usr_Kai" }));
    }

    [Fact]
    public void TheScreenListsThePicturesItMayAskFor()
    {
        var gone = new RecentLeaver(Person("Ash", "https://cats.example/api/v1/companion/picture/usr_Ash?v=1"), Now);
        var screen = Screen([Person("Kai", "https://cats.example/api/v1/companion/picture/usr_Kai?v=1"), Jo], left: [gone]);

        Assert.Equal(
            [
                "https://cats.example/api/v1/companion/picture/usr_Ash?v=1",
                "https://cats.example/api/v1/companion/picture/usr_Kai?v=1",
            ],
            screen.PictureAddresses().Order());
    }

    [Fact]
    public void TheHeadingColourIsTheHighlightsLightenedToReadAndTheBannerReadsAtBothEnds()
    {
        var colours = VRChatLook().VRChat!.Colours;

        var (heading, bannerWords, stops) = AvaloniaTestHost.Run(() =>
        {
            var v = VRChatLook().VRChat!;
            var banner = Assert.IsType<LinearGradientBrush>(v.Banner);
            return (
                Assert.IsAssignableFrom<ISolidColorBrush>(v.Heading).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(v.BannerText).Color,
                banner.GradientStops.Select(s => s.Color).ToList());
        });

        static PaletteColour P(Color c) => new(c.R, c.G, c.B);

        Assert.True(PaletteColour.Contrast(P(heading), colours.Panel) >= OverlayColours.LeastContrast);
        Assert.True(PaletteColour.Contrast(P(heading), colours.Bar) >= OverlayColours.LeastContrast);
        Assert.Equal(2, stops.Count);
        Assert.All(stops, stop => Assert.True(PaletteColour.Contrast(P(bannerWords), P(stop)) >= OverlayColours.LeastContrast));
    }
}
