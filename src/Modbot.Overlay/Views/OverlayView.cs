using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Clips;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Core.Users;
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

    /// <summary>
    /// A trust rank as a small mark in VRChat's colour for it, with the rank's name in dim text
    /// beside it. The colour is the second channel and the word carries the meaning, so a rank
    /// whose VRChat colour is dark on a dark panel still reads.
    /// </summary>
    private static StackPanel RankLine(TrustRank rank, double size)
    {
        var mark = new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = DesignTokens.Brush(Color.Parse(TrustRanks.Colour(rank))),
        };

        var name = Text(TrustRanks.Name(rank), size, T.TextDimBrush);
        name.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { mark, name },
        };
    }

    /// <summary>How big the group's icon is drawn, in panel pixels.</summary>
    private const double IconSize = 28;

    /// <param name="icon">
    /// The group's picture for an address, or null when there is none yet. The companion's own
    /// cache — the same one the window's server cards draw from — so nothing here fetches anything
    /// and a picture that has not arrived leaves the name standing on its own.
    /// </param>
    public static Control Build(OverlayScreen screen, Func<string?, IImage?>? icon = null)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // Outside a group instance the panel says nothing: a card reading "not in a group
        // instance" is a card in the moderator's face for most of their VRChat time. The debug
        // page can still ask for it, to see where the panel sits.
        //
        // Save a clip is the one thing that still shows there. The recorder runs wherever VRChat
        // does, so a moment worth keeping can happen in a public instance as easily as a group
        // one, and a control the moderator cannot reach there is a control they do not have. It is
        // only ever drawn because they switched Clips on themselves.
        if (screen.IsIdle && !screen.ShowIdleCard)
        {
            return screen.Clips.IsVisible
                ? new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(20),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = SaveClipBar(screen.Clips),
                }
                : new Border { Background = Brushes.Transparent };
        }

        // Worn on a wrist the panel is a sixth of the width it is in front of the head, so it gets
        // its own screen rather than a shrunken copy of the roster: four lines, drawn large.
        if (screen.Page is OverlayPage.Wrist)
            return Framed(WristCard(screen, icon), screen.Cursor);

        var stack = new StackPanel { Spacing = 12 };

        // Whose community this is, said once at the top rather than repeated on every card below.
        // The name and the icon, because that is what a moderator knows their group by; the
        // server's address is a fallback and never the first thing said.
        stack.Children.Add(GroupLine(screen, icon));

        if (screen.Health is { Length: > 0 } health)
            stack.Children.Add(HealthBanner(health));

        if (screen.Alert is { } alert)
            stack.Children.Add(AlertCard(alert, screen.GroupLabel));

        stack.Children.Add(Tabs(screen));

        // Under the tabs, above whichever screen is showing, so it is one press away from all
        // three rather than behind a page (clips design spec §11).
        if (screen.Clips.IsVisible)
            stack.Children.Add(SaveClipBar(screen.Clips));

        // One screen at a time. A panel that stacked all three would need scrolling to reach the
        // bottom of, and scrolling in a headset is the thing to design out.
        stack.Children.Add(screen.Page switch
        {
            OverlayPage.Events => EventsPanel(screen),
            OverlayPage.Person when screen.Person is { } person => PersonCard(person),
            _ => RosterPanel(screen),
        });

        return Framed(stack, screen.Cursor);
    }

    /// <summary>
    /// The panel's own frame: the cards on nothing, with the cursor over them.
    /// </summary>
    /// <remarks>
    /// Nothing behind the cards. The texture is square and the cards fill its top, so an opaque
    /// ground would hang a dark slab over half the moderator's view; each card paints its own
    /// surface, and the rest of the panel lets the world through. The cursor sits over everything,
    /// drawn into the same frame: SteamVR draws lasers only for dashboard overlays and OpenXR
    /// draws none, so the panel shows its own.
    /// </remarks>
    private static Control Framed(Control content, PanelCursor? cursor)
    {
        var panel = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(20),
            Child = content,
        };

        return cursor is { } where
            ? new Panel { Children = { panel, new CursorLayer(where) } }
            : panel;
    }

    /// <summary>
    /// What a panel worn on the wrist says: whose community, how many are here, and the one thing
    /// worth looking down for.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why not the roster.</strong> The wrist panel is 0.16 m across where the head
    /// panel is 0.45 m, on the same texture — everything on it is a third the size it was. A list
    /// of twenty names at that size is a grey smear, and a moderator is not going to read a list
    /// off their arm anyway. What a watch is for is the glance: is anything wrong, and how busy is
    /// it. So this is the pop-up's shape rather than the roster's — one thing said large — with
    /// the group and the head count above it so the glance answers both questions at once.</para>
    /// <para>The alert is tappable and clears, exactly as it does on the big panel; there is
    /// nothing else to press, because there is nothing else a wrist is the right place to do.
    /// Pointing at it with the other hand works like pointing at any other placement.</para>
    /// </remarks>
    private static Control WristCard(OverlayScreen screen, Func<string?, IImage?>? icon)
    {
        var lines = new StackPanel { Spacing = 10 };
        lines.Children.Add(GroupLine(screen, icon, T.Density.TextBase * 1.6, IconSize * 1.6));

        var here = screen.Roster.Value?.Members.Count ?? 0;
        lines.Children.Add(Text(
            here == 1 ? "1 here" : here + " here",
            T.Density.TextBase * 1.4,
            screen.Freshness == Freshness.Fresh ? T.TextDimBrush : T.WarnBrush,
            FontWeight.SemiBold));

        if (screen.Health is { Length: > 0 } health)
        {
            lines.Children.Add(Text(health, T.Density.TextBase * 1.3, T.WarnBrush, FontWeight.SemiBold));
        }
        else if (screen.Alert is { } alert)
        {
            lines.Children.Add(Text(alert.DisplayName ?? alert.SubjectId, T.Density.TextBase * 1.7, T.TextBrush, FontWeight.SemiBold));
            lines.Children.Add(Text(alert.Reason, T.Density.TextBase * 1.3, T.DangerBrush));
        }
        else if (screen.EventsOrNone.Count > 0)
        {
            var newest = screen.EventsOrNone[0];
            lines.Children.Add(Text(
                newest.Person?.DisplayName ?? newest.Person?.SubjectId ?? Words(newest.Kind),
                T.Density.TextBase * 1.5,
                T.TextDimBrush));
            lines.Children.Add(Text(Words(newest.Kind), T.Density.TextBase * 1.3, T.TextDimBrush));
        }

        return new Border
        {
            // The alert is the one thing on here worth being able to clear, and a tap anywhere on
            // the card clears it, because a wrist is a poor place to land on a small target.
            Tag = screen.Alert is null ? null : new OverlayTarget.DismissAlert(),
            Background = T.SurfaceBrush,
            BorderBrush = screen.Alert is null ? T.BorderBrush : T.DangerBrush,
            BorderThickness = new Thickness(screen.Alert is null ? T.Density.Hairline : 10, T.Density.Hairline, T.Density.Hairline, T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(24, 20),
            Child = lines,
        };
    }

    /// <summary>A ring where a controller points, placed by fractions of the panel.</summary>
    private sealed class CursorLayer : Panel
    {
        private const double Radius = 14;
        private readonly PanelCursor _cursor;

        public CursorLayer(PanelCursor cursor)
        {
            _cursor = cursor;
            IsHitTestVisible = false;
            Children.Add(new Ellipse
            {
                Width = Radius * 2,
                Height = Radius * 2,
                Stroke = T.TextBrush,
                StrokeThickness = 3,
                Fill = new SolidColorBrush(T.Palette.Text, 0.25),
            });
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var x = (_cursor.Across * finalSize.Width) - Radius;
            var y = (_cursor.Down * finalSize.Height) - Radius;
            foreach (var child in Children)
                child.Arrange(new Rect(x, y, Radius * 2, Radius * 2));

            return finalSize;
        }
    }

    /// <summary>
    /// Whose community this panel is speaking for: the group's icon and the group's name.
    /// </summary>
    /// <remarks>
    /// A moderator knows their community by its name and its picture, not by the address of the
    /// machine their server happens to run on. The address is what this falls back to when a
    /// pairing was made before servers gave their group's name, and it is never the first choice.
    /// </remarks>
    /// <param name="size">How big the name is drawn; the wrist panel asks for more.</param>
    /// <param name="iconSize">How big the picture is drawn, in panel pixels.</param>
    private static Control GroupLine(OverlayScreen screen, Func<string?, IImage?>? icon, double? size = null, double? iconSize = null)
    {
        var picture = iconSize ?? IconSize;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Height = picture,
        };

        if (icon?.Invoke(screen.GroupIconUrl) is { } image)
        {
            row.Children.Add(new Border
            {
                Width = picture,
                Height = picture,
                CornerRadius = new CornerRadius(picture / 2),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = image, Stretch = Stretch.UniformToFill },
            });
        }

        var name = Text(
            screen.GroupLabel ?? "Not in a group instance",
            size ?? T.Density.TextBase,
            screen.GroupLabel is null ? T.TextDimBrush : T.TextBrush,
            FontWeight.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);

        return row;
    }

    /// <summary>
    /// The tabs across the top: the three screens, with the one showing marked. The Person tab is
    /// there only while a person is open, because a tab that opens nothing is a dead control.
    /// </summary>
    private static Control Tabs(OverlayScreen screen)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        row.Children.Add(Tab("Instance", OverlayPage.Instance, screen.Page));
        row.Children.Add(Tab("Events", OverlayPage.Events, screen.Page));

        if (screen.Person is { } person)
            row.Children.Add(Tab(person.DisplayName ?? person.SubjectId, OverlayPage.Person, screen.Page));

        return row;
    }

    private static Control Tab(string caption, OverlayPage page, OverlayPage showing)
    {
        var chosen = page == showing;
        var label = Text(caption, T.Density.TextBase, chosen ? T.TextBrush : T.TextDimBrush, chosen ? FontWeight.SemiBold : FontWeight.Normal);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.MaxWidth = 220;

        return new Border
        {
            Tag = new OverlayTarget.GoTo(page),
            Background = chosen ? T.Surface2Brush : T.SurfaceBrush,
            BorderBrush = chosen ? T.AccentForegroundBrush : T.BorderBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,

            // A target a hand in a headset can actually land on.
            MinWidth = 140,
            MinHeight = T.Density.RowHeight,
            Padding = new Thickness(16, 8),
            Child = label,
        };
    }

    /// <summary>
    /// What the live link has heard for this instance, newest first: who joined, who left, who
    /// was already here, and a watch ending.
    /// </summary>
    /// <remarks>
    /// Drawn from what the drive loop already drained. Nothing is asked of a server to fill this
    /// screen — opening it makes no request at all.
    /// </remarks>
    private static Control EventsPanel(OverlayScreen screen)
    {
        var rows = new StackPanel { Spacing = 2 };

        var events = screen.EventsOrNone;
        if (events.Count == 0)
        {
            rows.Children.Add(Text("Nothing yet.", T.Density.TextBase, T.TextDimBrush));
        }
        else
        {
            foreach (var @event in events.Take(MostEventRows))
                rows.Children.Add(EventRow(@event));
        }

        return new Border
        {
            Tag = new OverlayTarget.Events(),
            Background = T.SurfaceBrush,
            BorderBrush = T.BorderBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(18, 14),
            Child = rows,
        };
    }

    /// <summary>How many event rows fit the panel. More than this and the oldest simply are not drawn.</summary>
    private const int MostEventRows = 10;

    private static Control EventRow(LiveEvent @event)
    {
        var flagged = @event.Flagged || @event.Kind == LiveEventKinds.FlaggedJoin;

        var what = Text(
            Words(@event.Kind),
            T.Density.TextSmall,
            flagged ? T.DangerBrush : T.TextDimBrush,
            FontWeight.SemiBold);
        what.VerticalAlignment = VerticalAlignment.Center;
        what.Width = 110;

        var who = Text(
            @event.Person?.DisplayName ?? @event.Person?.SubjectId ?? "—",
            T.Density.TextBase,
            flagged ? T.TextBrush : T.TextDimBrush,
            flagged ? FontWeight.SemiBold : FontWeight.Normal);
        who.VerticalAlignment = VerticalAlignment.Center;

        // Trimmed rather than allowed to push the clock off the end of the row.
        who.MaxWidth = 200;

        var line = new StackPanel
        {
            Tag = @event.Person is { } person ? new OverlayTarget.Person(person.SubjectId) : null,
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Height = T.Density.RowHeight,
            Children = { what, who },
        };

        // The clock the event arrived with, in the moderator's own time. It is the server's
        // stamp, not this machine's.
        var when = Text(@event.At.ToLocalTime().ToString("HH:mm"), T.Density.TextSmall, T.TextDimBrush);
        when.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(when);

        return line;
    }

    /// <summary>An event kind in plain words. An unknown kind is shown as it came, never guessed at.</summary>
    private static string Words(string kind) => kind switch
    {
        LiveEventKinds.PersonJoined => "Joined",
        LiveEventKinds.FlaggedJoin => "Flagged join",
        LiveEventKinds.PersonLeft => "Left",
        LiveEventKinds.PersonHere => "Already here",
        LiveEventKinds.WatchStopped => "Watch ended",
        _ => kind,
    };

    /// <summary>
    /// One person, opened from their roster row: what the roster already knew and what the
    /// server's profile read added, with Back and Refresh under it.
    /// </summary>
    private static Control PersonCard(UserSummary person)
    {
        var lines = new StackPanel { Spacing = 6 };

        lines.Children.Add(Text(person.DisplayName ?? person.SubjectId, T.Density.TextBase * 1.4, T.TextBrush, FontWeight.SemiBold));

        var standing = person.Standing switch
        {
            RosterStanding.Flagged => "Flagged",
            RosterStanding.Staff => "Staff",
            RosterStanding.Member => "Member",
            _ => "Not a member",
        };
        lines.Children.Add(Text(
            person.PriorActions switch
            {
                0 => standing,
                1 => standing + " · 1 prior action",
                var n => standing + " · " + n + " prior actions",
            },
            T.Density.TextBase,
            person.Standing == RosterStanding.Flagged ? T.DangerBrush : T.TextDimBrush));

        if (person.Roles.Count > 0)
            lines.Children.Add(Text(string.Join(" · ", person.Roles), T.Density.TextSmall, T.TextDimBrush));

        if (person.Flags.Count > 0)
            lines.Children.Add(Text(string.Join(" · ", person.Flags), T.Density.TextSmall, T.DangerBrush));

        if (person.JoinedAt is { } joined)
            lines.Children.Add(Text("Joined " + joined.ToString("yyyy-MM-dd"), T.Density.TextSmall, T.TextDimBrush));

        // The only two things this panel can honestly do about a person: go back, and read their
        // summary again. There is no ban, kick or warn here and there will not be one — the
        // client's device token is ingest-scoped and could not carry one.
        lines.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                Press("Back", new OverlayTarget.ClosePerson()),
                Press("Refresh", new OverlayTarget.RefreshPerson()),
            },
        });

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.AccentForegroundBrush,
            BorderThickness = new Thickness(6, T.Density.Hairline, T.Density.Hairline, T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(18, 14),
            Child = lines,
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

        if (alert.TrustRank is { } rank)
            lines.Children.Add(RankLine(rank, T.Density.TextBase));

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
            Tag = new OverlayTarget.DismissAlert(),
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

        var here = screen.Roster.Value?.Members.Count ?? 0;

        rows.Children.Add(new DockPanel
        {
            LastChildFill = false,
            Children =
            {
                Dock(Text(
                    here == 1 ? "1 here" : here + " here",
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
            var ordered = context.Members
                .OrderBy(Priority)
                .ThenBy(SortName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Scrolled-past rows are counted, not hidden without a word.
            var skip = Math.Clamp(screen.RosterSkip, 0, Math.Max(0, ordered.Count - 1));
            if (skip > 0)
                rows.Children.Add(Text(skip == 1 ? "1 more above" : skip + " more above", T.Density.TextSmall, T.TextDimBrush));

            foreach (var member in ordered.Skip(skip))
                rows.Children.Add(RosterRow(member));
        }

        return new Border
        {
            Tag = new OverlayTarget.Roster(),
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

        // A long name trims rather than shoving what follows it off the end of the row. That is
        // what used to send a person's flags drifting away from the person they belong to.
        name.MaxWidth = 220;

        var line = new StackPanel
        {
            Tag = new OverlayTarget.Person(member.SubjectId),
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Height = T.Density.RowHeight,
            Children = { badge, name },
        };

        // The rank in its VRChat colour, after the name and before the flags, on the same line.
        if (member.TrustRank is { } rank)
        {
            var mark = RankLine(rank, T.Density.TextSmall);
            mark.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(mark);
        }

        if (member.Flags.Count > 0)
            line.Children.Add(FlagChip(string.Join(" · ", member.Flags)));

        return line;
    }

    /// <summary>
    /// What is known against a person — "1 prior action", a flag's name — as a mark on their own
    /// row.
    /// </summary>
    /// <remarks>
    /// A tinted chip rather than loose red words, so it reads as belonging to the row it sits on.
    /// It used to be plain text at the end of a horizontal row, which put it wherever the name
    /// happened to leave it — usually far to the right, next to nobody.
    /// </remarks>
    private static Control FlagChip(string caption)
    {
        var label = Text(caption, T.Density.TextSmall, T.DangerBrush, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.MaxWidth = 200;

        return new Border
        {
            Background = new SolidColorBrush(T.Palette.Danger, 0.16),
            BorderBrush = T.DangerBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>
    /// Save a clip: keep the last few minutes of VRChat as a file on this PC.
    /// </summary>
    /// <remarks>
    /// <para><strong>It never looks pressable while nothing is being kept.</strong> Inside a
    /// headset there is no settings screen, no file explorer and no notification, so a control that
    /// appeared to work and quietly did nothing would leave a moderator believing they had kept a
    /// moment they had not. The switch off, VRChat not running, a folder that cannot be written to,
    /// a machine that cannot record — each gives the caption for that and a control the tap path
    /// refuses (<see cref="ClipButtonRule"/>).</para>
    /// <para><strong>And it says when a clip landed</strong>, for a few seconds, because that is
    /// the only way to find out from inside VR.</para>
    /// </remarks>
    private static Control SaveClipBar(ClipButton clip)
    {
        var label = Text(
            clip.Caption ?? string.Empty,
            T.Density.TextBase,
            clip.State switch
            {
                ClipButtonState.Saved => T.OkBrush,
                ClipButtonState.NotSaved => T.DangerBrush,
                ClipButtonState.Stopped => T.TextDimBrush,
                _ => T.TextBrush,
            },
            FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;

        return new Border
        {
            // No target at all while nothing is being kept, so a tap lands on nothing rather than
            // on a control that would have to decide to ignore it.
            Tag = clip.CanPress ? new OverlayTarget.SaveClip() : null,
            Background = clip.CanPress ? T.Surface2Brush : T.SurfaceBrush,
            BorderBrush = clip.State switch
            {
                ClipButtonState.Saved => T.OkBrush,
                ClipButtonState.NotSaved => T.DangerBrush,
                ClipButtonState.Stopped => T.BorderBrush,
                _ => T.AccentForegroundBrush,
            },
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,

            // A target a hand in a headset can land on without aiming.
            MinWidth = 240,
            MinHeight = T.Density.RowHeight,
            Padding = new Thickness(16, 8),
            Child = label,
        };
    }

    /// <summary>A control a controller can press, sized for a hand in a headset.</summary>
    private static Control Press(string caption, OverlayTarget target)
    {
        var label = Text(caption, T.Density.TextBase, T.TextBrush, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;

        return new Border
        {
            Tag = target,
            Background = T.Surface2Brush,
            BorderBrush = T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            MinWidth = 140,
            MinHeight = T.Density.RowHeight,
            Padding = new Thickness(16, 8),
            Child = label,
        };
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
