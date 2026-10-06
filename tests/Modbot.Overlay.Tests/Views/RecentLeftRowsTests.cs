using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The Instance list's rows for people who left in the last minute: where they sit, what they say,
/// what they are not counted in, and when the panel is drawn again for them. Everything is told
/// the time by the screen's own <c>Now</c>, never the system's.
/// </summary>
public class RecentLeftRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);

    private static RosterMember Person(string name, RosterStanding standing = RosterStanding.Ordinary)
        => new("usr_" + name, name, standing, 0, []);

    private static readonly RosterMember Rin = Person("Rin", RosterStanding.Flagged);
    private static readonly RosterMember Kai = Person("Kai", RosterStanding.Staff);
    private static readonly RosterMember Jo = Person("Jo");
    private static readonly RosterMember Ash = Person("Ash");

    private static RecentLeaver Left(RosterMember member, int secondsAgo = 18)
        => new(member, Now.AddSeconds(-secondsAgo));

    private static OverlayScreen Screen(
        IReadOnlyList<RosterMember> present,
        IReadOnlyList<RecentLeaver>? left,
        ListFilters? filters = null,
        OverlayPage page = OverlayPage.Instance,
        DateTimeOffset? now = null,
        bool canPlace = false)
        => new(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", present), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: page,
            RosterFilters: filters,
            Now: now ?? Now,
            CanPlaceHeadsUps: canPlace,
            Left: left);

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

    private static T Draw<T>(OverlayScreen screen, OverlayLook look, Func<Control, T> read)
        => AvaloniaTestHost.Run(() => read(OverlayView.Build(screen, null, look)));

    private static IReadOnlyList<string?> Texts(OverlayScreen screen, OverlayLook? look = null)
        => Draw<IReadOnlyList<string?>>(screen, look ?? OverlayLook.Headset, root =>
            [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)]);

    private static IReadOnlyList<OverlayTarget> Targets(OverlayScreen screen, OverlayLook? look = null)
        => Draw<IReadOnlyList<OverlayTarget>>(screen, look ?? OverlayLook.Headset, root =>
            [.. root.GetLogicalDescendants().OfType<Border>().Select(b => b.Tag).OfType<OverlayTarget>()]);

    [Fact]
    public void ThoseHereAreFollowedByThoseWhoLeft()
    {
        var all = ListFiltering.Everyone([Kai, Jo], [Left(Rin)]);

        Assert.Equal(["Kai", "Jo", "Rin"], all.Select(m => m.DisplayName));
    }

    [Fact]
    public void SomebodyOnBothListsIsShownOnceAsPresent()
    {
        var all = ListFiltering.Everyone([Kai, Rin], [Left(Rin)]);

        Assert.Equal(["Kai", "Rin"], all.Select(m => m.DisplayName));
    }

    [Fact]
    public void ARowWhoLeftSortsWhereItAlwaysDid()
    {
        // Flagged first: the row does not drop to the bottom just because they are gone.
        var shown = ListFiltering.Roster(
            ListFiltering.Everyone([Kai, Jo], [Left(Rin)]),
            ListFilters.None,
            new Dictionary<string, DateTimeOffset?>(),
            Now);

        Assert.Equal(["Rin", "Kai", "Jo"], shown.Select(m => m.DisplayName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLeftRowSaysLeftAndTheSecondsItHasLeft(bool inVRChatLook)
    {
        var texts = Texts(Screen([Kai, Jo], [Left(Rin, secondsAgo: 18)]), inVRChatLook ? VRChatLook() : null);

        Assert.Contains("Rin", texts);
        Assert.Contains("Left", texts);
        Assert.Contains("42s", texts);
    }

    [Fact]
    public void NoOneHasLeftMeansNoTagAndNoCountdown()
    {
        var texts = Texts(Screen([Kai, Jo], left: null));

        Assert.DoesNotContain("Left", texts);
        Assert.DoesNotContain(texts, t => t is { Length: > 1 } && t.EndsWith('s') && char.IsDigit(t[^2]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HereCountsOnlyThosePresent(bool inVRChatLook)
    {
        var texts = Texts(Screen([Kai, Jo], [Left(Rin), Left(Ash)]), inVRChatLook ? VRChatLook() : null);

        Assert.Contains("2 here", texts);
    }

    [Fact]
    public void WithAFilterOnlyThosePresentAreCountedAmongThoseShown()
    {
        // Not in a group: Jo, who is here, and Ash, who left. One of the two here is shown.
        var texts = Texts(Screen(
            [Kai, Jo],
            [Left(Ash), Left(Rin)],
            new ListFilters(Who: Who.NotInGroup)));

        Assert.Contains("1 of 2 here", texts);
        Assert.Contains("Ash", texts);
        Assert.DoesNotContain("Rin", texts);
    }

    [Fact]
    public void WhenEveryoneHasJustGoneTheirRowsStillShowAndNobodyIsHere()
    {
        var texts = Texts(Screen([], [Left(Rin)]));

        Assert.Contains("0 here", texts);
        Assert.Contains("Rin", texts);
        Assert.DoesNotContain("Nobody here.", texts);
    }

    [Fact]
    public void TappingALeftRowStillOpensThePerson()
    {
        var targets = Targets(Screen([Kai], [Left(Rin)]));

        Assert.Contains(new OverlayTarget.Person("usr_Rin"), targets);
    }

    [Fact]
    public void ALeftRowHasNoPlusForAHeadsUpButAPresentRowDoes()
    {
        var targets = Targets(Screen([Kai], [Left(Rin)], canPlace: true));

        Assert.Contains(new OverlayTarget.AddHeadsUp("usr_Kai"), targets);
        Assert.DoesNotContain(new OverlayTarget.AddHeadsUp("usr_Rin"), targets);
    }

    [Fact]
    public void SomebodyBackIsOneNormalRowWithNoTag()
    {
        // The roster lists them again, so the drive loop gives no leaver; and even if a screen
        // carried both, the row is drawn once, as present.
        var texts = Texts(Screen([Kai, Rin], [Left(Rin)]));

        Assert.Equal(1, texts.Count(t => t == "Rin"));
        Assert.DoesNotContain("Left", texts);
        Assert.Contains("2 here", texts);
    }

    [Fact]
    public void BothLooksDrawTheRowsWithoutThrowing()
    {
        foreach (var look in new[] { OverlayLook.Headset, VRChatLook() })
        {
            var pixels = AvaloniaTestHost.Run(() =>
            {
                using var renderer = new AvaloniaFrameRenderer(480, 640);
                return renderer.Render(OverlayView.Build(Screen([Kai, Jo], [Left(Rin)]), null, look)).ToArray();
            });

            Assert.Equal(480 * 640 * 4, pixels.Length);
            Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 3);
        }
    }

    [Fact]
    public void ALeftRowLooksDifferentFromAPresentOne()
    {
        // Greyed: the same person drawn as present and as just gone are not the same picture.
        byte[] Render(OverlayScreen screen) => AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(480, 640);
            return renderer.Render(OverlayView.Build(screen)).ToArray();
        });

        var here = Render(Screen([Kai, Rin], left: null));
        var gone = Render(Screen([Kai], [Left(Rin)]));

        Assert.False(here.AsSpan().SequenceEqual(gone));
    }

    [Fact]
    public void ThePanelIsDrawnAgainWhenTheCountdownMovesOnASecond()
    {
        var before = Screen([Kai], [Left(Rin)]);
        var later = Screen([Kai], [Left(Rin)], now: Now.AddSeconds(1));

        Assert.False(before.LooksTheSameAs(later));
    }

    [Fact]
    public void ThePanelIsNotDrawnAgainWhileTheSecondsStayTheSame()
    {
        var before = Screen([Kai], [Left(Rin)]);
        var soon = Screen([Kai], [Left(Rin)], now: Now.AddSeconds(0.25));

        Assert.True(before.LooksTheSameAs(soon));
    }

    [Fact]
    public void ThePanelIsDrawnAgainWhenSomebodyLeavesOrTheirRowGoes()
    {
        var nobody = Screen([Kai, Rin], left: null);
        var gone = Screen([Kai], [Left(Rin, secondsAgo: 0)]);
        var expired = Screen([Kai], left: null);

        Assert.False(nobody.LooksTheSameAs(gone));
        Assert.False(gone.LooksTheSameAs(expired));
    }

    [Fact]
    public void AnotherScreenIsNotDrawnAgainForTheCountdown()
    {
        // Only the Instance list shows the rows, so the Audit Log does not redraw every second.
        var before = Screen([Kai], [Left(Rin)], page: OverlayPage.Events);
        var later = Screen([Kai], [Left(Rin)], page: OverlayPage.Events, now: Now.AddSeconds(1));

        Assert.True(before.LooksTheSameAs(later));
    }
}
