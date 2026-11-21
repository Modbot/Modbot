using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Modbot.Companion.Presentation;

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

            var dictate = window.GetLogicalDescendants()
                .OfType<Button>()
                .Single(button => ToolTip.GetTip(button) as string == "Dictate");

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
