using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The show and hide button's face, in both states and both looks: "Hide" while the panel is up,
/// "Show" while it is down, the shortcut under the label, and one thing to point at.
/// </summary>
public class ButtonViewTests
{
    private const double Size = OverlayButton.PanelPixels;

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

    private static Control Laid(ButtonScreen screen, OverlayLook look)
    {
        var root = ButtonView.Build(screen, look);
        root.Measure(new Size(Size, Size));
        root.Arrange(new Rect(0, 0, Size, Size));
        return root;
    }

    private static TextBlock Words(Control root, string text)
        => root.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == text);

    [Theory]
    [MemberData(nameof(Looks))]
    public void WhileThePanelIsUpTheButtonOffersToHideIt(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            var root = Laid(ButtonScreen.Shown, headset ? OverlayLook.Headset : VRChatLook());

            Assert.NotNull(Words(root, "Hide"));
            Assert.DoesNotContain(root.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Show");
        });
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void WhileThePanelIsDownTheButtonOffersToShowIt(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            var root = Laid(ButtonScreen.Hidden, headset ? OverlayLook.Headset : VRChatLook());

            Assert.NotNull(Words(root, "Show"));
            Assert.DoesNotContain(root.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Hide");
        });
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void TheShortcutSitsUnderTheLabelInBothStates(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            foreach (var screen in new[] { ButtonScreen.Shown, ButtonScreen.Hidden })
            {
                var root = Laid(screen, headset ? OverlayLook.Headset : VRChatLook());
                var label = Words(root, screen.Label);
                var shortcut = Words(root, OverlayButton.DefaultShortcut);

                var labelTop = label.TranslatePoint(new Point(0, 0), root)!.Value.Y;
                var shortcutTop = shortcut.TranslatePoint(new Point(0, 0), root)!.Value.Y;

                Assert.True(shortcutTop >= labelTop + label.Bounds.Height - 1, "The shortcut starts under the label.");
            }
        });
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void TheWholeFaceIsOneThingToPointAtAndFitsThePanel(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            foreach (var screen in new[] { ButtonScreen.Shown, ButtonScreen.Hidden })
            {
                var root = Laid(screen, headset ? OverlayLook.Headset : VRChatLook());
                var targets = OverlayTargets.Find(root);

                var face = Assert.Single(targets);
                Assert.IsType<OverlayTarget.PanelButton>(face.Target);
                Assert.True(face.Bounds.Left >= 0 && face.Bounds.Top >= 0 && face.Bounds.Right <= Size && face.Bounds.Bottom <= Size);

                // The words fit inside the face rather than running off it.
                foreach (var text in root.GetLogicalDescendants().OfType<TextBlock>())
                {
                    var at = text.TranslatePoint(new Point(0, 0), root)!.Value;
                    Assert.True(face.Bounds.Contains(at) && face.Bounds.Contains(new Point(at.X + text.Bounds.Width - 1, at.Y + text.Bounds.Height - 1)),
                        $"\"{text.Text}\" runs off the face.");
                }
            }
        });
    }

    [Fact]
    public void TheCursorIsDrawnOnTopWhenAHandPointsAtTheButton()
    {
        AvaloniaTestHost.Run(() =>
        {
            var root = ButtonView.Build(ButtonScreen.Shown, OverlayLook.Headset, new PanelCursor(0.5f, 0.5f));

            Assert.IsType<Panel>(root);
            Assert.Equal(2, ((Panel)root).Children.Count);
        });
    }

    [Fact]
    public void TheShortcutTextComesFromOnePlace()
    {
        Assert.Equal(OverlayButton.DefaultShortcut, ButtonScreen.Shown.Shortcut);
        Assert.Equal(OverlayButton.DefaultShortcut, ButtonScreen.Hidden.Shortcut);
        Assert.Equal("Right stick back 5s", OverlayButton.DefaultShortcut);
    }

    [Fact]
    public void TheLineUnderTheLabelIsTheShortcutAndWhileCountingItSaysWhatWillHappenAndWhen()
    {
        Assert.Equal(OverlayButton.DefaultShortcut, ButtonScreen.Shown.Line);

        Assert.Equal("Hide in 3", (ButtonScreen.Shown with { Countdown = new ButtonCountdown(3, 0.4f) }).Line);
        Assert.Equal("Show in 5", (ButtonScreen.Hidden with { Countdown = new ButtonCountdown(5, 0f) }).Line);
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void WhileCountingTheCountReplacesTheShortcutAndABarFills(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            var look = headset ? OverlayLook.Headset : VRChatLook();
            var screen = ButtonScreen.Shown with { Countdown = new ButtonCountdown(2, 0.5f) };
            var root = Laid(screen, look);

            Assert.NotNull(Words(root, "Hide in 2"));
            Assert.DoesNotContain(root.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == OverlayButton.DefaultShortcut);

            var idle = Laid(ButtonScreen.Shown, look);
            Assert.DoesNotContain(idle.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Hide in 2");
            Assert.True(
                root.GetLogicalDescendants().OfType<Border>().Count() > idle.GetLogicalDescendants().OfType<Border>().Count(),
                "The bar is drawn only while counting.");
        });
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void TheLongestShortcutWordsAreShrunkToFitTheCardNotCutOff(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            var words = new ButtonShortcut(ShortcutStick.Left, StickDirection.Forward, 8).Text;
            var root = Laid(ButtonScreen.Shown with { Shortcut = words }, headset ? OverlayLook.Headset : VRChatLook());

            var box = root.GetLogicalDescendants().OfType<Viewbox>().Single(v => v.Child is TextBlock t && t.Text == words);
            var left = box.TranslatePoint(new Point(0, 0), root)!.Value.X;
            var right = left + box.Bounds.Width;

            Assert.True(left >= 0 && right <= OverlayButton.PanelPixels, $"The words run from {left} to {right}.");
        });
    }

    [Fact]
    public void WithNoShortcutThereIsNothingUnderTheLabel()
    {
        AvaloniaTestHost.Run(() =>
        {
            var root = Laid(ButtonScreen.Shown with { Shortcut = "" }, OverlayLook.Headset);

            Assert.Equal(["Hide"], root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray());
        });
    }
}
