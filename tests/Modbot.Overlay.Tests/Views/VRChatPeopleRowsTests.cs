using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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

    private static T Draw<T>(OverlayScreen screen, Func<Control, T> read, Func<string?, IImage?>? picture = null, OverlayLook? look = null, double width = PanelWidth)
        => AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(screen, picture, look ?? VRChatLook());
            root.Measure(new Size(width, PanelHeight));
            root.Arrange(new Rect(0, 0, width, PanelHeight));
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

    private static readonly IReadOnlyList<RosterMember> Mixed =
    [
        new("usr_a", "Rin", RosterStanding.Flagged, 0, [], EighteenPlus: true),
        new("usr_b", "Kai", RosterStanding.Staff, 0, [], EighteenPlus: true),
        new("usr_c", "Mira", RosterStanding.Member, 0, [], EighteenPlus: false),
        new("usr_d", "Jo", RosterStanding.Member, 0, []),
        new("usr_e", "Ash", RosterStanding.Ordinary, 0, [], EighteenPlus: true),
    ];

    [Fact]
    public void TheHeadingCountsGroupEighteenPlusAndStaffOfEverybodyHere()
    {
        var texts = Texts(Screen(Mixed));

        Assert.Contains("Users (5)", texts);
        Assert.Contains("Group (3)", texts);
        Assert.Contains("18+ (3)", texts);
        Assert.Contains("Staff (1)", texts);
    }

    [Fact]
    public void AFilterLeavesTheCountsOfEverybodyHere()
    {
        var texts = Texts(Screen(Mixed, filters: new ListFilters(Name: "Kai")));

        Assert.Contains("Users (1 of 5)", texts);
        Assert.Contains("Group (3)", texts);
        Assert.Contains("18+ (3)", texts);
        Assert.Contains("Staff (1)", texts);
    }

    [Fact]
    public void TheHeadingPartsAreSeparatedByDotsInTheHeadingColourAndTheCountsAreInTheTextColour()
    {
        var (dots, parts) = Draw(
            Screen(Mixed),
            root =>
            {
                var blocks = root.GetLogicalDescendants().OfType<TextBlock>().ToList();
                var look = VRChatLook().VRChat!;
                var heading = ((ISolidColorBrush)look.Heading).Color;
                var text = ((ISolidColorBrush)look.Text).Color;
                var separators = blocks.Where(t => t.Text == "•").ToList();
                var counted = blocks.Where(t => t.Text is "Users (5)" or "Group (3)" or "18+ (3)" or "Staff (1)").ToList();

                return (
                    separators.Count(t => (t.Foreground as ISolidColorBrush)?.Color == heading && t.FontSize == 20),
                    counted.Count(t => (t.Foreground as ISolidColorBrush)?.Color == text && t.FontSize == 20 && t.FontWeight == FontWeight.Bold));
            });

        Assert.Equal(3, dots);
        Assert.Equal(4, parts);
    }

    [Fact]
    public void TheModbotLookSaysTheSameCountsAfterTheNumberHere()
    {
        var texts = Texts(Screen(Mixed), OverlayLook.Headset);

        Assert.Contains("5 here", texts);
        // Each count is its own item, so a heading wider than the room wraps between counts.
        Assert.Contains("· Group (3)", texts);
        Assert.Contains("· 18+ (3)", texts);
        Assert.Contains("· Staff (1)", texts);
    }

    private static IReadOnlyList<RosterMember> OneHundredTwenty()
        => [.. Enumerable.Range(0, 120).Select(i => new RosterMember("usr_" + i, "P" + i, RosterStanding.Member, 0, [], EighteenPlus: true))];

    [Fact]
    public void TheHeadingFitsBesideTheFreshnessWhenThePanelIsWideEnough()
    {
        var (staffRight, freshnessLeft, staffTop, headingTop, _) = Draw(
            Screen(OneHundredTwenty(), filters: new ListFilters(Name: "P1")),
            root => HeadingEdges(root),
            // Wide enough for the counts in the wider system font a Linux runner falls back to, too.
            width: 1000);

        Assert.True(staffRight <= freshnessLeft, $"Counts end at {staffRight}, freshness starts at {freshnessLeft}.");
        Assert.True(staffTop - headingTop < 10, "The counts wrapped onto a second line.");
    }

    [Theory]
    [InlineData(300)]
    [InlineData(PanelWidth)]
    public void ANarrowPanelWrapsTheLastCountsOntoASecondLineInsteadOfRunningThemUnderTheFreshness(double width)
    {
        var (staffRight, freshnessLeft, staffTop, headingTop, clipRight) = Draw(
            Screen(OneHundredTwenty(), filters: new ListFilters(Name: "P1")),
            root => HeadingEdges(root),
            width: width);

        // The counts are too long for one line beside the freshness, so the last one drops to a second
        // line; the room the counts get stops at the freshness, and nothing is cut off.
        Assert.True(staffTop > headingTop, $"Expected the last count on a second line, it is at {staffTop} and the heading at {headingTop}.");
        Assert.True(clipRight <= freshnessLeft, $"The counts' room ends at {clipRight}, freshness starts at {freshnessLeft}.");
        Assert.True(staffRight <= clipRight, $"The last count ends at {staffRight}, past the room of {clipRight}.");
    }

    [Fact]
    public void TheChevronIsAtTheFarRightAfterTheFreshness()
    {
        var (freshnessRight, chevronLeft, chevronRight, headRight) = Draw(
            Screen([Kai, Jo]),
            root =>
            {
                var freshness = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == "up to date");
                var end = (Control)freshness.GetLogicalParent()!;
                var chevron = end.GetLogicalChildren().OfType<Viewbox>().Single();
                var head = (Control)end.GetLogicalParent()!;

                return (
                    freshness.TranslatePoint(new Point(freshness.Bounds.Width, 0), root)!.Value.X,
                    chevron.TranslatePoint(new Point(0, 0), root)!.Value.X,
                    chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), root)!.Value.X,
                    head.TranslatePoint(new Point(head.Bounds.Width, 0), root)!.Value.X);
            });

        Assert.True(chevronLeft >= freshnessRight, $"Freshness ends at {freshnessRight}, chevron starts at {chevronLeft}.");
        Assert.True(chevronRight >= headRight - 0.5, $"The chevron ends at {chevronRight}, the heading at {headRight}.");
    }

    [Fact]
    public void WithNoFreshnessTheChevronIsStillAtTheFarRight()
    {
        var (chevronRight, headRight) = Draw(
            Screen([Kai, Jo]) with { NotSynced = true },
            root =>
            {
                var heading = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == "Users (2)");
                var head = (Control)((Control)heading.GetLogicalParent()!).GetLogicalParent()!;
                var chevron = head.GetLogicalDescendants().OfType<Viewbox>().First();

                return (
                    chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), root)!.Value.X,
                    head.TranslatePoint(new Point(head.Bounds.Width, 0), root)!.Value.X);
            });

        Assert.True(chevronRight >= headRight - 0.5, $"The chevron ends at {chevronRight}, the heading at {headRight}.");
    }

    /// <summary>Where the Staff part and the freshness sit, the heights of the Staff part and the Users part, and where the counts' room ends.</summary>
    private static (double StaffRight, double FreshnessLeft, double StaffTop, double HeadingTop, double ClipRight) HeadingEdges(Control root)
    {
        var blocks = root.GetLogicalDescendants().OfType<TextBlock>().ToList();
        var staff = blocks.First(t => t.Text is not null && t.Text.StartsWith("Staff (", StringComparison.Ordinal));
        var freshness = blocks.First(t => t.Text == "up to date");
        var heading = blocks.First(t => t.Text is not null && t.Text.StartsWith("Users (", StringComparison.Ordinal));
        var counts = (Control)heading.GetLogicalParent()!;

        return (
            staff.TranslatePoint(new Point(staff.Bounds.Width, 0), root)!.Value.X,
            freshness.TranslatePoint(new Point(0, 0), root)!.Value.X,
            staff.TranslatePoint(new Point(0, 0), root)!.Value.Y,
            heading.TranslatePoint(new Point(0, 0), root)!.Value.Y,
            counts.TranslatePoint(new Point(counts.Bounds.Width, 0), root)!.Value.X);
    }

    /// <summary>A person with all three labels after the name: a rank, a flag and a heads-up.</summary>
    private static OverlayScreen WithThreeLabels(string name)
    {
        var who = Person(name) with
        {
            Flags = ["5 warns"],
            TrustRank = Modbot.Core.Users.TrustRank.KnownUser,
        };

        return Screen([who]) with
        {
            HeadsUps = [new HeadsUp("h1", "keep_an_eye", who.SubjectId, null, null, null, "Jo", Now)],
        };
    }

    /// <summary>Where the name ends, where the labels start and end and where the last one ends, and where the line ends.</summary>
    private static (double NameRight, double NameWidth, double NaturalNameWidth, double StripLeft, double StripRight, double LastLabelRight, double LineRight) NameLineEdges(Control root, string name)
    {
        var words = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == name);
        var line = (Control)words.GetLogicalParent()!;
        var strip = line.GetLogicalChildren().OfType<Control>().Last();
        var last = strip.GetLogicalChildren().OfType<Control>().Last();

        double X(Control c, double inside) => c.TranslatePoint(new Point(inside, 0), root)!.Value.X;

        // The same words on one line with all the room they want: what the name is when nothing trims it.
        var untrimmed = new TextBlock { Text = name, FontSize = words.FontSize, FontFamily = words.FontFamily, FontWeight = words.FontWeight, TextWrapping = TextWrapping.NoWrap };
        untrimmed.Measure(Size.Infinity);

        // A strip cut to nothing has no width but still sits its margin to the right of where it was put, which
        // is outside the line and not visible; its right edge is where it would end if it had no margin.
        var stripRight = strip.Bounds.Width > 0 ? X(strip, strip.Bounds.Width) : X(strip, 0) - strip.Margin.Left;

        return (X(words, words.Bounds.Width), words.Bounds.Width, untrimmed.DesiredSize.Width, X(strip, 0), stripRight, X(last, last.Bounds.Width), X(line, line.Bounds.Width));
    }

    [Theory]
    // Not the 520 panel width: the font a Linux runner falls back to is wider than Segoe UI and the
    // last label ends 7 pixels past the cut there. The headset width is the one this promises.
    [InlineData(OverlayView.HeadsetDesignWidth)]
    [InlineData(800)]
    [InlineData(1000)]
    public void ThreeLabelsAfterANameAreShownInFullAtTheHeadsetWidthAndWider(double width)
    {
        var e = Draw(WithThreeLabels("Kai"), root => NameLineEdges(root, "Kai"), width: width);

        Assert.True(e.LastLabelRight <= e.StripRight + 0.5, $"The last label ends at {e.LastLabelRight}, the cut is at {e.StripRight}.");
        Assert.True(e.NameRight <= e.StripLeft + 0.5, $"The name ends at {e.NameRight}, the labels start at {e.StripLeft}.");
    }

    [Theory]
    [InlineData(OverlayView.HeadsetDesignWidth)]
    [InlineData(800)]
    public void ALongNameIsTrimmedAndTheLabelsStayWhole(double width)
    {
        var long_ = new string('W', 80);
        var e = Draw(WithThreeLabels(long_), root => NameLineEdges(root, long_), width: width);

        Assert.True(e.LastLabelRight <= e.StripRight + 0.5, $"The last label ends at {e.LastLabelRight}, the cut is at {e.StripRight}.");
        Assert.True(e.NameRight <= e.StripLeft + 0.5, $"The name ends at {e.NameRight}, the labels start at {e.StripLeft}.");
        Assert.True(e.NameWidth < e.NaturalNameWidth - 1, $"The name is {e.NameWidth} wide of {e.NaturalNameWidth}: it was not trimmed.");
        Assert.True(e.NameWidth > 0);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(260)]
    [InlineData(340)]
    [InlineData(420)]
    public void AtANarrowWidthTheNameAndTheLabelsNeverOverlapAndNeitherRunsPastTheLine(double width)
    {
        var long_ = new string('W', 80);
        var e = Draw(WithThreeLabels(long_), root => NameLineEdges(root, long_), width: width);

        Assert.True(e.NameRight <= e.StripLeft + 0.5, $"The name ends at {e.NameRight}, the labels start at {e.StripLeft}.");
        Assert.True(e.StripRight <= e.LineRight + 0.5, $"The labels end at {e.StripRight}, the line at {e.LineRight}.");
    }

    [Fact]
    public void TheNameKeepsAStubOfItselfWhileTheLabelsAreCutToLeaveIt()
    {
        var long_ = new string('W', 80);
        var e = Draw(WithThreeLabels(long_), root => NameLineEdges(root, long_), width: 340);

        Assert.True(e.NameWidth >= 80, $"The name is only {e.NameWidth} wide.");
        Assert.True(e.LastLabelRight > e.StripRight, "The labels were expected to be cut at this width.");
    }

    /// <summary>A person with the rank, the 18+ mark and "Not in group" after the name, who was here before the moderator.</summary>
    private static OverlayScreen HereBeforeWithMarks(string name)
    {
        var who = Person(name) with
        {
            TrustRank = Modbot.Core.Users.TrustRank.KnownUser,
            EighteenPlus = true,
            IsMember = false,
        };

        return Screen([who]) with
        {
            ModeratorArrived = Now.AddMinutes(-64),
            Arrivals = new Dictionary<string, DateTimeOffset?> { [who.SubjectId] = null },
        };
    }

    /// <summary>The name, the marks that are never cut and the marks that are cut first, and where the line ends, with the width the never-cut marks want.</summary>
    private static (Rect Name, Rect Whole, double WholeWanted, Rect More, double LineRight, Rect Time) RowEdges(Control root, string name, string time)
    {
        var words = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == name);
        var line = (Control)words.GetLogicalParent()!;
        var parts = line.GetLogicalChildren().OfType<Control>().ToList();

        // A part cut to nothing has no width but still sits its margin to the right of where it was put, which
        // is outside the line and not visible; it is taken to be where it would be with no margin.
        Rect Of(Control c)
        {
            var at = c.TranslatePoint(new Point(0, 0), root) ?? default;
            return new(c.Bounds.Width > 0 ? at : at - new Point(c.Margin.Left, 0), c.Bounds.Size);
        }

        var timeBlock = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == time);

        // The never-cut strip's own margin is outside its box, so the width it wants is its box's.
        var wholeWanted = parts[1].DesiredSize.Width - parts[1].Margin.Left - parts[1].Margin.Right;

        return (Of(parts[0]), Of(parts[1]), wholeWanted, Of(parts[2]), Of(line).Right, Of(timeBlock));
    }

    [Fact]
    public void AHereBeforeYouRowShowsTwelveLettersOfTheNameAndWholeChipsAtTheDefaultWidth()
    {
        var name = "Cobaltfoxxlive";
        var (blockWidth, twelve, e) = Draw(HereBeforeWithMarks(name), root =>
        {
            var block = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == name);
            var first = new TextBlock { Text = name[..12], FontSize = block.FontSize, FontFamily = block.FontFamily, FontWeight = block.FontWeight, TextWrapping = TextWrapping.NoWrap };
            first.Measure(Size.Infinity);

            return (block.Bounds.Width, first.DesiredSize.Width, RowEdges(root, name, "~1h 4m+"));
        });

        Assert.True(blockWidth >= twelve - 0.5, $"The name is {blockWidth} wide, twelve letters of it need {twelve}.");
        Assert.True(e.Whole.Width >= e.WholeWanted - 0.5, $"The rank and the 18+ mark are {e.Whole.Width} wide of {e.WholeWanted}.");
        Assert.True(e.Whole.Right <= e.LineRight + 0.5, "The rank and the 18+ mark run past the line.");
        Assert.True(e.Whole.Right <= e.Time.Left + 0.5, "The time is not after the marks.");
    }

    [Theory]
    [InlineData(380)]
    [InlineData(420)]
    [InlineData(520)]
    public void ALongNameIsCutBeforeTheRankAndTheEighteenPlusMark(double width)
    {
        var name = new string('W', 80);
        var (blockWidth, natural, e) = Draw(HereBeforeWithMarks(name), root =>
        {
            var block = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == name);
            var untrimmed = new TextBlock { Text = name, FontSize = block.FontSize, FontFamily = block.FontFamily, FontWeight = block.FontWeight, TextWrapping = TextWrapping.NoWrap };
            untrimmed.Measure(Size.Infinity);

            return (block.Bounds.Width, untrimmed.DesiredSize.Width, RowEdges(root, name, "~1h 4m+"));
        }, width: width);

        Assert.True(blockWidth < natural - 1, "The name was not cut.");
        Assert.True(e.Whole.Width >= e.WholeWanted - 0.5, $"The rank and the 18+ mark are {e.Whole.Width} wide of {e.WholeWanted}.");
        Assert.True(e.Name.Right <= e.Whole.Left + 0.5, "The name runs into the marks.");
        Assert.True(e.Whole.Right <= e.More.Left + 0.5, "The cut marks start before the never-cut ones end.");
        Assert.True(e.More.Right <= e.LineRight + 0.5, "The cut marks run past the line.");
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
        Assert.InRange(cards[1].Bounds.Top - cards[0].Bounds.Bottom, 3, 5);
        Assert.InRange(cards[0].Bounds.Height, 38, 42);
    }

    [Fact]
    public void ACardIsOneLineWithTheTimeAtTheEndAndThePlusInsideIt()
    {
        var kai = Kai with { TrustRank = Modbot.Core.Users.TrustRank.KnownUser, EighteenPlus = true };
        var screen = Screen([kai, Jo]) with
        {
            ModeratorArrived = Now.AddMinutes(-5),
            Arrivals = new Dictionary<string, DateTimeOffset?> { ["usr_Kai"] = null, ["usr_Jo"] = Now.AddMinutes(-3) },
        };

        var (card, name, time, plus) = Draw(screen, root =>
        {
            var placed = OverlayTargets.Find(root);
            var card = placed.Single(p => p.Target is OverlayTarget.Person { SubjectId: "usr_Kai" }).Bounds;
            var plus = placed.Single(p => p.Target is OverlayTarget.AddHeadsUp { SubjectId: "usr_Kai" }).Bounds;

            Rect Of(string text)
            {
                var block = root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);
                return new Rect(block.TranslatePoint(new Point(0, 0), root) ?? default, block.Bounds.Size);
            }

            return (card, Of("Kai"), Of("~5m+"), plus);
        });

        Assert.InRange(card.Height, 38, 42);
        Assert.True(card.Contains(name) && card.Contains(time) && card.Contains(plus), "Something is outside the card.");
        Assert.True(name.Right <= time.Left + 0.5, "The time is not after the name.");
        Assert.True(time.Right <= plus.Left + 0.5, "The time is not before the plus.");
        Assert.True(Math.Abs(name.Center.Y - time.Center.Y) < 3, "The time is not on the name's line.");
        Assert.InRange(plus.Height, 30, 40);
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
