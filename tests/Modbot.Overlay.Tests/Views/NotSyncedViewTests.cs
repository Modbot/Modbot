using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The panel for an instance no group owns: the note, and what is left off because a group's
/// server is not the source of the list.
/// </summary>
public class NotSyncedViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);

    private static RosterMember Person(string name) => new("usr_" + name, name, RosterStanding.Ordinary, 0, []);

    private static OverlayScreen Screen(bool notSynced, OverlayPage page = OverlayPage.Instance, bool canPlace = false)
        => new(
            null,
            new Cached<InstanceContext>(new InstanceContext("12345", [Person("Jo"), Person("Kai")]), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: page,
            Now: Now,
            CanPlaceHeadsUps: canPlace,
            NotSynced: notSynced);

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
    public void TheNoteIsSaidOnceUnderTheHeadingInTheHeadsetLook()
    {
        var texts = Texts(Screen(notSynced: true));

        Assert.Single(texts, "Not synced with the group");
        Assert.Contains("Not in a group instance", texts);
    }

    [Fact]
    public void InVRChatsLookTheNoteIsATagInTheTitleStripAndNotOnThePanel()
    {
        // The window's strip draws the tag (DesktopOverlayWindow); the panel keeps the one heading.
        var texts = Texts(Screen(notSynced: true), VRChatLook());

        Assert.DoesNotContain("Not synced with the group", texts);
        Assert.Single(texts, "Not in a group instance");
    }

    [Fact]
    public void InVRChatsLookAGroupsNameIsInTheStripAndNotAlsoHeadingThePanel()
    {
        var group = Screen(notSynced: false) with { GroupLabel = "Thy Kingdom" };

        Assert.DoesNotContain("Thy Kingdom", Texts(group, VRChatLook()));
        Assert.DoesNotContain("Not in a group instance", Texts(group, VRChatLook()));
        Assert.Contains("Thy Kingdom", Texts(group));
    }

    [Fact]
    public void TheNoteIsOnTheWristPanelToo()
    {
        var texts = Texts(Screen(notSynced: true, page: OverlayPage.Wrist));

        Assert.Single(texts, "Not synced with the group");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGroupInstanceHasNoNote(bool inVRChatLook)
    {
        var texts = Texts(Screen(notSynced: false), inVRChatLook ? VRChatLook() : null);

        Assert.DoesNotContain("Not synced with the group", texts);
    }

    [Fact]
    public void TheNoteIsTheOnlyNewWordOnThePanel()
    {
        // Every word on the log's panel is a word the group's panel already has, bar the note.
        var synced = Texts(Screen(notSynced: false));
        var local = Texts(Screen(notSynced: true));

        Assert.Equal(["Not synced with the group"], local.Except(synced));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePeopleAreListedAndTheCountIsOfThemWithNoAge(bool inVRChatLook)
    {
        var texts = Texts(Screen(notSynced: true), inVRChatLook ? VRChatLook() : null);

        Assert.Contains("Jo", texts);
        Assert.Contains("Kai", texts);
        Assert.Contains(inVRChatLook ? "Users (2)" : "2 here", texts);
        Assert.DoesNotContain("up to date", texts);
    }

    [Fact]
    public void ARowStillOpensThePersonsCard()
    {
        var targets = Targets(Screen(notSynced: true));

        Assert.Contains(targets, t => t is OverlayTarget.Person { SubjectId: "usr_Jo" });
    }

    [Fact]
    public void NoRowHasAHeadsUpPlus()
    {
        // The driver sets the control off for a screen made from the log, and the rows draw none.
        var targets = Targets(Screen(notSynced: true, canPlace: false));

        Assert.DoesNotContain(targets, t => t is OverlayTarget.AddHeadsUp);
    }

    [Fact]
    public void TheFiltersForWhoAndRankAreLeftOffTheList()
    {
        var parts = Targets(Screen(notSynced: true)).OfType<OverlayTarget.Filter>().Select(f => f.Part).ToList();

        Assert.DoesNotContain(FilterPart.Who, parts);
        Assert.DoesNotContain(FilterPart.Rank, parts);
        Assert.Contains(FilterPart.Time, parts);
        Assert.Contains(FilterPart.Sort, parts);
    }

    [Fact]
    public void AGroupInstanceKeepsTheWhoAndRankFilters()
    {
        var parts = Targets(Screen(notSynced: false)).OfType<OverlayTarget.Filter>().Select(f => f.Part).ToList();

        Assert.Contains(FilterPart.Who, parts);
        Assert.Contains(FilterPart.Rank, parts);
    }

    [Fact]
    public void ThePanelIsNotIdleAndRedrawsWhenItBecomesSynced()
    {
        var local = Screen(notSynced: true);

        Assert.False(local.IsIdle);
        Assert.False(local.LooksTheSameAs(Screen(notSynced: false) with { GroupLabel = null }));
        Assert.True(local.LooksTheSameAs(Screen(notSynced: true)));
        Assert.True(OverlayScreen.Idle.IsIdle);
    }
}
