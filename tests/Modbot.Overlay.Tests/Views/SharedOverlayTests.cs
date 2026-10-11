using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The headset panel and the desktop window say the same things: one count of who is here, no "here
/// before you" said to the moderator about themself, and the headset's VRChat look drawn bigger with
/// taps still landing where things are drawn.
/// </summary>
public class SharedOverlayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);

    private static RosterMember Person(string name) => new("usr_" + name, name, RosterStanding.Ordinary, 0, []);

    private static OverlayLook VRChatLook() => OverlayLook.FromColours(new OverlayColours(
        Panel: new PaletteColour(0x18, 0x12, 0x24),
        Bar: new PaletteColour(0x10, 0x0C, 0x1A),
        Button: new PaletteColour(0x2A, 0x20, 0x44),
        Border: new PaletteColour(0x38, 0x2C, 0x5A),
        Hover: new PaletteColour(0x30, 0x26, 0x4E),
        Selected: new PaletteColour(0x50, 0x3A, 0x90),
        SelectedBorder: new PaletteColour(0x80, 0x60, 0xE0),
        SelectedText: new PaletteColour(0xF5, 0xF5, 0xF5),
        Icon: new PaletteColour(0xA0, 0x90, 0xD0),
        Text: new PaletteColour(0xF0, 0xF0, 0xF8),
        Subtext: new PaletteColour(0xB0, 0xA8, 0xC8)));

    /// <summary>Three people here, one more who has just left, the moderator (Kai) first in the list.</summary>
    private static OverlayScreen Instance(string? moderatorId = "usr_Kai") => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [Person("Kai"), Person("Jo"), Person("Ren")]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh,
        Arrivals: new Dictionary<string, DateTimeOffset?>
        {
            // Kai was here before the log began, and so was Jo.
            ["usr_Kai"] = null,
            ["usr_Jo"] = null,
            ["usr_Ren"] = Now.AddMinutes(-3),
        },
        Left: [new RecentLeaver(Person("Mo"), Now.AddSeconds(-10))],
        Now: Now,
        ModeratorArrived: Now.AddMinutes(-64),
        ModeratorId: moderatorId);

    private static IReadOnlyList<string?> Texts(Control root)
        => [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)];

    [Fact]
    public void TheCountIsThePeopleHereAndNotThoseWhoJustLeft()
    {
        Assert.Equal(3, Instance().HereCount);
    }

    [Fact]
    public void BothLooksSayTheSameCount()
    {
        var modbot = AvaloniaTestHost.Run(() => Texts(OverlayView.Build(Instance(), null, OverlayLook.Headset)));
        var vrchat = AvaloniaTestHost.Run(() => Texts(OverlayView.Build(Instance(), null, VRChatLook())));

        Assert.Contains("3 here", modbot);
        Assert.Contains("Users (3)", vrchat);
    }

    [Fact]
    public void TheModeratorIsNotToldTheyWereHereBeforeThemselves()
    {
        var texts = AvaloniaTestHost.Run(() => Texts(OverlayView.Build(Instance(), null, VRChatLook())));

        // Jo's row says it, Kai's own row does not.
        Assert.Single(texts, t => t is not null && t.Contains("here before you", StringComparison.Ordinal));
    }

    [Fact]
    public void SomebodyElseIsStillToldSoWhenThereIsNoModeratorId()
    {
        var texts = AvaloniaTestHost.Run(() => Texts(OverlayView.Build(Instance(moderatorId: null), null, VRChatLook())));

        Assert.Equal(2, texts.Count(t => t is not null && t.Contains("here before you", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheModbotLookIsTheScreenAsItWas()
    {
        var root = AvaloniaTestHost.Run(() => OverlayView.BuildForHeadset(Instance(), null, OverlayLook.Headset, 1024));
        var plain = AvaloniaTestHost.Run(() => OverlayView.Build(Instance(), null, OverlayLook.Headset));

        Assert.Equal(plain.GetType(), root.GetType());
    }

    [Fact]
    public void TheVRChatLookIsDrawnAtTheTextureWidthWithTheGroupNameInAStrip()
    {
        var (size, texts) = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.BuildForHeadset(Instance(), null, VRChatLook(), 1024);
            root.Measure(new Size(1024, 1024));
            return (root.DesiredSize, Texts(root));
        });

        Assert.Equal(1024, size.Width, 1);
        Assert.Contains("Cat Lounge", texts);
        Assert.Contains("Users (3)", texts);
    }

    [Fact]
    public void ATapOnWhatIsDrawnLandsOnItInTheBiggerLook()
    {
        AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(1024, 1024);
            var root = OverlayView.BuildForHeadset(Instance(), null, VRChatLook(), 1024);
            renderer.Render(root);

            var found = OverlayTargets.Find(root);
            Assert.NotEmpty(found);

            foreach (var placed in found)
            {
                // Every target is where the texture is, not where the smaller layout put it.
                Assert.True(placed.Bounds.Right <= 1024.5, "a target stays inside the texture");
                Assert.NotNull(OverlayTargets.At(root, placed.Bounds.Center));
            }

            // The layout is 600 wide drawn at 1024, so something must be further right than 600.
            Assert.Contains(found, placed => placed.Bounds.Right > OverlayView.HeadsetDesignWidth);
        });
    }
}
