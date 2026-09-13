using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Client.Ingest;
using Modbot.Client.Journal;
using Modbot.Client.Pipeline;
using Modbot.Client.Presentation;
using Modbot.Overlay;

namespace Modbot.Client.App;

/// <summary>
/// The tray window: what Modbot is reading, what it has sent, and how to stop it.
/// </summary>
/// <remarks>
/// <para><strong>This window is the trust argument made visible.</strong> A volunteer moderator is
/// being asked to run background software on a personal machine that watches what they do in
/// VRChat. Suspicion is the correct response, and the answer cannot be "read the source" for
/// everybody. So: a list of exactly what was disclosed, in plain English, on demand; a pause
/// button that stops transmission immediately and shows that it has; and a tray icon that is
/// always there while the program runs.</para>
/// <para><strong>The client never runs invisibly.</strong> That is a rule rather than a default,
/// and it is also — not coincidentally — one of the things that keeps this program from being
/// scored as hostile by the heuristics it otherwise resembles.</para>
/// <para>It is built in code rather than markup because it is one window of five panels, and a
/// reader auditing this program should be able to see what it displays without also learning a
/// XAML dialect.</para>
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly StackPanel _servers = new() { Spacing = 10 };
    private readonly StackPanel _warnings = new() { Spacing = 8 };
    private readonly StackPanel _journal = new() { Spacing = 2 };
    private readonly TextBlock _logStatus = Body("");

    public MainWindow()
    {
        Title = "Modbot";
        Width = 720;
        Height = 760;
        Background = DesignTokens.BackgroundBrush;

        Content = new ScrollViewer
        {
            Padding = new Thickness(20),
            Content = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    Heading("What Modbot is reading"),
                    Card(_logStatus),
                    _warnings,
                    Heading("Where it reports"),
                    _servers,
                    Heading("What it has sent"),
                    Note(
                        "Every line below is something this program disclosed about you, in the "
                        + "order it happened. Nothing is sent that does not appear here."),
                    Card(_journal),
                },
            },
        };
    }

    /// <summary>Called whenever the state changes. Rebuilds only what the snapshot describes.</summary>
    public void Render(ClientAppSnapshot snapshot, Action<string> onTogglePause)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(onTogglePause);

        _logStatus.Text = snapshot.LogDetail;
        _logStatus.Foreground = snapshot.LogStatus switch
        {
            LogHealthStatus.NotUnderstood => DesignTokens.WarnBrush,
            LogHealthStatus.Healthy => DesignTokens.ForegroundBrush,
            _ => DesignTokens.MutedForegroundBrush,
        };

        _warnings.Children.Clear();
        foreach (var warning in snapshot.Warnings)
            _warnings.Children.Add(Warning(warning));

        _servers.Children.Clear();
        if (snapshot.Servers.Count == 0)
        {
            _servers.Children.Add(Card(Body(
                "No servers paired. Modbot is reading nothing and sending nothing. Ask the group "
                + "you moderate for a pairing code.")));
        }
        else
        {
            foreach (var server in snapshot.Servers)
                _servers.Children.Add(ServerCard(server, onTogglePause));
        }

        _journal.Children.Clear();
        if (snapshot.Journal.Count == 0)
        {
            _journal.Children.Add(Body("Nothing has been sent yet."));
        }
        else
        {
            foreach (var entry in snapshot.Journal)
                _journal.Children.Add(JournalLine(entry));
        }
    }

    private static Control ServerCard(ServerRow server, Action<string> onTogglePause)
    {
        var pause = new Button
        {
            Content = server.IsPaused ? "Resume reporting" : "Pause reporting",
            MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        pause.Click += (_, _) => onTogglePause(server.ServerId);

        var lines = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = server.ServerId,
                    FontSize = DesignTokens.TextBase,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = DesignTokens.ForegroundBrush,
                },
                Muted($"{server.Address} — group {server.ManagedGroupId}"),
                new TextBlock
                {
                    Text = server.Detail,
                    FontSize = DesignTokens.TextSmall,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = server.State switch
                    {
                        ConnectionState.Stopped => DesignTokens.DestructiveBrush,
                        ConnectionState.NeedsRenegotiation => DesignTokens.WarnBrush,
                        ConnectionState.Paused => DesignTokens.WarnBrush,
                        _ => DesignTokens.MutedForegroundBrush,
                    },
                },

                // Stated plainly, because "deduplicated" sounds like a loss and is not: several
                // moderators in one instance all report the same join, and the server keeping one
                // of them is the system working.
                Muted(
                    $"{server.AcceptedTotal:N0} observations recorded · "
                    + $"{server.DeduplicatedTotal:N0} already reported by another moderator · "
                    + $"{server.Pending:N0} queued"),
                pause,
            },
        };

        return Card(lines);
    }

    private static Control JournalLine(JournalEntry entry) => new TextBlock
    {
        Text = entry.Summary,
        FontSize = DesignTokens.TextSmall,
        TextWrapping = TextWrapping.Wrap,
        Foreground = entry.Kind switch
        {
            JournalEntryKind.Withheld => DesignTokens.MutedForegroundBrush,
            JournalEntryKind.Note => DesignTokens.WarnBrush,
            _ => DesignTokens.ForegroundBrush,
        },
    };

    private static Control Warning(string message) => new Border
    {
        Background = new SolidColorBrush(DesignTokens.Warn, 0.14),
        BorderBrush = DesignTokens.WarnBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = DesignTokens.CornerRadius,
        Padding = new Thickness(14, 12),
        Child = new TextBlock
        {
            Text = message,
            FontSize = DesignTokens.TextSmall,
            TextWrapping = TextWrapping.Wrap,
            Foreground = DesignTokens.WarnBrush,
        },
    };

    private static Border Card(Control child) => new()
    {
        Background = DesignTokens.CardBrush,
        BorderBrush = DesignTokens.BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = DesignTokens.CornerRadius,
        Padding = new Thickness(16, 14),
        Child = child,
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = DesignTokens.TextBase * 1.15,
        FontWeight = FontWeight.SemiBold,
        Foreground = DesignTokens.ForegroundBrush,
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        FontSize = DesignTokens.TextSmall,
        TextWrapping = TextWrapping.Wrap,
        Foreground = DesignTokens.ForegroundBrush,
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = DesignTokens.TextSmall,
        TextWrapping = TextWrapping.Wrap,
        Foreground = DesignTokens.MutedForegroundBrush,
    };

    private static TextBlock Note(string text) => Muted(text);
}
