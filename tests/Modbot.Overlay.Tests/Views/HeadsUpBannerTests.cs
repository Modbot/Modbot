using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The heads-up banner above the Instance list: a long note wraps and stays left of Clear, in
/// both looks.
/// </summary>
public class HeadsUpBannerTests
{
    private const double PanelWidth = 520;
    private const double PanelHeight = 720;

    private const string LongNote = "4th person in a row to have august 0 on the account and a very long note after it that cannot fit on one line";

    private static OverlayScreen Screen(string? note)
        => new(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", [new RosterMember("usr_Jo", "Jo", RosterStanding.Ordinary, 0, [])]), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: OverlayPage.Instance,
            HeadsUps: [new HeadsUp("h1", "keep_an_eye", "usr_Jo", "RAyQuan", note, null, "Kai", new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero))],
            CanPlaceHeadsUps: true);

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

    public static TheoryData<bool> Looks => new() { true, false };

    /// <summary>The note's text block and the Clear button, in the panel's own coordinates.</summary>
    private static (Rect Note, Rect Clear) Laid(OverlayLook look)
        => AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(Screen(LongNote), null, look);
            root.Measure(new Size(PanelWidth, PanelHeight));
            root.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));

            var note = root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == LongNote);
            var origin = note.TranslatePoint(new Point(0, 0), root)!.Value;
            var clear = OverlayTargets.Find(root).First(p => p.Target is OverlayTarget.ClearHeadsUp).Bounds;

            return (new Rect(origin, note.Bounds.Size), clear);
        });

    [Theory]
    [MemberData(nameof(Looks))]
    public void ALongNoteStaysLeftOfClear(bool headset)
    {
        var (note, clear) = Laid(headset ? OverlayLook.Headset : VRChatLook());

        Assert.True(note.Right <= clear.Left, $"The note ends at {note.Right}, past Clear at {clear.Left}.");
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void ALongNoteWrapsOntoMoreLines(bool headset)
    {
        var (note, _) = Laid(headset ? OverlayLook.Headset : VRChatLook());

        // Taller than one line of the base text.
        Assert.True(note.Height > 24, $"The note is {note.Height} high, which is one line.");
    }
}
