using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Feedback;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App.Feedback;

/// <summary>
/// The draft the person changes before sending: the kind (Bug, Idea or Change), a title and the
/// details, with Cancel and Send. Nothing is saved until Send.
/// </summary>
/// <remarks>
/// <para>A window of its own in the app's colours, on top of whatever is in front (the person is in
/// VRChat when they speak), centred on the screen. It takes the keyboard, so it can be typed in.
/// Closing it any way other than Send (Cancel, its X, Escape) is Cancel.</para>
/// <para>No kind is chosen when the draft has none (AI off, or the server could not tidy it); Send
/// waits until one is chosen and there are details. When sending fails the window stays, with the
/// reason, so nothing written is lost.</para>
/// </remarks>
internal sealed class FeedbackDraftWindow : Window
{
    private readonly Action _cancel;
    private readonly Action<string?, string?, string?> _send;
    private readonly Dictionary<string, Button> _chips = [];
    private readonly TextBox _title = Ui.Input();
    private readonly TextBox _details = Ui.Input();
    private readonly TextBlock _problem = Ui.Text(string.Empty, Ui.T.Density.TextSmall, Ui.T.DangerBrush);
    private readonly Button _sendButton = Ui.Button("Send", primary: true);

    private string? _kind;
    private bool _closedByHost;

    /// <param name="draft">What to show to begin with.</param>
    /// <param name="cancel">Called once when the person cancels or closes the window.</param>
    /// <param name="send">Called with the kind, title and details as they now read, when the person sends.</param>
    public FeedbackDraftWindow(FeedbackDraft draft, Action cancel, Action<string?, string?, string?> send)
    {
        _cancel = cancel;
        _send = send;
        _kind = FeedbackKinds.Known(draft.Kind);

        Title = "Feedback";
        Icon = Brand.Icon();
        Width = 460;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.T.SurfaceBrush;

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (kind, caption) in new[]
                 {
                     (FeedbackKinds.Bug, "Bug"),
                     (FeedbackKinds.Idea, "Idea"),
                     (FeedbackKinds.Change, "Change"),
                 })
        {
            var chip = Ui.Button(caption);
            chip.Click += (_, _) => Choose(kind);
            _chips[kind] = chip;
            chips.Children.Add(chip);
        }

        _title.MaxLength = FeedbackController.MaxTitle;
        _title.Text = draft.Title;

        _details.MaxLength = FeedbackController.MaxDetails;
        _details.Text = draft.Details;
        _details.AcceptsReturn = true;
        _details.TextWrapping = TextWrapping.Wrap;
        _details.Height = 180;
        _details.Padding = new Thickness(10, 8);
        _details.VerticalContentAlignment = VerticalAlignment.Top;
        _details.TextChanged += (_, _) => Refresh();

        _problem.IsVisible = false;

        var cancelButton = Ui.Button("Cancel");
        cancelButton.Click += (_, _) => Close();

        _sendButton.Click += (_, _) => _send(_kind, _title.Text, _details.Text);

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                Close();
        };

        Closed += (_, _) =>
        {
            if (!_closedByHost)
                _cancel();
        };

        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(20),
            Children =
            {
                chips,
                Ui.Field("Title", _title),
                Ui.Field("Details", _details),
                _problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, _sendButton },
                },
            },
        };

        Refresh();
    }

    /// <summary>Whether the window is up because sending is in progress (it cannot be sent twice).</summary>
    public void SetSending(bool sending)
    {
        _sendButton.IsEnabled = !sending && CanSend();
        _title.IsEnabled = !sending;
        _details.IsEnabled = !sending;
    }

    /// <summary>The reason the last send failed, shown above the buttons; null hides it.</summary>
    public void ShowProblem(string? problem)
    {
        _problem.Text = problem ?? string.Empty;
        _problem.IsVisible = !string.IsNullOrEmpty(problem);
    }

    /// <summary>Closes the window because the controller moved on (sent, cancelled elsewhere or shut down); does not count as Cancel.</summary>
    public void CloseFromHost()
    {
        _closedByHost = true;
        Close();
    }

    private void Choose(string kind)
    {
        _kind = kind;
        Refresh();
    }

    private bool CanSend() => _kind is not null && !string.IsNullOrWhiteSpace(_details.Text);

    private void Refresh()
    {
        foreach (var (kind, chip) in _chips)
            Ui.SetPrimary(chip, kind == _kind);

        _sendButton.IsEnabled = CanSend();
    }
}
