using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Shared.Names;

namespace Modbot.Overlay.Views;

/// <summary>
/// Builds the Avalonia visual tree for one <see cref="OverlayScreen"/>.
/// </summary>
/// <remarks>
/// <para><strong>Glanceable, and readable from inside a headset.</strong> Reading is fine in VR;
/// scrolling and typing are hostile. So this is a short card and a short list, at the VR density
/// values — large targets, body text above the mush floor, two-pixel borders because one-pixel
/// ones vanish, and colours pulled back from pure black and white because panels bloom at the
/// extremes.</para>
/// <para><strong>Nothing here can act.</strong> There are no buttons that ban, kick or warn: a
/// moderator acting on what they see goes through the normal authenticated API as themselves,
/// because the client's device token is ingest-scoped and could not carry a moderation action
/// even if something here tried.</para>
/// <para><strong>Freshness is shown, never implied.</strong> Every panel that came from a server
/// carries the age of what it is showing. "Flagged — as of 20 minutes ago" is something a
/// moderator can act on; a stale panel pretending to be current is not.</para>
/// <para><strong>Display names are hostile input.</strong> They are arbitrary user-controlled
/// text, so they are placed as text — never parsed, never interpreted as markup — and given a
/// fixed line count so a name built out of newlines cannot push the rest of the card off the
/// panel.</para>
/// </remarks>
public static class OverlayView
{
    /// <summary>The headset's tokens: the VR palette at the VR density.</summary>
    private static DesignTokens T => DesignTokens.Vr;

    public static Control Build(OverlayScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // Outside a group instance the panel says nothing: a card reading "not in a group
        // instance" is a card in the moderator's face for most of their VRChat time. The debug
        // page can still ask for it, to see where the panel sits.
        if (screen.IsIdle && !screen.ShowIdleCard)
            return new Border { Background = Brushes.Transparent };

        var stack = new StackPanel { Spacing = 12 };

        if (screen.Health is { Length: > 0 } health)
            stack.Children.Add(HealthBanner(health));

        if (screen.Alert is { } alert)
            stack.Children.Add(AlertCard(alert, screen.GroupLabel));

        stack.Children.Add(RosterPanel(screen));

        // Nothing behind the cards. The texture is square and the cards fill its top, so an
        // opaque ground would hang a dark slab over half the moderator's view; each card paints
        // its own surface, and the rest of the panel lets the world through.
        return new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(20),
            Child = stack,
        };
    }

    /// <summary>
    /// Credentials rejected, ingest stopped, a WAF block. Inside VRChat there is no email, no
    /// Discord and no browser, so for the person doing moderation at the moment it matters, this
    /// is the entire notification surface.
    /// </summary>
    private static Control HealthBanner(string message) => new Border
    {
        // The tint sits on the surface colour rather than on the world behind the panel.
        Background = T.SurfaceBrush,
        CornerRadius = T.CornerRadius,
        Child = new Border
        {
            Background = new SolidColorBrush(T.Palette.Warn, 0.16),
            BorderBrush = T.WarnBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(16, 12),
            Child = Text(message, T.Density.TextBase, T.WarnBrush, FontWeight.SemiBold),
        },
    };

    /// <summary>
    /// The highest-value thing the overlay does: somebody with prior kicks, an active warning or a
    /// flag has just walked into this instance.
    /// </summary>
    private static Control AlertCard(FlaggedJoinAlert alert, string? groupLabel)
    {
        var lines = new StackPanel { Spacing = 6 };

        lines.Children.Add(Text(
            groupLabel is null ? "Flagged user joined" : $"Flagged user joined · {groupLabel}",
            T.Density.TextSmall,
            T.TextDimBrush,
            FontWeight.SemiBold));

        lines.Children.Add(Text(
            alert.DisplayName ?? alert.SubjectId,
            T.Density.TextBase * 1.4,
            T.TextBrush,
            FontWeight.SemiBold));

        lines.Children.Add(Text(alert.Reason, T.Density.TextBase, T.TextBrush));

        if (alert.PriorActions > 0)
        {
            lines.Children.Add(Text(
                alert.PriorActions == 1 ? "1 prior action" : $"{alert.PriorActions} prior actions",
                T.Density.TextSmall,
                T.TextDimBrush));
        }

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.DangerBrush,

            // A thicker left edge rather than a full border: the eye finds it at a glance without
            // the card becoming a box inside a box.
            BorderThickness = new Thickness(6, T.Density.Hairline, T.Density.Hairline, T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(18, 14),
            MinHeight = T.Density.RowHeight * 2,
            Child = lines,
        };
    }

    private static Control RosterPanel(OverlayScreen screen)
    {
        var rows = new StackPanel { Spacing = 2 };

        rows.Children.Add(new DockPanel
        {
            LastChildFill = false,
            Children =
            {
                Dock(Text(
                    screen.GroupLabel is null ? "Not in a group instance" : screen.GroupLabel,
                    T.Density.TextSmall,
                    T.TextDimBrush,
                    FontWeight.SemiBold), Avalonia.Controls.Dock.Left),

                // Always stated, on every panel that came from a server.
                Dock(Text(
                    screen.Roster.Describe(),
                    T.Density.TextSmall,
                    screen.Freshness == Freshness.Fresh
                        ? T.TextDimBrush
                        : T.WarnBrush), Avalonia.Controls.Dock.Right),
            },
        });

        if (screen.Roster.Value is not { } context || context.Members.Count == 0)
        {
            rows.Children.Add(Text(
                screen.Freshness == Freshness.Never
                    ? "No roster loaded for this instance."
                    : "Nobody here.",
                T.Density.TextBase,
                T.TextDimBrush));
        }
        else
        {
            // Flagged first, then staff, then everybody else: the overlay's job is to put the row
            // that matters where the eye lands, not to reproduce a sortable table. Within a band
            // the order is by the name in plain letters, so 𝕬𝖑𝖊𝖝 sits with the As.
            foreach (var member in context.Members.OrderBy(Priority).ThenBy(SortName, StringComparer.OrdinalIgnoreCase))
                rows.Children.Add(RosterRow(member));
        }

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.BorderBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(18, 14),
            Child = rows,
        };
    }

    private static int Priority(RosterMember member) => member.Standing switch
    {
        RosterStanding.Flagged => 0,
        RosterStanding.Staff => 1,
        RosterStanding.Member => 2,
        _ => 3,
    };

    /// <summary>The name in plain letters, else the name, else the id: what the row sorts by.</summary>
    private static string SortName(RosterMember member)
    {
        if (member.DisplayName is null)
            return member.SubjectId;

        var plain = NameNormalizer.Readable(member.DisplayName);
        return plain.Length == 0 ? member.DisplayName : plain;
    }

    private static Control RosterRow(RosterMember member)
    {
        var badge = new Ellipse
        {
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = member.Standing switch
            {
                RosterStanding.Flagged => T.DangerBrush,
                RosterStanding.Staff => T.AccentForegroundBrush,
                RosterStanding.Member => T.OkBrush,
                _ => T.BorderBrush,
            },
        };

        var name = Text(
            member.DisplayName ?? member.SubjectId,
            T.Density.TextBase,
            member.Standing == RosterStanding.Flagged
                ? T.TextBrush
                : T.TextDimBrush,
            member.Standing == RosterStanding.Flagged ? FontWeight.SemiBold : FontWeight.Normal);
        name.VerticalAlignment = VerticalAlignment.Center;

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Height = T.Density.RowHeight,
            Children = { badge, name },
        };

        if (member.Flags.Count > 0)
        {
            line.Children.Add(Text(
                string.Join(" · ", member.Flags),
                T.Density.TextSmall,
                T.DangerBrush));
        }

        return line;
    }

    private static TextBlock Text(
        string content,
        double size,
        IBrush brush,
        FontWeight weight = FontWeight.Normal) => new()
        {
            Text = content,
            FontSize = size,
            FontFamily = new FontFamily(DesignTokens.FontFamily),
            FontWeight = weight,
            Foreground = brush,

            // Names are arbitrary user-controlled text. Clipping to one line means a name made of
            // newlines cannot push the rest of the panel out of the headset's view.
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

    private static Control Dock(Control control, Dock side)
    {
        DockPanel.SetDock(control, side);
        return control;
    }
}
