using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Dictation;
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
    private const double Size = OverlayButton.CardPixels;

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

            Assert.True(left >= 0 && right <= OverlayButton.CardPixels, $"The words run from {left} to {right}.");
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

    [Fact]
    public void WithDictateOffTheScreenIsTheOldOneAndHasNoDictateWords()
    {
        Assert.Equal(DictateState.Off, ButtonScreen.Shown.Dictate);
        Assert.Equal(new ButtonScreen(true, OverlayButton.DefaultShortcut), ButtonScreen.Shown);
        Assert.Equal(ButtonScreen.Shown, ButtonScreen.Shown.WithDictate(null, 40));
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void WithDictateOffTheCardHoldsOneFaceAndNoDictateWords(bool headset)
    {
        AvaloniaTestHost.Run(() =>
        {
            var root = Laid(ButtonScreen.Shown, headset ? OverlayLook.Headset : VRChatLook());

            Assert.DoesNotContain(OverlayTargets.Find(root), p => p.Target is OverlayTarget.DictateButton);
            Assert.DoesNotContain(root.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Dictate");
        });
    }

    [Fact]
    public void TheControllerStateDecidesTheDictateState()
    {
        var shown = ButtonScreen.Shown;

        Assert.Equal(DictateState.Idle, shown.WithDictate(DictationState.Idle, 0).Dictate);
        Assert.Equal(DictateState.Listening, shown.WithDictate(DictationState.Listening, 0).Dictate);
        Assert.Equal(DictateState.Writing, shown.WithDictate(DictationState.Writing, 0).Dictate);

        var preparing = shown.WithDictate(DictationState.Preparing, 42);
        Assert.Equal(DictateState.GettingReady, preparing.Dictate);
        Assert.Equal(42, preparing.Percent);
    }

    [Fact]
    public void TheDictateWordsAreLabelsAndThePercentShowsOnlyWhileGettingReady()
    {
        var shown = ButtonScreen.Shown;

        Assert.Equal("Dictate", shown.WithDictate(DictationState.Idle, 0).DictateLabel);
        Assert.Equal("Listening", shown.WithDictate(DictationState.Listening, 0).DictateLabel);
        Assert.Equal("Writing", shown.WithDictate(DictationState.Writing, 0).DictateLabel);
        Assert.Equal("Getting ready", shown.WithDictate(DictationState.Preparing, 7).DictateLabel);

        Assert.Equal("7%", shown.WithDictate(DictationState.Preparing, 7).DictatePercent);
        Assert.Equal("100%", shown.WithDictate(DictationState.Preparing, 250).DictatePercent);
        Assert.Equal("", shown.WithDictate(DictationState.Listening, 7).DictatePercent);
    }

    [Theory]
    [InlineData(DictateState.Idle, true, "Dictate")]
    [InlineData(DictateState.Idle, false, "Dictate")]
    [InlineData(DictateState.Listening, true, "Listening")]
    [InlineData(DictateState.Writing, false, "Writing")]
    [InlineData(DictateState.GettingReady, true, "Getting ready")]
    public void WithDictateOnTheCardHasTwoRowsToPointAtAndTheirWordsFit(DictateState state, bool headset, string label)
    {
        AvaloniaTestHost.Run(() =>
        {
            var screen = ButtonScreen.Shown with { Dictate = state, Percent = 30 };
            var root = Laid(screen, headset ? OverlayLook.Headset : VRChatLook());
            var targets = OverlayTargets.Find(root);

            var first = Assert.Single(targets, p => p.Target is OverlayTarget.PanelButton);
            var second = Assert.Single(targets, p => p.Target is OverlayTarget.DictateButton);
            Assert.Equal(2, targets.Count);
            Assert.True(first.Bounds.Bottom <= second.Bounds.Top + 1, "The Dictate row is under the show and hide row.");

            foreach (var row in new[] { first, second })
                Assert.True(row.Bounds.Left >= 0 && row.Bounds.Top >= 0 && row.Bounds.Right <= Size && row.Bounds.Bottom <= Size);

            Assert.NotNull(Words(root, label));
            Assert.NotNull(Words(root, screen.Label));

            // Every line sits in a box that shrinks it to fit, so the box is what is measured.
            foreach (var box in root.GetLogicalDescendants().OfType<Viewbox>().Where(b => b.Child is TextBlock))
            {
                var at = box.TranslatePoint(new Point(0, 0), root)!.Value;
                var row = first.Bounds.Contains(at) ? first : second;
                Assert.True(row.Bounds.Contains(at) && row.Bounds.Contains(new Point(at.X + box.Bounds.Width - 1, at.Y + box.Bounds.Height - 1)),
                    $"\"{((TextBlock)box.Child!).Text}\" runs off its row.");
            }
        });
    }

    [Fact]
    public void WhileGettingReadyThePercentShowsAndTheMovingPicturesChangeWithTheFrame()
    {
        AvaloniaTestHost.Run(() =>
        {
            var preparing = ButtonScreen.Shown with { Dictate = DictateState.GettingReady, Percent = 64 };
            Assert.NotNull(Words(Laid(preparing, OverlayLook.Headset), "64%"));

            var listening = ButtonScreen.Shown with { Dictate = DictateState.Listening };
            var quiet = Laid(listening with { Frame = 0 }, OverlayLook.Headset);
            var loud = Laid(listening with { Frame = 2 }, OverlayLook.Headset);

            static double[] Opacities(Control root)
                => root.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Select(p => p.Opacity).ToArray();

            Assert.NotEqual(Opacities(quiet), Opacities(loud));
        });
    }
}
