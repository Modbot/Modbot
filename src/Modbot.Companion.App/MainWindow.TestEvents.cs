using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Modbot.Companion.Presentation;
using Modbot.Core.Users;

namespace Modbot.Companion.App;

/// <summary>
/// The Debug page's Test events card: a made-up join, leave, flagged join, problem or heads-up,
/// sent through the same paths on this PC that a real one takes (<see cref="TestEvents"/>).
/// </summary>
/// <remarks>
/// <para>Only on the Debug page, which only exists with <c>MODBOT_DEBUG_MODE=1</c>. Nothing it
/// sends reaches a server, the queues, the journal or settings.json.</para>
/// <para>Built once, like the Listening card: the Debug page is built again whenever the overlay's
/// status changes, and a name being typed or a list being open would die under a rebuild.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private static readonly (TestEventKind Kind, string Name)[] TestKinds =
    [
        (TestEventKind.Joined, "Joined"),
        (TestEventKind.Left, "Left"),
        (TestEventKind.AlreadyThere, "Already there"),
        (TestEventKind.ChangedAvatar, "Changed avatar"),
        (TestEventKind.FlaggedJoin, "Flagged join"),
        (TestEventKind.Problem, "Problem"),
        (TestEventKind.Pin, "Pin"),
        (TestEventKind.KeepAnEye, "Keep an eye"),
        (TestEventKind.Message, "Message"),
        (TestEventKind.AskForHelp, "Ask for help"),
    ];

    private readonly ComboBox _testKind = new();
    private readonly TextBox _testName = Ui.Input(TestEvents.DefaultName);
    private readonly ComboBox _testRank = new();
    private readonly CheckBox _testEighteenPlus = new() { Content = Ui.Text("18+", Ui.T.Density.TextSmall, Ui.T.TextBrush) };
    private readonly TextBox _testReason = Ui.Input();
    private readonly Button _testSend = Ui.Button("Send", primary: true);
    private readonly Button _testRun = Ui.Button("Send a run");

    /// <summary>Wires the Test events card. Called once, from the constructor.</summary>
    private void SetUpTestEvents()
    {
        foreach (var list in new[] { _testKind, _testRank })
        {
            list.Height = Ui.T.Density.ControlHeight;
            list.MinWidth = 180;
            list.FontSize = Ui.T.Density.TextSmall;
            list.FontFamily = Ui.Sans;
            list.Background = Ui.T.BackgroundBrush;
            list.Foreground = Ui.T.TextBrush;
            list.BorderBrush = Ui.T.Border2Brush;
            list.BorderThickness = new Thickness(Ui.T.Density.Hairline);
            list.CornerRadius = new CornerRadius(Ui.T.Density.Radius);
        }

        _testKind.ItemsSource = TestKinds.Select(k => k.Name).ToList();
        _testKind.SelectedIndex = 0;

        _testRank.ItemsSource = new[] { "None" }.Concat(Enum.GetValues<TrustRank>().Select(TrustRanks.Name)).ToList();
        _testRank.SelectedIndex = 0;

        _testName.Width = 220;
        _testReason.Width = 320;

        _testSend.Click += (_, _) => _actions.SendTestEvent(ReadTestEvent());
        _testRun.Click += (_, _) => _actions.SendTestRun(ReadTestEvent());
    }

    /// <summary>The test event the card's controls say right now.</summary>
    private TestEvent ReadTestEvent()
    {
        var kind = _testKind.SelectedIndex is var k and >= 0 && k < TestKinds.Length
            ? TestKinds[k].Kind
            : TestEventKind.Joined;

        var ranks = Enum.GetValues<TrustRank>();
        TrustRank? rank = _testRank.SelectedIndex is var r and >= 1 && r <= ranks.Length ? ranks[r - 1] : null;

        return new TestEvent(kind, _testName.Text, rank, _testEighteenPlus.IsChecked == true, _testReason.Text);
    }

    /// <summary>The Test events card's body: the controls, then the two buttons.</summary>
    private Control TestEventsCard()
    {
        foreach (var control in new Control[] { _testKind, _testName, _testRank, _testEighteenPlus, _testReason, _testSend, _testRun })
            DetachFromParent(control);

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 12,
                    LineSpacing = 12,
                    Children =
                    {
                        Ui.Field("Kind", _testKind),
                        Ui.Field("Name", _testName),
                        Ui.Field("Rank", _testRank),
                        Ui.Field("Reason", _testReason),
                    },
                },
                _testEighteenPlus,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _testSend, _testRun },
                },
            },
        };
    }
}
