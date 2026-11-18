using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Modbot.Companion.App.Feedback;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App;

// The Feedback button at the top of the sidebar, under the group's name: a microphone and the word
// Feedback, shown only while the server's Send feedback permission lets this person use it.
public sealed partial class MainWindow
{
    // Built once and kept, because the window redraws on a timer and a button rebuilt every second
    // can lose the click on it.
    private readonly Button _feedbackButton = new();
    private readonly DispatcherTimer _feedbackMotion = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private FeedbackCard _feedback = FeedbackCard.Off;
    private int _feedbackFrame;
    private bool _feedbackWired;

    /// <summary>The Feedback button was pressed.</summary>
    internal event Action? FeedbackPressed;

    /// <summary>
    /// What the Feedback button shows, from the card the controller last gave: not offered hides it,
    /// and the picture moves a step each 150 ms while the card says it has something moving.
    /// </summary>
    internal void SetFeedback(FeedbackCard card)
    {
        _feedback = card;
        _feedbackButton.IsVisible = card.Offered;

        if (card.Moves)
        {
            _feedbackMotion.Start();
        }
        else
        {
            _feedbackMotion.Stop();
            _feedbackFrame = 0;
        }

        PaintFeedback();
    }

    /// <summary>The button, hidden until it is offered, for the top of the sidebar.</summary>
    private Control FeedbackButton()
    {
        if (!_feedbackWired)
        {
            _feedbackWired = true;
            _feedbackButton.IsVisible = false;
            _feedbackButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            _feedbackButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            _feedbackButton.Height = Ui.T.Density.ControlHeight;
            _feedbackButton.Padding = new Thickness(8, 0);
            _feedbackButton.Margin = new Thickness(0, 0, 0, 8);
            _feedbackButton.BorderThickness = new Thickness(Ui.T.Density.Hairline);
            _feedbackButton.CornerRadius = new CornerRadius(Ui.T.Density.Radius);
            _feedbackButton.Click += (_, _) => FeedbackPressed?.Invoke();
            _feedbackMotion.Tick += (_, _) =>
            {
                _feedbackFrame++;
                PaintFeedback();
            };
            Closed += (_, _) => _feedbackMotion.Stop();
            PaintFeedback();
        }

        return _feedbackButton;
    }

    private void PaintFeedback()
    {
        var ink = Ui.T.TextBrush;
        var bright = Ui.T.AccentBrush;
        var listening = _feedback.Picture is DictateState.Listening;

        _feedbackButton.Background = listening ? Ui.T.Surface2Brush : Ui.T.BackgroundBrush;
        _feedbackButton.BorderBrush = listening ? bright : Ui.T.BorderBrush;

        var picture = ButtonView.Dictation(_feedback.Picture, _feedbackFrame, ink, bright, _feedback.Level);
        picture.Width = picture.Height = 20;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                picture,
                Ui.Text(_feedback.Label, Ui.T.Density.TextBase, ink, FontWeight.Medium, wrap: false),
            },
        };

        if (_feedback.PercentText.Length > 0)
            row.Children.Add(Ui.Text(_feedback.PercentText, Ui.T.Density.TextSmall, ink, FontWeight.Medium, wrap: false));
        else if (listening)
            row.Children.Add(ButtonView.Meter(_feedback.Level, bright, Ui.T.Surface3Brush, 4, 12));

        _feedbackButton.Content = row;
    }
}
