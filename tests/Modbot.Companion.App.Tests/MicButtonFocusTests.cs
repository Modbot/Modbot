using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App.Tests;

/// <summary>
/// The Dictate and Feedback microphone buttons never hold the keyboard, so the space bar goes to
/// whatever is being typed into.
/// </summary>
/// <remarks>
/// A button that kept the keyboard after a click answered the space bar with a click of its own: a
/// space typed into a note started Dictate and was never typed (2026-11-20).
/// </remarks>
public sealed class MicButtonFocusTests
{
    [Fact]
    public void TheStripsRoundButtonsCannotTakeTheKeyboard()
    {
        AvaloniaTestHost.Run(() => Assert.False(DesktopOverlayWindow.SwitchButton().Focusable));
    }

    [Fact]
    public void TheDictateButtonOnTheOverlaysStripCannotTakeTheKeyboard()
    {
        AvaloniaTestHost.Run(() =>
        {
            var window = new DesktopOverlayWindow();

            // The Dictate button is part of the shared title strip, which exists once a screen is drawn.
            window.SetDictate(ButtonScreen.Shown with { Dictate = DictateState.Idle });
            window.Update(new OverlayScreen(
                "Cat Lounge",
                new Cached<InstanceContext>(new InstanceContext("39911", []), Freshness.Fresh, TimeSpan.Zero),
                Freshness.Fresh));

            var dictate = window.GetLogicalDescendants()
                .OfType<Control>()
                .Single(control => control.Tag is OverlayTarget.DictateButton);

            Assert.False(dictate.Focusable);
        });
    }

    [Fact]
    public void TheFeedbackButtonOnTheMainWindowCannotTakeTheKeyboard()
    {
        AvaloniaTestHost.Run(() =>
        {
            var window = new MainWindow();
            window.Show();
            window.Render(CompanionAppSnapshot.Empty, MainWindowActions.None);
            Dispatcher.UIThread.RunJobs();

            var feedback = window.GetLogicalDescendants()
                .OfType<Button>()
                .Single(button => button.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Feedback"));

            Assert.False(feedback.Focusable);
            window.Close();
        });
    }
}
