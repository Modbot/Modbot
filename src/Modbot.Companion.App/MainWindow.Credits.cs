using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Credits;

namespace Modbot.Companion.App;

/// <summary>
/// The Credits page: the sponsors, the groups that used Modbot early, and the people who have
/// written some of it.
/// </summary>
/// <remarks>
/// <para>The same three lists the web app shows, read by the client itself from Modbot Cloud
/// rather than from a paired server — a client with no server paired still has a Credits page, and
/// which Cloud it asks is decided on this PC and nowhere else. The reading and the disclosure of
/// what it sends are in <see cref="CloudCredits"/>; this file only draws what came back.</para>
/// <para>Nothing to show draws nothing. A Cloud that is off, or unreachable and never yet read, is
/// not a fault a moderator can act on and not worth a box of red text on a page of thank-yous.</para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>How wide one person's tile is before the row wraps.</summary>
    private const double CreditTileWidth = 250;

    private void RenderCredits()
    {
        var credits = _snapshot.CreditsOrNone;

        if (credits.Sponsors.Count > 0)
            _body.Children.Add(Ui.Card(People(credits.Sponsors), "Sponsors"));

        if (credits.EarlyAdopters.Count > 0)
            _body.Children.Add(Ui.Card(People(credits.EarlyAdopters), "Early adopters"));

        if (credits.Contributors.Count > 0)
            _body.Children.Add(Ui.Card(Contributors(credits.Contributors), "Contributors"));
    }

    private Control People(IReadOnlyList<CreditPerson> people)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var person in people)
            wrap.Children.Add(PersonTile(person));

        return wrap;
    }

    /// <summary>
    /// One sponsor or early adopter: their group's banner when they have one, then their picture
    /// and their name.
    /// </summary>
    /// <remarks>
    /// A group's own page wins over whatever other link the row carries: for a VRChat group, "open
    /// the group" is what somebody reading this page is after.
    /// </remarks>
    private Control PersonTile(CreditPerson person)
    {
        var rows = new StackPanel { Spacing = 8 };

        if (Pictures?.For(person.GroupBannerUrl) is { } banner)
        {
            rows.Children.Add(new Border
            {
                Height = 64,
                CornerRadius = new CornerRadius(Ui.T.Density.Radius),
                ClipToBounds = true,
                Child = new Image { Source = banner, Stretch = Stretch.UniformToFill },
            });
        }

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };

        if (Pictures?.For(person.GroupIconUrl ?? person.PictureUrl) is { } picture)
            line.Children.Add(Ui.Picture(picture, 26));

        var name = Ui.Text(person.Name, Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.MaxWidth = CreditTileWidth - 60;
        line.Children.Add(name);

        rows.Children.Add(line);

        return Tile(rows, person.GroupPage ?? (person.Link.Length > 0 ? person.Link : null), CreditTileWidth);
    }

    private Control Contributors(IReadOnlyList<CreditContributor> contributors)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var person in contributors)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            if (Pictures?.For(person.PictureUrl) is { } picture)
                line.Children.Add(Ui.Picture(picture, 22));

            var name = Ui.Text(person.Name, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
            name.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(name);

            wrap.Children.Add(Tile(line, person.ProfileUrl.Length > 0 ? person.ProfileUrl : null, double.NaN));
        }

        return wrap;
    }

    /// <summary>
    /// One tile, a button when there is somewhere for it to go. The link opens in the moderator's
    /// browser; nothing about them goes with it.
    /// </summary>
    /// <param name="width">A person's tile is a fixed width so the rows line up; a contributor's fits its name.</param>
    private Control Tile(Control content, string? link, double width)
    {
        var padded = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(Ui.T.Density.Radius),
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            Child = content,
        };

        // Only an https link is opened, and only in the moderator's own browser. A row that carries
        // anything else is drawn and does nothing when it is pressed.
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps)
        {
            padded.Width = width;
            padded.Margin = new Thickness(0, 0, 8, 8);
            return padded;
        }

        var button = new Button
        {
            Content = padded,
            Width = width,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        button.Click += (_, _) => _ = CrashGuard.RunAsync(
            "opening a credits link", async () => await Launcher.LaunchUriAsync(address));

        return button;
    }
}
