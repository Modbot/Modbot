using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Clips;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Core.Users;
using Modbot.Shared.HeadsUps;
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
/// carries the age of what it is showing. "Flagged — as of 20m ago" is something a
/// moderator can act on; a stale panel pretending to be current is not.</para>
/// <para><strong>Display names are hostile input.</strong> They are arbitrary user-controlled
/// text, so they are placed as text — never parsed, never interpreted as markup — and given a
/// fixed line count so a name built out of newlines cannot push the rest of the card off the
/// panel.</para>
/// </remarks>
public sealed class OverlayView
{
    private readonly OverlayLook _look;

    private OverlayView(OverlayLook look) => _look = look;

    /// <summary>The tokens this screen is drawn in: the headset's, unless the desktop window asked for another look.</summary>
    private DesignTokens T => _look.Tokens;

    /// <summary>The look of VRChat's own menu, or null while the screen is drawn the headset's way.</summary>
    private VRChatLook? V => _look.VRChat;

    /// <summary>A trust rank's dot and name, drawn the way every surface draws it.</summary>
    private Control RankLine(TrustRank rank, double size)
        => V is null ? PersonMarks.RankLine(rank, size, T) : RankPill(rank, size);

    /// <summary>How big the group's icon is drawn, in panel pixels.</summary>
    private const double IconSize = 28;

    /// <param name="icon">
    /// The group's picture for an address, or null when there is none yet. The companion's own
    /// cache — the same one the window's server cards draw from — so nothing here fetches anything
    /// and a picture that has not arrived leaves the name standing on its own.
    /// </param>
    public static Control Build(OverlayScreen screen, Func<string?, IImage?>? icon = null)
        => Build(screen, icon, OverlayLook.Headset);

    /// <param name="look">
    /// How to draw it. The headset panel never passes one; the desktop window passes the look of
    /// VRChat's own menu while it has a palette to take it from.
    /// </param>
    public static Control Build(OverlayScreen screen, Func<string?, IImage?>? icon, OverlayLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        return new OverlayView(look).Draw(screen, icon);
    }

    private Control Draw(OverlayScreen screen, Func<string?, IImage?>? icon)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // With no server to speak for, and nothing of this PC's own log to list, the panel says
        // nothing: a card reading "not in a group instance" is a card in the moderator's face for
        // most of their VRChat time. The debug page can still ask for it, to see where the panel
        // sits. (With a server paired, a public or private instance is not idle: it lists the
        // people from the log and says it is not synced.)
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

        var stack = new StackPanel { Spacing = V is null ? 12 : 14 };

        // Whose community this is, said once at the top rather than repeated on every card below.
        // The name and the icon, because that is what a moderator knows their group by; the
        // server's address is a fallback and never the first thing said. In VRChat's look the
        // title strip above already carries the icon, so this is the large heading alone.
        stack.Children.Add(V is null ? GroupLine(screen, icon) : Heading(screen));

        if (screen.NotSynced)
            stack.Children.Add(NotSyncedNote(T.Density.TextSmall));

        if (screen.Health is { Length: > 0 } health)
            stack.Children.Add(HealthBanner(health));

        if (screen.Alert is { } alert)
            stack.Children.Add(AlertCard(alert, screen.GroupLabel));

        stack.Children.Add(Tabs(screen));

        // Under the tabs, above whichever screen is showing, so it is one press away from all
        // three rather than behind a page (clips design spec §11).
        if (screen.Clips.IsVisible)
            stack.Children.Add(SaveClipBar(screen.Clips));

        // The list's own filters, between the tabs and the list, and the open one's choices under
        // them. Each list keeps its own. Not over an empty list, where they would have nothing to
        // filter, unless something is picked and has to be seen to be cleared.
        if (screen.ShownFilters is { } filters && (HasRows(screen) || filters.AnyPicked || filters.Open is not null))
        {
            stack.Children.Add(FilterRow(screen, screen.Page, filters));

            if (filters.Open is { } open)
                stack.Children.Add(Choices(screen.Page, open, filters));
        }

        // A heads-up being written takes the place of a filter's choices, over the roster it was
        // opened from; the ones standing here sit above the roster, where the eye lands first.
        if (screen.Page is OverlayPage.Instance)
        {
            if (screen.Draft is { } draft)
                stack.Children.Add(DraftStrip(draft, screen.NoKeyboard));

            if (screen.HeadsUpsOrNone.Count > 0)
                stack.Children.Add(HeadsUpList(screen.HeadsUpsOrNone, screen.CanPlaceHeadsUps));
        }

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
    private Control Framed(Control content, PanelCursor? cursor)
    {
        var panel = new Border
        {
            Background = Brushes.Transparent,
            Padding = V is null ? new Thickness(20) : new Thickness(18, 16),
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
    private Control WristCard(OverlayScreen screen, Func<string?, IImage?>? icon)
    {
        var lines = new StackPanel { Spacing = 10 };
        lines.Children.Add(GroupLine(screen, icon, T.Density.TextBase * 1.6, IconSize * 1.6));

        if (screen.NotSynced)
            lines.Children.Add(NotSyncedNote(T.Density.TextBase * 1.2));

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
    internal sealed class CursorLayer : Panel
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
                Stroke = DesignTokens.Vr.TextBrush,
                StrokeThickness = 3,
                Fill = new SolidColorBrush(DesignTokens.Vr.Palette.Text, 0.25),
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
    private Control GroupLine(OverlayScreen screen, Func<string?, IImage?>? icon, double? size = null, double? iconSize = null)
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
    /// The one line a list made from this PC's own log carries, directly under the heading: the
    /// people are real, and the group is not being asked about them.
    /// </summary>
    /// <remarks>
    /// In the dim text colour of the look in use, so it is read as a note and not as a warning.
    /// </remarks>
    private Control NotSyncedNote(double size)
        => Text(NotSyncedWords, size, V is null ? T.TextDimBrush : V.Subtext);

    /// <summary>What the panel says while its lists come from this PC's log and not from the group.</summary>
    public const string NotSyncedWords = "Not synced with the group";

    /// <summary>
    /// The tabs across the top: the three screens, with the one showing marked. The Person tab is
    /// there only while a person is open, because a tab that opens nothing is a dead control.
    /// </summary>
    private Control Tabs(OverlayScreen screen)
    {
        if (V is { } vrchat)
            return VRChatTabs(screen, vrchat);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        row.Children.Add(Tab("Instance", OverlayPage.Instance, screen.Page));
        row.Children.Add(Tab("Audit Log", OverlayPage.Events, screen.Page));

        if (screen.Person is { } person)
            row.Children.Add(Tab(person.DisplayName ?? person.SubjectId, OverlayPage.Person, screen.Page));

        return row;
    }

    /// <summary>
    /// The large heading at the top of the panel in VRChat's look: the group's name, or what stands
    /// in for it. The same words the title strip says, as the mock has them.
    /// </summary>
    private Control Heading(OverlayScreen screen)
    {
        var words = Text(screen.GroupLabel ?? "Not in a group instance", HeadingSize, V!.Text, FontWeight.Bold);
        words.LetterSpacing = 0.2;
        return words;
    }

    private const double HeadingSize = 26;

    /// <summary>A person's head and shoulders, on the mock's 24-unit square.</summary>
    private const string PersonIcon = "F1 M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm0 2c-3.3 0-8 1.7-8 5v1h16v-1c0-3.3-4.7-5-8-5z";

    /// <summary>A sheet of paper with a folded corner, on the mock's 24-unit square.</summary>
    private const string PageIcon = "F1 M6 3h9l5 5v13H6zM14 4v5h5M8 12h8v2H8zm0 4h8v2H8z";

    /// <summary>
    /// The tabs as the mock has them: equal rounded buttons, each with an icon and a bold label,
    /// and the one showing in the highlights colour. The same tabs, in the same order, doing the
    /// same thing when pressed.
    /// </summary>
    private Control VRChatTabs(OverlayScreen screen, VRChatLook v)
    {
        var tabs = new List<Control>
        {
            VRChatTab(v, "Instance", PersonIcon, OverlayPage.Instance, screen.Page),
            VRChatTab(v, "Audit Log", PageIcon, OverlayPage.Events, screen.Page),
        };

        if (screen.Person is { } person)
            tabs.Add(VRChatTab(v, person.DisplayName ?? person.SubjectId, PersonIcon, OverlayPage.Person, screen.Page));

        var row = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < tabs.Count; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Grid.SetColumn(tabs[i], i);
            row.Children.Add(tabs[i]);
        }

        return row;
    }

    private Control VRChatTab(VRChatLook v, string caption, string icon, OverlayPage page, OverlayPage showing)
    {
        var chosen = page == showing;

        var glyph = new Viewbox
        {
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(icon),
                Fill = chosen ? v.SelectedText : v.Icon,
                Width = 24,
                Height = 24,
            },
        };
        glyph.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(glyph, Avalonia.Controls.Dock.Left);

        var label = Text(caption, 17, chosen ? v.SelectedText : v.Text, FontWeight.Bold);
        label.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Tag = new OverlayTarget.GoTo(page),
            Background = chosen ? v.Selected : v.Button,
            BorderBrush = chosen ? v.SelectedEdge : T.BorderBrush,
            BorderThickness = new Thickness(VRChatLook.EdgeWidth),
            CornerRadius = new CornerRadius(VRChatLook.CardRadius),
            Padding = new Thickness(12, 10),
            Child = new DockPanel { LastChildFill = true, Children = { glyph, label } },
        };
    }

    private Control Tab(string caption, OverlayPage page, OverlayPage showing)
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
    /// A list's filters in one row: each names itself until something is picked, then says what
    /// is. Clear is there only while something is picked.
    /// </summary>
    /// <remarks>
    /// <para>Taps, not typing, because typing is hostile in a headset: every filter but the name is
    /// a handful of choices under one tap (<see cref="Choices"/>). The row wraps rather than
    /// running off the panel when every filter is saying something long.</para>
    /// <para>Name is left off a headset that has no keyboard, unless a name was typed on the
    /// desktop window, because then it is hiding people and has to say so.</para>
    /// </remarks>
    private Control FilterRow(OverlayScreen screen, OverlayPage list, ListFilters filters)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };

        // Who is a member or staff, and what rank somebody holds, are things a group's server says.
        // A list made from this PC's log has neither, so a filter on them would only ever be empty.
        if (!screen.NotSynced)
        {
            row.Children.Add(FilterChip(list, FilterPart.Who, filters, filters.Who is Who.All ? null : WhoWords(filters.Who)));
            row.Children.Add(FilterChip(list, FilterPart.Rank, filters, filters.Ranks.IsEmpty ? null : RankWords(filters.Ranks)));
        }

        if (list is OverlayPage.Events)
            row.Children.Add(FilterChip(list, FilterPart.Kind, filters, filters.Kinds.IsEmpty ? null : KindWords(filters.Kinds)));

        row.Children.Add(FilterChip(list, FilterPart.Time, filters, filters.Time is TimeWindow.Any ? null : WindowWords(filters.Time)));

        if (!screen.NoKeyboard || filters.Name is not null)
            row.Children.Add(FilterChip(list, FilterPart.Name, filters, filters.Name));

        if (list is OverlayPage.Instance)
            row.Children.Add(FilterChip(list, FilterPart.Sort, filters, filters.Order is RosterOrder.Standing ? null : OrderWords(filters.Order)));

        if (filters.AnyPicked)
            row.Children.Add(Choice("Clear", false, new OverlayTarget.ClearFilters(list)));

        return row;
    }

    /// <summary>Whether the list showing has anything in it before any filter.</summary>
    private bool HasRows(OverlayScreen screen) => screen.Page switch
    {
        OverlayPage.Instance => screen.Roster.Value is { Members.Count: > 0 } || screen.LeftOrNone.Count > 0,
        OverlayPage.Events => screen.EventsOrNone.Count > 0,
        _ => false,
    };

    /// <summary>A filter's own name on its chip: "Joined" on the Instance list, "When" on the Audit Log.</summary>
    private string PartName(OverlayPage list, FilterPart part) => part switch
    {
        FilterPart.Who => "Who",
        FilterPart.Rank => "Rank",
        FilterPart.Time => list is OverlayPage.Instance ? "Joined" : "When",
        FilterPart.Name => "Name",
        FilterPart.Sort => "Sort",
        _ => "Kind",
    };

    /// <param name="picked">What is picked, in words, or null while nothing is.</param>
    private Control FilterChip(OverlayPage list, FilterPart part, ListFilters filters, string? picked)
    {
        var open = filters.Open == part;
        var set = picked is not null;

        var label = Text(
            (set ? PartName(list, part) + ": " + picked : PartName(list, part)) + (open ? "  ▴" : "  ▾"),
            T.Density.TextSmall,
            set ? T.TextBrush : T.TextDimBrush,
            FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.MaxWidth = 260;

        return new Border
        {
            Tag = new OverlayTarget.Filter(list, part),
            Background = set ? T.AccentDimBrush : T.Surface2Brush,
            BorderBrush = open ? T.AccentForegroundBrush : set ? T.AccentBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            MinWidth = 96,
            MinHeight = ChipHeight,
            Padding = new Thickness(14, 6),
            Child = label,
        };
    }

    /// <summary>How tall a filter or a choice is: short of a tab, still an easy target for a ray.</summary>
    private double ChipHeight => V is null ? 48 : 36;

    /// <summary>
    /// The open filter's choices, in a strip under the row. One tap picks; a filter that takes
    /// several (rank, kind) stays open so the next can be ticked.
    /// </summary>
    private Control Choices(OverlayPage list, FilterPart part, ListFilters filters)
    {
        var strip = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };

        switch (part)
        {
            case FilterPart.Who:
                foreach (var who in Enum.GetValues<Who>())
                    strip.Children.Add(Choice(who is Who.All ? "All" : WhoWords(who), filters.Who == who, new OverlayTarget.Pick(list, part, (int)who)));
                break;

            case FilterPart.Rank:
                foreach (var rank in RankPick.Offered)
                {
                    strip.Children.Add(Choice(
                        rank is { } known ? TrustRanks.Name(known) : "Not known",
                        filters.Ranks.Has(rank),
                        new OverlayTarget.Pick(list, part, rank is { } value ? (int)value : -1),
                        rank));
                }

                break;

            case FilterPart.Time:
                foreach (var window in Enum.GetValues<TimeWindow>())
                {
                    strip.Children.Add(Choice(
                        window switch
                        {
                            TimeWindow.Any => "Any time",
                            TimeWindow.FiveMinutes => "Last 5m",
                            TimeWindow.FifteenMinutes => "Last 15m",
                            TimeWindow.Hour => "Last 1h",
                            _ => "Earlier",
                        },
                        filters.Time == window,
                        new OverlayTarget.Pick(list, part, (int)window)));
                }

                break;

            case FilterPart.Sort:
                foreach (var order in Enum.GetValues<RosterOrder>())
                    strip.Children.Add(Choice(OrderWords(order), filters.Order == order, new OverlayTarget.Pick(list, part, (int)order)));
                break;

            case FilterPart.Kind:
                for (var i = 0; i < KindPick.Offered.Count; i++)
                {
                    var kind = KindPick.Offered[i];
                    strip.Children.Add(Choice(Words(kind), filters.Kinds.Has(kind), new OverlayTarget.Pick(list, part, i)));
                }

                break;

            case FilterPart.Name:
                strip.Children.Add(NameBox(list, filters.Name));
                if (filters.Name is not null)
                    strip.Children.Add(Choice("Clear", false, new OverlayTarget.Pick(list, part, 0)));
                break;
        }

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.AccentForegroundBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(12, 10),
            Child = strip,
        };
    }

    /// <param name="rank">A rank's colour mark beside its name, for the rank choices.</param>
    private Control Choice(string caption, bool chosen, OverlayTarget target, TrustRank? rank = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        if (rank is { } known)
        {
            line.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = DesignTokens.Brush(Color.Parse(TrustRanks.Colour(known))),
            });
        }

        var label = Text(
            chosen ? "✓ " + caption : caption,
            T.Density.TextSmall,
            chosen ? T.TextBrush : T.TextDimBrush,
            chosen ? FontWeight.SemiBold : FontWeight.Normal);
        label.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(label);
        line.VerticalAlignment = VerticalAlignment.Center;
        line.HorizontalAlignment = HorizontalAlignment.Center;

        return new Border
        {
            Tag = target,
            Background = chosen ? T.AccentDimBrush : T.Surface3Brush,
            BorderBrush = chosen ? T.AccentBrush : T.BorderBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            MinHeight = ChipHeight,
            MinWidth = 72,
            Padding = new Thickness(12, 6),
            Child = line,
        };
    }

    /// <summary>
    /// The name searched for, in a box that looks like one. The text is drawn as text, never
    /// read as anything else, like every name on the panel.
    /// </summary>
    private Control NameBox(OverlayPage list, string? name)
    {
        var label = Text((name ?? string.Empty) + "|", T.Density.TextBase, T.TextBrush);
        label.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Tag = new OverlayTarget.TypeName(list, name ?? string.Empty),
            Background = T.Surface3Brush,
            BorderBrush = T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Width = 360,
            MinHeight = ChipHeight,
            Padding = new Thickness(12, 6),
            Child = label,
        };
    }

    private string WhoWords(Who who) => who switch
    {
        Who.Flagged => "Flagged",
        Who.Members => "Members",
        Who.Staff => "Staff",
        Who.NotInGroup => "Not in group",
        _ => "All",
    };

    private string RankWords(RankPick ranks)
    {
        var first = ranks.Picked.First();
        var name = first is { } known ? TrustRanks.Name(known) : "Not known";
        return ranks.Count == 1 ? name : name + " +" + (ranks.Count - 1);
    }

    private string KindWords(KindPick kinds)
    {
        var picked = kinds.Picked.ToList();
        return picked.Count switch
        {
            1 => Words(picked[0]),
            2 => Words(picked[0]) + ", " + Words(picked[1]),
            _ => Words(picked[0]) + " +" + (picked.Count - 1),
        };
    }

    /// <summary>The time filter's chip, as a window rather than a length: "5m", "15m", "1h", "earlier".</summary>
    private string WindowWords(TimeWindow window) => window switch
    {
        TimeWindow.FiveMinutes => "5m",
        TimeWindow.FifteenMinutes => "15m",
        TimeWindow.Hour => "1h",
        TimeWindow.Earlier => "earlier",
        _ => "any time",
    };

    private string OrderWords(RosterOrder order) => order switch
    {
        RosterOrder.Newest => "Newest",
        RosterOrder.Name => "Name",
        _ => "Standing",
    };

    /// <summary>
    /// What the live link has heard for this instance, newest first: who joined, who left, who
    /// was already here, and a watch ending.
    /// </summary>
    /// <remarks>
    /// Drawn from what the drive loop already drained. Nothing is asked of a server to fill this
    /// screen — opening it makes no request at all.
    /// </remarks>
    private Control EventsPanel(OverlayScreen screen)
    {
        var rows = new StackPanel { Spacing = RowGap };

        var filters = screen.EventFiltersOrNone;
        var all = screen.EventsOrNone;
        var events = ListFiltering.Events(all, filters, screen.Now);

        // How many the filters left, said only while they are hiding something.
        if (filters.Hides)
            rows.Children.Add(Text(events.Count + " of " + all.Count, T.Density.TextSmall, T.TextDimBrush, FontWeight.SemiBold));

        if (events.Count == 0)
        {
            var nothing = all.Count > 0 ? "Nothing matches." : "Nothing yet.";
            rows.Children.Add(V is null ? Text(nothing, T.Density.TextBase, T.TextDimBrush) : InfoCard(nothing));
        }
        else
        {
            foreach (var @event in events.Take(MostEventRows))
                rows.Children.Add(EventRow(@event));
        }

        // In VRChat's look every row is a card of its own, so there is nothing to put them in.
        if (V is not null)
            return new Border { Tag = new OverlayTarget.Events(), Background = Brushes.Transparent, Child = rows };

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

    private Control EventRow(LiveEvent @event)
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
            flagged || V is not null ? T.TextBrush : T.TextDimBrush,
            V is not null ? FontWeight.Bold : flagged ? FontWeight.SemiBold : FontWeight.Normal);
        who.VerticalAlignment = VerticalAlignment.Center;

        // Trimmed rather than allowed to push the clock off the end of the row.
        who.MaxWidth = 200;

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { what, who },
        };

        // The rank, as on the Instance list, so a row the Rank filter kept says why it was kept.
        if (@event.Person is { } person && ListFiltering.RankOf(person) is { } rank)
        {
            var mark = RankLine(rank, T.Density.TextSmall);
            mark.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(mark);
        }

        // The clock the event arrived with, in the moderator's own time. It is the server's
        // stamp, not this machine's.
        var when = Text(@event.At.ToLocalTime().ToString("HH:mm"), T.Density.TextSmall, T.TextDimBrush);
        when.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(when);

        return Row(line, @event.Person?.SubjectId);
    }

    /// <summary>An event kind in plain words. An unknown kind is shown as it came, never guessed at.</summary>
    private string Words(string kind) => kind switch
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
    private Control PersonCard(UserSummary person)
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
        // Why somebody is Flagged -- kicks and bans among the rest -- is the flags line below, in
        // the server's words, so the count is not repeated here.
        lines.Children.Add(Text(
            standing,
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
    private Control HealthBanner(string message) => new Border
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
    private Control AlertCard(FlaggedJoinAlert alert, string? groupLabel)
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

        // The reason already names every rule that matched, kicks and bans included.
        lines.Children.Add(Text(alert.Reason, T.Density.TextBase, T.TextBrush));

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

    private Control RosterPanel(OverlayScreen screen)
    {
        var rows = new StackPanel { Spacing = RowGap };

        // Who is here: only the people present. The rows of people who just left are drawn among
        // them, greyed, and are never part of the count.
        var present = screen.Roster.Value?.Members ?? [];
        var here = present.Count;
        var filters = screen.RosterFiltersOrNone;
        var arrivals = screen.ArrivalsOrNone;
        var everyone = ListFiltering.Everyone(present, screen.LeftOrNone);
        var shown = ListFiltering.Roster(everyone, filters, arrivals, screen.Now);

        var secondsLeft = screen.LeftOrNone
            .GroupBy(leaver => leaver.Member.SubjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().SecondsLeft(screen.Now), StringComparer.Ordinal);
        var presentIds = present.Select(member => member.SubjectId).ToHashSet(StringComparer.Ordinal);
        bool JustLeft(RosterMember member) => !presentIds.Contains(member.SubjectId) && secondsLeft.ContainsKey(member.SubjectId);
        var shownHere = shown.Count(member => !JustLeft(member));

        var count = filters.Hides ? shownHere + " of " + here + " here"
            : here == 1 ? "1 here"
            : here + " here";

        // Always stated, on every panel that came from a server. A list made from this PC's log has
        // no server to be as old as, so it says nothing of the kind.
        var freshness = screen.NotSynced ? null : screen.Roster.Describe();
        var freshnessBrush = screen.Freshness == Freshness.Fresh ? T.TextDimBrush : T.WarnBrush;

        string? nobody = everyone.Count == 0
            ? screen.Freshness == Freshness.Never ? "No roster loaded for this instance." : "Nobody here."
            : shown.Count == 0 ? "Nobody matches." : null;

        if (V is not null)
        {
            // The count and its freshness in a card of their own; the people go under it, each in
            // a card of theirs.
            rows.Children.Add(InfoCard(count, freshness, freshnessBrush, nobody));
        }
        else
        {
            var head = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    Dock(Text(count, T.Density.TextSmall, T.TextDimBrush, FontWeight.SemiBold), Avalonia.Controls.Dock.Left),
                },
            };

            if (freshness is not null)
                head.Children.Add(Dock(Text(freshness, T.Density.TextSmall, freshnessBrush), Avalonia.Controls.Dock.Right));

            rows.Children.Add(head);

            if (nobody is not null)
                rows.Children.Add(Text(nobody, T.Density.TextBase, T.TextDimBrush));
        }

        if (nobody is null)
        {
            // Unless the moderator picked another order: flagged first, then staff, then everybody
            // else. The overlay's job is to put the row that matters where the eye lands
            // (ListFiltering.Roster).

            // Scrolled-past rows are counted, not hidden without a word.
            var skip = Math.Clamp(screen.RosterSkip, 0, Math.Max(0, shown.Count - 1));
            if (skip > 0)
                rows.Children.Add(Text(skip == 1 ? "1 more above" : skip + " more above", T.Density.TextSmall, T.TextDimBrush));

            // How long each person has been here, from this PC's own log. Nothing when the log
            // never mentioned them, rather than a guess.
            foreach (var member in shown.Skip(skip))
            {
                var joined = arrivals.TryGetValue(member.SubjectId, out var at) && screen.Now != default
                    ? ListFiltering.JoinedWords(at, screen.Now)
                    : null;

                // Somebody who just left: a greyed row with the seconds it has left instead of how
                // long they were here, and no "+", because a heads-up is placed on somebody present.
                if (JustLeft(member))
                {
                    rows.Children.Add(RosterRow(member, null, HeadsUpOn(screen.HeadsUpsOrNone, member.SubjectId), false, secondsLeft[member.SubjectId]));
                    continue;
                }

                rows.Children.Add(RosterRow(member, joined, HeadsUpOn(screen.HeadsUpsOrNone, member.SubjectId), screen.CanPlaceHeadsUps));
            }
        }

        if (V is not null)
            return new Border { Tag = new OverlayTarget.Roster(), Background = Brushes.Transparent, Child = rows };

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

    /// <summary>
    /// A card in VRChat's look that says one thing large, with the state of it at the far end and a
    /// line of words under it: the roster's count and how fresh it is, or "Nothing yet".
    /// </summary>
    private Control InfoCard(string big, string? state = null, IBrush? stateBrush = null, string? message = null)
    {
        var lines = new StackPanel { Spacing = 4 };

        var top = new DockPanel { LastChildFill = false };

        var heading = Text(big, 19, V!.Text, FontWeight.Bold);
        DockPanel.SetDock(heading, Avalonia.Controls.Dock.Left);
        top.Children.Add(heading);

        if (state is not null)
        {
            var end = Text(state, T.Density.TextSmall, stateBrush ?? V.Subtext, FontWeight.Bold);
            end.VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(end, Avalonia.Controls.Dock.Right);
            top.Children.Add(end);
        }

        lines.Children.Add(top);

        if (message is not null)
            lines.Children.Add(Text(message, 15, V.Subtext));

        return new Border
        {
            Background = V.Button,
            BorderBrush = T.BorderBrush,
            BorderThickness = new Thickness(VRChatLook.EdgeWidth),
            CornerRadius = new CornerRadius(VRChatLook.CardRadius),
            Padding = new Thickness(14, 12),
            Child = lines,
        };
    }

    /// <param name="joined">How long they have been here, in words, or null when it is not known.</param>
    /// <param name="headsUp">The kind of heads-up standing on this person, or null.</param>
    /// <param name="canAdd">Draw the "+" that starts a heads-up from this row.</param>
    /// <param name="secondsLeft">
    /// Set for somebody who has just left: the row is greyed, carries a "Left" tag, and says how many
    /// seconds it stays. Null for somebody who is here.
    /// </param>
    private Control RosterRow(RosterMember member, string? joined, HeadsUpKind? headsUp = null, bool canAdd = false, int? secondsLeft = null)
    {
        var left = secondsLeft is not null;

        var dot = new Ellipse
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

        // In VRChat's look the dot sits on a round stand-in for a profile picture, as the mock has
        // it, and says the same thing it always did.
        Control badge = V is null ? dot : Avatar(dot);

        var name = Text(
            member.DisplayName ?? member.SubjectId,
            T.Density.TextBase,
            !left && (V is not null || member.Standing == RosterStanding.Flagged)
                ? T.TextBrush
                : T.TextDimBrush,
            V is not null ? FontWeight.Bold : member.Standing == RosterStanding.Flagged ? FontWeight.SemiBold : FontWeight.Normal);
        name.VerticalAlignment = VerticalAlignment.Center;

        // A long name trims rather than shoving what follows it off the end of the row. That is
        // what used to send a person's flags drifting away from the person they belong to.
        name.MaxWidth = 220;

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { badge, name },
        };

        // The rank in its VRChat colour, after the name and before the flags, on the same line.
        if (member.TrustRank is { } rank)
        {
            var mark = RankLine(rank, T.Density.TextSmall);
            mark.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(mark);
        }

        if (member.EighteenPlus == true)
            line.Children.Add(EighteenPlusChip());

        if (member.Flags.Count > 0)
            line.Children.Add(FlagChip(string.Join(" · ", member.Flags)));

        if (headsUp is { } kind)
            line.Children.Add(HeadsUpChip(HeadsUpRules.Name(kind)));

        if (secondsLeft is { } seconds)
        {
            // Greyed by fading everything about the person, in the colours the look already has,
            // so it follows the palette on the desktop window and the headset's own on a headset.
            // The tag and the countdown stay at full strength: they are what the row now says.
            line.Opacity = LeftRowOpacity;

            return Row(
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { line, LeftChip() } },
                member.SubjectId,
                seconds + "s");
        }

        return Row(line, member.SubjectId, joined, canAdd ? AddHeadsUpPress(member.SubjectId) : null);
    }

    /// <summary>How much of its strength a row keeps once somebody has left.</summary>
    private const double LeftRowOpacity = 0.5;

    /// <summary>The "Left" tag on the row of somebody who has just gone.</summary>
    private Control LeftChip()
    {
        var label = Text("Left", V is null ? T.Density.TextSmall : T.Density.TextTiny, T.TextDimBrush, V is null ? FontWeight.SemiBold : FontWeight.Bold);
        label.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Background = new SolidColorBrush(T.Palette.TextDim, 0.16),
            BorderBrush = T.TextDimBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = SmallCorner,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>
    /// A round stand-in for a person's picture, in the icon colour, with the standing dot at its
    /// lower edge. Roster rows have no picture to show, so this is not one.
    /// </summary>
    private Control Avatar(Ellipse standing)
    {
        const double Size = 30;

        standing.Stroke = V!.Button;
        standing.StrokeThickness = 2;
        standing.HorizontalAlignment = HorizontalAlignment.Right;
        standing.VerticalAlignment = VerticalAlignment.Bottom;
        standing.Margin = new Thickness(0, 0, -3, -3);

        return new Grid
        {
            Width = Size,
            Height = Size,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Fill = V.Icon, Opacity = 0.9 },
                standing,
            },
        };
    }

    /// <summary>
    /// A trust rank as the mock draws a small mark: a dark, half see-through pill with the rank's
    /// VRChat colour as a dot and its name in bold. The colour is the same one every surface uses.
    /// </summary>
    private Control RankPill(TrustRank rank, double size)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = DesignTokens.Brush(Color.Parse(TrustRanks.Colour(rank))),
        };

        var name = Text(TrustRanks.Name(rank), T.Density.TextTiny, T.TextBrush, FontWeight.Bold);
        name.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Background = VRChatLook.PillGround,
            BorderBrush = T.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(VRChatLook.PillRadius),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { dot, name },
            },
        };
    }

    /// <summary>
    /// The kind of the heads-up standing on a person, for the chip on their row: Keep an eye before
    /// Message, because it is the one that outlasts them leaving.
    /// </summary>
    private HeadsUpKind? HeadsUpOn(IReadOnlyList<HeadsUp> headsUps, string subjectId)
    {
        HeadsUpKind? found = null;

        foreach (var headsUp in headsUps)
        {
            if (!string.Equals(headsUp.SubjectId, subjectId, StringComparison.Ordinal) || headsUp.KindOrNull is not { } kind)
                continue;

            if (kind is HeadsUpKind.KeepAnEye)
                return kind;

            found ??= kind;
        }

        return found;
    }

    /// <summary>A heads-up's kind as a mark on the row of the person it is about.</summary>
    private Control HeadsUpChip(string caption)
    {
        var label = Text(caption, V is null ? T.Density.TextSmall : T.Density.TextTiny, T.AccentForegroundBrush, V is null ? FontWeight.SemiBold : FontWeight.Bold);
        label.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Background = T.AccentDimBrush,
            BorderBrush = T.AccentBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = SmallCorner,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>
    /// The "+" at a roster row's far end, which starts a heads-up from that row. Its own target
    /// inside the row's, so it wins over opening the person.
    /// </summary>
    private Control AddHeadsUpPress(string subjectId)
    {
        var label = Text("+", T.Density.TextBase, T.TextBrush, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;

        return new Border
        {
            Tag = new OverlayTarget.AddHeadsUp(subjectId),
            Background = T.Surface2Brush,
            BorderBrush = T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Width = 36,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>
    /// A heads-up being written: its kind, the place for Ask for help, its words, and Place and
    /// Cancel. Nothing in it has left the PC until Place is pressed.
    /// </summary>
    private Control DraftStrip(HeadsUpDraft draft, bool noKeyboard)
    {
        var lines = new StackPanel { Spacing = 10 };

        lines.Children.Add(Text(
            draft.AboutPerson ? "Heads-up · " + (draft.SubjectName ?? draft.SubjectId) : "Heads-up",
            T.Density.TextBase,
            T.TextBrush,
            FontWeight.SemiBold));

        var kinds = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var kind in Enum.GetValues<HeadsUpKind>())
            kinds.Children.Add(Choice(HeadsUpRules.Name(kind), draft.Kind == kind, new OverlayTarget.HeadsUpKindPick(kind)));
        lines.Children.Add(kinds);

        if (draft.Kind is HeadsUpKind.Message)
        {
            lines.Children.Add(new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemSpacing = 8,
                LineSpacing = 8,
                Children =
                {
                    Choice(draft.SubjectName ?? draft.SubjectId, !draft.OnInstance, new OverlayTarget.HeadsUpAboutPick(false)),
                    Choice("Instance", draft.OnInstance, new OverlayTarget.HeadsUpAboutPick(true)),
                },
            });
        }

        if (draft.Kind is HeadsUpKind.AskForHelp)
        {
            var places = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
            foreach (var place in HeadsUpRules.Places)
                places.Children.Add(Choice(place, string.Equals(draft.Place, place, StringComparison.Ordinal), new OverlayTarget.HeadsUpPlacePick(place)));
            lines.Children.Add(places);
        }

        // A headset with no keyboard cannot write anything, so it gets no box to write in; the
        // kinds that need no words can still be placed from it.
        if (!noKeyboard || draft.Text.Length > 0)
            lines.Children.Add(HeadsUpBox(draft.Text));

        if (draft.Problem is { Length: > 0 } problem)
            lines.Children.Add(Text(problem, T.Density.TextSmall, T.WarnBrush, FontWeight.SemiBold));

        lines.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Press(draft.Sending ? "Placing…" : "Place", new OverlayTarget.PlaceHeadsUp()),
                Press("Cancel", new OverlayTarget.CancelHeadsUp()),
            },
        });

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.AccentForegroundBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(18, 14),
            Child = lines,
        };
    }

    /// <summary>
    /// A heads-up's words, in a box that looks like one, wrapping onto a second line rather than
    /// trimming what the moderator is still writing.
    /// </summary>
    private Control HeadsUpBox(string text)
    {
        var label = Text(text + "|", T.Density.TextBase, T.TextBrush);
        label.TextWrapping = TextWrapping.Wrap;
        label.MaxLines = 3;
        label.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Tag = new OverlayTarget.TypeHeadsUp(text),
            Background = T.Surface3Brush,
            BorderBrush = T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            MinHeight = ChipHeight,
            Padding = new Thickness(12, 6),
            Child = label,
        };
    }

    /// <summary>
    /// The heads-ups standing here, oldest first: what kind, about whom or where, the words, and who
    /// placed it, with Clear on each.
    /// </summary>
    /// <remarks>
    /// Every word in it is another moderator's or a display name, so each is placed as text and cut
    /// to its line, like every name on the panel.
    /// </remarks>
    private Control HeadsUpList(IReadOnlyList<HeadsUp> headsUps, bool canClear)
    {
        var rows = new StackPanel { Spacing = RowGap };

        foreach (var headsUp in headsUps)
        {
            if (headsUp.KindOrNull is not { } kind)
                continue;

            var what = Text(HeadsUpRules.Name(kind), T.Density.TextSmall, T.AccentForegroundBrush, FontWeight.SemiBold);
            what.VerticalAlignment = VerticalAlignment.Center;

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { what } };

            if (headsUp.About is { } about)
            {
                var whom = Text(about, T.Density.TextBase, T.TextBrush, FontWeight.SemiBold);
                whom.VerticalAlignment = VerticalAlignment.Center;
                whom.MaxWidth = 200;
                line.Children.Add(whom);
            }

            if (headsUp.Text is { } words)
            {
                var said = Text(words, T.Density.TextBase, T.TextBrush);
                said.VerticalAlignment = VerticalAlignment.Center;
                said.MaxWidth = 360;
                line.Children.Add(said);
            }

            var by = Text("· " + headsUp.PlacedBy, T.Density.TextSmall, T.TextDimBrush);
            by.VerticalAlignment = VerticalAlignment.Center;
            by.MaxWidth = 160;
            line.Children.Add(by);

            var row = new DockPanel { LastChildFill = true, Height = RowBoxHeight };

            if (canClear)
            {
                var clear = Choice("Clear", false, new OverlayTarget.ClearHeadsUp(headsUp.Id));
                clear.MinHeight = 32;
                clear.VerticalAlignment = VerticalAlignment.Center;
                clear.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(clear, Avalonia.Controls.Dock.Right);
                row.Children.Add(clear);
            }

            line.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(line);
            rows.Children.Add(row);
        }

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.AccentBrush,
            BorderThickness = new Thickness(6, T.Density.Hairline, T.Density.Hairline, T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            Padding = new Thickness(18, 10),
            Child = rows,
        };
    }

    /// <summary>How tall one roster or events row is, in panel pixels.</summary>
    /// <remarks>
    /// Tighter than the density's row height, which left more air than name between two rows.
    /// Each row is its own box now, so the eye no longer needs that air to tell rows apart, and
    /// 40 of the texture's 1,024 pixels is still about 1.8 cm on a panel 45 cm wide: easy to land
    /// a controller's ray on.
    /// </remarks>
    private double RowBoxHeight => V is null ? 40 : 48;

    /// <summary>The gap between two rows' boxes.</summary>
    private double RowGap => V is null ? 4 : 8;

    /// <summary>
    /// One row of a list: a box that opens a person's card when <paramref name="subjectId"/> is
    /// known, or the same line with no box when there is nobody to open.
    /// </summary>
    /// <remarks>
    /// The box is the whole of what a tap or a click lands on, drawn, so a moderator can see where
    /// a row starts and ends rather than guessing at a name. On the desktop overlay window it is
    /// also lit while the mouse is over it. A row with nothing behind it has no box, which is how
    /// it says so.
    /// </remarks>
    /// <param name="trailing">Words at the row's far end, such as how long somebody has been here.</param>
    /// <param name="press">A control at the very end of the row, after the words, or null.</param>
    private Control Row(Control line, string? subjectId, string? trailing = null, Control? press = null)
    {
        line.VerticalAlignment = VerticalAlignment.Center;

        Control content = line;
        if (trailing is not null || press is not null)
        {
            var dock = new DockPanel { LastChildFill = true };

            if (press is not null)
            {
                press.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(press, Avalonia.Controls.Dock.Right);
                dock.Children.Add(press);
            }

            if (trailing is not null)
            {
                var end = Text(trailing, T.Density.TextSmall, T.TextDimBrush);
                end.VerticalAlignment = VerticalAlignment.Center;
                end.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(end, Avalonia.Controls.Dock.Right);
                dock.Children.Add(end);
            }

            dock.Children.Add(line);
            content = dock;
        }

        var row = new Border
        {
            Height = RowBoxHeight,
            Padding = new Thickness(12, 0),
            CornerRadius = T.CornerRadius,
            Child = content,
        };

        if (subjectId is null)
            return row;

        // In VRChat's look a row with somebody behind it is a card, edge and all.
        if (V is not null)
        {
            row.BorderBrush = T.BorderBrush;
            row.BorderThickness = new Thickness(T.Density.Hairline);
            row.ClipToBounds = true;
        }

        row.Tag = new OverlayTarget.Person(subjectId);
        row.Background = T.Surface3Brush;
        row.PointerEntered += (_, _) => row.Background = T.AccentDimBrush;
        row.PointerExited += (_, _) => row.Background = T.Surface3Brush;

        return row;
    }

    /// <summary>
    /// What is known against a person — "1 kick or ban", "5 warns", "Nuisance" — as a mark on their own
    /// row.
    /// </summary>
    /// <remarks>
    /// A tinted chip rather than loose red words, so it reads as belonging to the row it sits on.
    /// It used to be plain text at the end of a horizontal row, which put it wherever the name
    /// happened to leave it — usually far to the right, next to nobody.
    /// </remarks>
    private Control FlagChip(string caption)
    {
        var label = Text(caption, V is null ? T.Density.TextSmall : T.Density.TextTiny, T.DangerBrush, V is null ? FontWeight.SemiBold : FontWeight.Bold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.MaxWidth = 200;

        return new Border
        {
            Background = new SolidColorBrush(T.Palette.Danger, 0.16),
            BorderBrush = T.DangerBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = SmallCorner,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>
    /// Modbot's 18+ mark on a roster row. Shown only on those who carry it, so somebody without it
    /// gets no mark at all.
    /// </summary>
    private Control EighteenPlusChip() => V is null
        ? PersonMarks.EighteenPlusChip(T)
        : PersonMarks.EighteenPlusChip(T, new CornerRadius(VRChatLook.PillRadius), T.Density.TextTiny);

    /// <summary>How round a small mark on a row is: the headset's own corner, or the mock's smaller one.</summary>
    private CornerRadius SmallCorner => V is null ? T.CornerRadius : new CornerRadius(VRChatLook.PillRadius);

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
    private Control SaveClipBar(ClipButton clip)
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
    private Control Press(string caption, OverlayTarget target)
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

    private TextBlock Text(
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

    private Control Dock(Control control, Dock side)
    {
        DockPanel.SetDock(control, side);
        return control;
    }
}
