using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The Audit Log shows nobody twice for one visit (a "here before you" row after a Joined row, or
/// the moderator as here before themself), and shows the 18+ mark on the rows of those who carry it,
/// in both looks.
/// </summary>
public class AuditLogRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 30, 0, TimeSpan.Zero);

    private const string Me = "usr_Me";

    private static LiveEvent Event(string id, string kind, string name, int minutesAgo, bool? eighteenPlus = null)
        => new(
            id,
            id,
            kind,
            Now.AddMinutes(-minutesAgo),
            "39911",
            new LivePerson("usr_" + name, name, "Trusted User", RosterStanding.Ordinary, 0, [], eighteenPlus),
            false,
            null,
            false);

    private static List<string> Ids(params LiveEvent[] newestFirst)
        => [.. ListFiltering.WithoutRepeatedHere(newestFirst, Me).Select(e => e.Id)];

    [Fact]
    public void AHereBeforeYouRowIsDroppedForSomebodyWhoAlreadyHasAJoinedRow()
    {
        // Newest first: the restatement at 18:10, after the joins at 18:05 and 18:08.
        Assert.Equal(["2", "1"], Ids(
            Event("3", LiveEventKinds.PersonHere, "Cineko", 20),
            Event("2", LiveEventKinds.PersonJoined, "Kai", 21),
            Event("1", LiveEventKinds.PersonJoined, "Cineko", 25)));
    }

    [Fact]
    public void ARepeatedHereBeforeYouRowIsDroppedAndTheFirstKept()
    {
        Assert.Equal(["1"], Ids(
            Event("2", LiveEventKinds.PersonHere, "Kai", 5),
            Event("1", LiveEventKinds.PersonHere, "Kai", 30)));
    }

    [Fact]
    public void AHereBeforeYouRowIsKeptForSomebodyNoOtherRowMentions()
    {
        Assert.Equal(["2", "1"], Ids(
            Event("2", LiveEventKinds.PersonHere, "Kai", 5),
            Event("1", LiveEventKinds.PersonJoined, "Jo", 30)));
    }

    [Fact]
    public void ThereIsNoHereBeforeYouRowForTheModeratorThemself()
    {
        Assert.Equal(["1"], Ids(
            Event("2", LiveEventKinds.PersonHere, "Me", 5),
            Event("1", LiveEventKinds.PersonJoined, "Kai", 30)));
    }

    [Fact]
    public void AHereBeforeYouRowIsKeptForSomebodyWhoLeftAndIsStatedAsHereAgain()
    {
        Assert.Equal(["3", "2", "1"], Ids(
            Event("3", LiveEventKinds.PersonHere, "Kai", 2),
            Event("2", LiveEventKinds.PersonLeft, "Kai", 10),
            Event("1", LiveEventKinds.PersonJoined, "Kai", 30)));
    }

    [Fact]
    public void WithNoHereBeforeYouRowsTheListIsReturnedAsItCame()
    {
        IReadOnlyList<LiveEvent> log = [Event("2", LiveEventKinds.PersonLeft, "Kai", 5), Event("1", LiveEventKinds.PersonJoined, "Kai", 30)];

        Assert.Same(log, ListFiltering.WithoutRepeatedHere(log, Me));
    }

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

    private static IReadOnlyList<string?> Texts(IReadOnlyList<LiveEvent> events, OverlayLook look)
    {
        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", []), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: OverlayPage.Events,
            Events: events,
            Now: Now);

        return AvaloniaTestHost.Run<IReadOnlyList<string?>>(() =>
            [.. OverlayView.Build(screen, null, look).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAuditLogRowShowsTheEighteenPlusMarkOnlyOnThoseWhoCarryIt(bool inVRChatLook)
    {
        var look = inVRChatLook ? VRChatLook() : OverlayLook.Headset;

        var marked = Texts([Event("1", LiveEventKinds.PersonJoined, "Kai", 5, eighteenPlus: true)], look);
        var unmarked = Texts([Event("1", LiveEventKinds.PersonJoined, "Kai", 5, eighteenPlus: false)], look);
        var unknown = Texts([Event("1", LiveEventKinds.PersonJoined, "Kai", 5)], look);

        Assert.Contains("18+", marked);
        Assert.DoesNotContain("18+", unmarked);
        Assert.DoesNotContain("18+", unknown);
    }
}
