using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Shared.HeadsUps;

namespace Modbot.Overlay.Views;

/// <summary>
/// The people rows of the desktop window's look: the roster under "Users" and the Audit Log, drawn the
/// way VRChat's own Users list is, as cards with a picture, a name and a line under it.
/// </summary>
/// <remarks>
/// <para>Only the VRChat look draws these. The headset's rows are the ones they always were
/// (<see cref="RosterRow"/>, <see cref="EventRow"/>), and nothing here is reached while the screen is drawn the
/// headset's way.</para>
/// <para>The panel's width is the window's and the rows fit it: the text column takes what is left
/// of a card and cuts what does not fit with an ellipsis, so no name, mark or line of words can make a
/// card, and with it the panel, wider than it is.</para>
/// <para>Nothing here fetches anything. A picture is the companion's own cache's answer for an address
/// the roster carried, and until it has arrived, or when there is none, the card shows the head and shoulders.</para>
/// </remarks>
public sealed partial class OverlayView
{
    /// <summary>How big a person's picture is drawn, in panel pixels: a rounded square, as VRChat's Users list has it.</summary>
    private const double AvatarSize = 48;

    /// <summary>How tall a person's card is: the picture and a little air above and below it.</summary>
    private const double CardHeight = 68;

    /// <summary>The gap between two cards.</summary>
    private const double CardGap = 7;

    /// <summary>How wide and tall the square button at the end of a card is.</summary>
    private const double SquareButton = 40;

    /// <summary>The picture address of each person a screen carries one for, by their id.</summary>
    private static Dictionary<string, string> PicturesOn(OverlayScreen screen)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var member in screen.Roster.Value?.Members ?? [])
        {
            if (member.PictureUrl is { Length: > 0 } address)
                found[member.SubjectId] = address;
        }

        foreach (var leaver in screen.LeftOrNone)
        {
            if (leaver.Member.PictureUrl is { Length: > 0 } address)
                found.TryAdd(leaver.Member.SubjectId, address);
        }

        return found;
    }

    /// <summary>
    /// VRChat's heading over its list: "Users (12)" with a chevron after it, and at the far end how old
    /// the list is. Not something that folds; the chevron is how VRChat's heading looks.
    /// </summary>
    private Control UsersHeading(string words, string? state, IBrush stateBrush)
    {
        var v = V!;

        var heading = Text(words, 20, v.Text, FontWeight.Bold);
        heading.VerticalAlignment = VerticalAlignment.Center;

        var chevron = new Viewbox
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M6 9 L12 15 L18 9"),
                Stroke = v.Icon,
                StrokeThickness = 2.6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24,
            },
        };

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 2, 2, 0) };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { heading, chevron } };
        DockPanel.SetDock(left, Avalonia.Controls.Dock.Left);
        head.Children.Add(left);

        if (state is not null)
        {
            var end = Text(state, T.Density.TextSmall, stateBrush, FontWeight.Bold);
            end.VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(end, Avalonia.Controls.Dock.Right);
            head.Children.Add(end);
        }

        return head;
    }

    /// <summary>A small label over a run of cards, in the look's heading colour: "Other users (11)".</summary>
    private Control SectionLabel(string words)
    {
        var label = Text(words, T.Density.TextSmall, V!.Heading, FontWeight.Bold);
        label.LetterSpacing = 0.6;
        label.Margin = new Thickness(2, 6, 0, 0);
        return label;
    }

    /// <summary>
    /// The people under the heading: the moderator's own card first, in a banner in the highlights
    /// colour, then a label, then everybody else, with the rows scrolled past counted and not hidden
    /// without a word.
    /// </summary>
    /// <remarks>
    /// <para>Without a moderator's own card (their id is not known, or the filters have hidden them) there is
    /// no banner and no label, and everybody is under the heading as one list.</para>
    /// <para>The banner stays where it is while the others scroll under it. The count in the label is
    /// of the people present, as the heading's is: somebody who has just left is a row and not a person
    /// counted.</para>
    /// </remarks>
    private void AddVRChatRoster(
        StackPanel rows,
        OverlayScreen screen,
        IReadOnlyList<RosterMember> shown,
        IReadOnlyDictionary<string, DateTimeOffset?> arrivals,
        Func<RosterMember, bool> justLeft,
        IReadOnlyDictionary<string, int> secondsLeft)
    {
        var mine = screen.ModeratorId is { Length: > 0 } id
            ? shown.FirstOrDefault(member => string.Equals(member.SubjectId, id, StringComparison.Ordinal) && !justLeft(member))
            : null;

        IReadOnlyList<RosterMember> others = mine is null
            ? shown
            : [.. shown.Where(member => !string.Equals(member.SubjectId, mine.SubjectId, StringComparison.Ordinal))];

        Control RowFor(RosterMember member, bool banner)
        {
            // How long each person has been here, from this PC's own log. Nothing when the log never
            // mentioned them, rather than a guess.
            var joined = arrivals.TryGetValue(member.SubjectId, out var at) && screen.Now != default
                ? ListFiltering.JoinedWords(at, screen.Now, screen.ModeratorArrived)
                : null;

            // Somebody who just left: a faded card with the seconds it has left, and no "+", because
            // a heads-up is placed on somebody present. Nor is the moderator's own card given one.
            return justLeft(member)
                ? VRChatRosterRow(member, null, HeadsUpOn(screen.HeadsUpsOrNone, member.SubjectId), false, secondsLeft[member.SubjectId], banner)
                : VRChatRosterRow(member, joined, HeadsUpOn(screen.HeadsUpsOrNone, member.SubjectId), screen.CanPlaceHeadsUps && !banner, null, banner);
        }

        if (mine is not null)
        {
            rows.Children.Add(RowFor(mine, true));

            if (others.Count > 0)
                rows.Children.Add(SectionLabel($"Other users ({others.Count(member => !justLeft(member))})"));
        }

        // Scrolled-past rows are counted, not hidden without a word.
        var skip = Math.Clamp(screen.RosterSkip, 0, Math.Max(0, others.Count - 1));
        if (skip > 0)
            rows.Children.Add(Text(skip == 1 ? "1 more above" : skip + " more above", T.Density.TextSmall, V!.Subtext));

        foreach (var member in others.Skip(skip))
            rows.Children.Add(RowFor(member, false));
    }

    /// <summary>
    /// One person on the Instance list: their picture, their name in bold with their marks at the end
    /// of the line, and under it when they came, or the "Left" tag and the seconds the card has left.
    /// </summary>
    /// <param name="joined">How long they have been here, in words, or null when it is not known.</param>
    /// <param name="headsUp">The kind of heads-up standing on this person, or null.</param>
    /// <param name="canAdd">Draw the square "+" at the end that starts a heads-up from this card.</param>
    /// <param name="secondsLeft">Set for somebody who has just left; null for somebody who is here.</param>
    /// <param name="banner">The moderator's own card: in the highlights colour, and never with a "+".</param>
    private Control VRChatRosterRow(RosterMember member, string? joined, HeadsUpKind? headsUp, bool canAdd, int? secondsLeft, bool banner)
    {
        var v = V!;

        var marks = new List<Control>();

        // The rank in its VRChat colour, then the 18+ mark, the flags and the heads-up, as they have always run.
        if (member.TrustRank is { } rank)
            marks.Add(RankPill(rank, T.Density.TextSmall));

        if (member.EighteenPlus == true)
            marks.Add(EighteenPlusChip());

        if (member.Flags.Count > 0)
            marks.Add(FlagChip(string.Join(" · ", member.Flags)));

        if (headsUp is { } kind)
            marks.Add(HeadsUpChip(HeadsUpRules.Name(kind)));

        var top = NameLine(member.DisplayName ?? member.SubjectId, banner ? v.BannerText : v.Text, marks);
        var avatar = VRChatAvatar(member.SubjectId, StandingDot(member, 14));

        Control? under = null;

        if (secondsLeft is { } seconds)
        {
            // Faded by fading everything about the person, in the colours the look already has. The tag
            // and the countdown stay at full strength: they are what the card now says.
            top.Opacity = LeftRowOpacity;
            avatar.Opacity = LeftRowOpacity;

            var countdown = Text(seconds + "s", T.Density.TextSmall, v.Text, FontWeight.Bold);
            countdown.VerticalAlignment = VerticalAlignment.Center;

            under = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { LeftChip(), countdown } };
        }
        else if (joined is not null)
        {
            under = SubLine(joined, banner);
        }

        return VRChatCard(avatar, top, under, null, canAdd ? AddHeadsUpPress(member.SubjectId) : null, member.SubjectId, banner);
    }

    /// <summary>
    /// One thing the live link heard, as a card the way the Instance list's are: who, what happened
    /// under their name, and the time at the end.
    /// </summary>
    private Control VRChatEventRow(LiveEvent @event, OverlayScreen screen)
    {
        var v = V!;
        var flagged = @event.Flagged || @event.Kind == LiveEventKinds.FlaggedJoin;
        var person = @event.Person;

        var marks = new List<Control>();

        // The rank, as on the Instance list, so a row the Rank filter kept says why it was kept.
        if (person is not null && ListFiltering.RankOf(person) is { } rank)
            marks.Add(RankPill(rank, T.Density.TextSmall));

        var top = NameLine(person?.DisplayName ?? person?.SubjectId ?? "—", v.Text, marks);

        var what = Text(
            EventWords(@event, screen),
            T.Density.TextSmall,
            flagged ? T.DangerBrush : v.Subtext,
            FontWeight.SemiBold);

        // The clock the event arrived with, in the moderator's own time. It is the server's stamp,
        // not this machine's.
        var when = Text(@event.At.ToLocalTime().ToString("HH:mm"), T.Density.TextSmall, v.Subtext, FontWeight.Bold);
        when.VerticalAlignment = VerticalAlignment.Center;

        return VRChatCard(
            person is null ? null : VRChatAvatar(person.SubjectId, null),
            top,
            what,
            when,
            null,
            person?.SubjectId,
            false);
    }

    /// <summary>
    /// A person's name in bold, with their marks at the end of the line. The name is cut with an
    /// ellipsis before the marks are crowded out of the row, and the marks never push past it.
    /// </summary>
    private Control NameLine(string name, IBrush brush, IReadOnlyList<Control> marks)
    {
        var words = Text(name, T.Density.TextBase, brush, FontWeight.Bold);
        words.VerticalAlignment = VerticalAlignment.Center;

        if (marks.Count == 0)
            return words;

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 220,
            ClipToBounds = true,
        };

        foreach (var mark in marks)
        {
            mark.VerticalAlignment = VerticalAlignment.Center;
            strip.Children.Add(mark);
        }

        DockPanel.SetDock(strip, Avalonia.Controls.Dock.Right);

        return new DockPanel { LastChildFill = true, Children = { strip, words } };
    }

    /// <summary>The small line of words under a name: when somebody came, or the words that say they were here before.</summary>
    private Control SubLine(string words, bool banner)
    {
        var line = Text(words, T.Density.TextSmall, banner ? V!.BannerText : V!.Subtext);
        line.Opacity = banner ? 0.85 : 1;
        return line;
    }

    /// <summary>
    /// A card in VRChat's look: the picture at the left, the name and the line under it in the middle,
    /// then what goes at the end and the square button after it. A card with somebody behind it opens
    /// their card when pressed, and is lit under the mouse.
    /// </summary>
    /// <remarks>
    /// <para>The middle column takes what is left, so the card is as wide as the panel gives it and
    /// no wider whatever is written in it. A card with nobody behind it (an event with no person) has
    /// no edge and no target, which is how it says so.</para>
    /// <para>The edge, the shadow and the lighting under the mouse belong to the card and the
    /// contents are not cut by it, so the shadow can fall outside.</para>
    /// </remarks>
    /// <param name="avatar">The picture, or null for a card with no one on it.</param>
    /// <param name="top">The first line: the name.</param>
    /// <param name="under">The line under it, or null.</param>
    /// <param name="trailing">Words at the end of the card, such as the time, or null.</param>
    /// <param name="press">A button after those, or null.</param>
    /// <param name="subjectId">Whose card a press opens, or null when there is nobody to open.</param>
    /// <param name="banner">Drawn as the moderator's own: in the highlights colour.</param>
    private Control VRChatCard(Control? avatar, Control top, Control? under, Control? trailing, Control? press, string? subjectId, bool banner)
    {
        var v = V!;

        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(top);

        if (under is not null)
            words.Children.Add(under);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        if (avatar is not null)
        {
            avatar.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);
        }

        Grid.SetColumn(words, 1);
        grid.Children.Add(words);

        if (trailing is not null)
        {
            trailing.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        if (press is not null)
        {
            press.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(press, 3);
            grid.Children.Add(press);
        }

        var card = new Border
        {
            Height = CardHeight,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(VRChatLook.CardRadius),
            Child = grid,
        };

        if (subjectId is null)
            return card;

        card.BorderBrush = banner ? v.RaisedBrightEdge : v.RaisedEdge;
        card.BorderThickness = new Thickness(VRChatLook.EdgeWidth);
        card.BoxShadow = v.CardShadow;
        card.Tag = new OverlayTarget.Person(subjectId);

        if (banner)
        {
            card.Background = v.Banner;
            return card;
        }

        card.Background = T.Surface3Brush;
        card.PointerEntered += (_, _) => card.Background = T.AccentDimBrush;
        card.PointerExited += (_, _) => card.Background = T.Surface3Brush;

        return card;
    }

    /// <summary>
    /// A person's picture as a rounded square, with the standing dot at its lower corner when they have one.
    /// </summary>
    /// <remarks>
    /// The picture is whatever the companion's cache answers for the address the roster carried. The head
    /// and shoulders in the look's own colours are what shows until it arrives, when there is no
    /// address (somebody in an instance no group owns, or whom the server holds no picture of), and
    /// when it will not load.
    /// </remarks>
    private Control VRChatAvatar(string? subjectId, Ellipse? standing)
    {
        var v = V!;

        var picture = subjectId is not null && _pictureOf.TryGetValue(subjectId, out var address)
            ? _picture?.Invoke(address)
            : null;

        Control face = picture is not null
            ? new Image { Source = picture, Stretch = Stretch.UniformToFill, Width = AvatarSize, Height = AvatarSize }
            : new Viewbox
            {
                Width = AvatarSize * 0.62,
                Height = AvatarSize * 0.62,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(PersonIcon),
                    Fill = v.IconOnEdge,
                    Width = 24,
                    Height = 24,
                },
            };

        var square = new Border
        {
            Background = v.Edge,
            CornerRadius = new CornerRadius(VRChatLook.CardRadius),
            ClipToBounds = true,
            Child = face,
        };

        var grid = new Grid
        {
            Width = AvatarSize,
            Height = AvatarSize,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { square },
        };

        if (standing is not null)
        {
            standing.Stroke = v.Button;
            standing.StrokeThickness = 2;
            standing.HorizontalAlignment = HorizontalAlignment.Right;
            standing.VerticalAlignment = VerticalAlignment.Bottom;
            standing.Margin = new Thickness(0, 0, -4, -4);
            grid.Children.Add(standing);
        }

        return grid;
    }

    /// <summary>The dot that says what a person's standing is: flagged, staff, a member, or nothing on record.</summary>
    private Ellipse StandingDot(RosterMember member, double size) => new()
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        Fill = member.Standing switch
        {
            RosterStanding.Flagged => T.DangerBrush,
            RosterStanding.Staff => T.AccentForegroundBrush,
            RosterStanding.Member => T.OkBrush,
            _ => T.BorderBrush,
        },
    };
}
