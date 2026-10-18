using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>
/// The question the Quit button asks while Close to tray is on: quit for real, or carry on.
/// </summary>
/// <remarks>
/// A window of its own over the main one, in the app's own colours. Closing it any other way than
/// the Quit button (its X, Escape) counts as Cancel, so a stray key never stops the client.
/// </remarks>
internal sealed class QuitWindow : Window
{
    private QuitWindow()
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.T.SurfaceBrush;

        var question = Ui.Text(CloseChoices.QuitQuestion, Ui.T.Density.TextSmall, Ui.T.TextBrush);
        question.MaxWidth = 300;

        var quit = Ui.Button("Quit", danger: true);
        quit.Click += (_, _) => Close(true);

        var cancel = Ui.Button("Cancel", primary: true);
        cancel.Click += (_, _) => Close(false);

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                Close(false);
        };

        Content = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(20),
            Children =
            {
                question,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, quit },
                },
            },
        };
    }

    /// <summary>True when the moderator chose Quit; false for Cancel or any other way of closing it.</summary>
    public static async Task<bool> AskAsync(Window owner)
        => await new QuitWindow().ShowDialog<bool>(owner);
}
