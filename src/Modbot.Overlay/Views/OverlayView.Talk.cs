using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Clips;
using Modbot.Companion.Talk;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>
/// Clip and the Talk buttons on one row, in two groups: Clip at one side, the audio icons at the other.
/// </summary>
public sealed partial class OverlayView
{
    // Drawn on a 24-unit square, like the eye on the show and hide button.
    // A clapperboard with its striped arm lifted, the Lucide one.
    private const string ClapperGlyph =
        "M20.2,6 L3,11 L2.1,8.6 C1.8,7.5 2.4,6.4 3.4,6.1 L16.9,2.1 C18,1.8 19.1,2.4 19.4,3.4 Z "
        + "M6.2,5.3 L9.3,9.2 M12.4,3.4 L15.5,7.4 "
        + "M3,11 L21,11 L21,19 A2,2 0 0 1 19,21 L5,21 A2,2 0 0 1 3,19 Z";

    // The Discord and VRChat marks, filled, from Simple Icons (CC0), on the same 24-unit square.
    private const string DiscordGlyph =
        "M 20.317 4.3698 a 19.7913 19.7913 0 0 0 -4.8851 -1.5152 .0741 .0741 0 0 0 -.0785 .0371 c -.211 .3753 -.4447 .8648 -.6083 1.2495 -1.8447 -.2762 -3.68 -.2762 -5.4868 0 -.1636 -.3933 -.4058 -.8742 -.6177 -1.2495 a .077 .077 0 0 0 -.0785 -.037 19.7363 19.7363 0 0 0 -4.8852 1.515 .0699 .0699 0 0 0 -.0321 .0277 C .5334 9.0458 -.319 13.5799 .0992 18.0578 a .0824 .0824 0 0 0 .0312 .0561 c 2.0528 1.5076 4.0413 2.4228 5.9929 3.0294 a .0777 .0777 0 0 0 .0842 -.0276 c .4616 -.6304 .8731 -1.2952 1.226 -1.9942 a .076 .076 0 0 0 -.0416 -.1057 c -.6528 -.2476 -1.2743 -.5495 -1.8722 -.8923 a .077 .077 0 0 1 -.0076 -.1277 c .1258 -.0943 .2517 -.1923 .3718 -.2914 a .0743 .0743 0 0 1 .0776 -.0105 c 3.9278 1.7933 8.18 1.7933 12.0614 0 a .0739 .0739 0 0 1 .0785 .0095 c .1202 .099 .246 .1981 .3728 .2924 a .077 .077 0 0 1 -.0066 .1276 12.2986 12.2986 0 0 1 -1.873 .8914 .0766 .0766 0 0 0 -.0407 .1067 c .3604 .698 .7719 1.3628 1.225 1.9932 a .076 .076 0 0 0 .0842 .0286 c 1.961 -.6067 3.9495 -1.5219 6.0023 -3.0294 a .077 .077 0 0 0 .0313 -.0552 c .5004 -5.177 -.8382 -9.6739 -3.5485 -13.6604 a .061 .061 0 0 0 -.0312 -.0286 z "
        + "M 8.02 15.3312 c -1.1825 0 -2.1569 -1.0857 -2.1569 -2.419 0 -1.3332 .9555 -2.4189 2.157 -2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332 -.9555 2.4189 -2.1569 2.4189 z "
        + "M 15.9948 15.3312 c -1.1825 0 -2.1569 -1.0857 -2.1569 -2.419 0 -1.3332 .9554 -2.4189 2.1569 -2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332 -.946 2.4189 -2.1568 2.4189 Z";

    private const string VRChatGlyph =
        "M 22.732 6.767 H 1.268 A 1.27 1.27 0 0 0 0 8.035 v 5.296 c 0 .7 .57 1.268 1.268 1.268 h 18.594 l 1.725 2.22 c .215 .275 .443 .415 .68 .415 .153 0 .296 -.06 .403 -.167 .128 -.129 .193 -.308 .193 -.536 l -.002 -1.939 A 1.27 1.27 0 0 0 24 13.331 V 8.035 c 0 -.7 -.569 -1.269 -1.268 -1.269 Z "
        + "M 23.532 13.331 a .8 .8 0 0 1 -.8 .801 h -.34 v .031 l .004 2.371 c 0 .155 -.05 .233 -.129 .233 s -.19 -.079 -.31 -.235 l -1.866 -2.4 H 1.268 a .8 .8 0 0 1 -.8 -.8 V 8.064 a .8 .8 0 0 1 .8 -.8 h 21.464 a .8 .8 0 0 1 .8 .8 v 5.266 Z "
        + "M 4.444 8.573 c -.127 0 -.225 .041 -.254 .15 l -.877 3.129 -.883 -3.128 c -.03 -.11 -.127 -.15 -.254 -.15 -.202 0 -.473 .126 -.473 .311 0 .012 .005 .035 .011 .058 l 1.114 3.63 c .058 .173 .265 .254 .485 .254 s .433 -.08 .484 -.254 l 1.109 -3.63 c .005 -.023 .011 -.04 .011 -.058 0 -.179 -.27 -.312 -.473 -.312 Z "
        + "M 7.369 10.933 c .433 -.132 .757 -.49 .757 -1.153 0 -.918 -.612 -1.207 -1.368 -1.207 H 5.614 a .234 .234 0 0 0 -.242 .231 v 3.752 c 0 .156 .184 .237 .374 .237 s .376 -.081 .376 -.237 V 11.05 h .484 l .82 1.593 c .058 .115 .156 .179 .26 .179 .219 0 .467 -.203 .467 -.393 a .155 .155 0 0 0 -.028 -.092 l -.756 -1.403 Z "
        + "M 6.759 10.46 h -.636 V 9.231 h .635 c .375 0 .618 .162 .618 .618 s -.242 .612 -.618 .612 Z "
        + "M 16.815 11.286 h 1.004 l -.502 -1.772 -.502 1.772 Z "
        + "M 21.499 8.191 H 9.366 a .8 .8 0 0 0 -.8 .8 v 3.383 a .8 .8 0 0 0 .8 .8 h 12.132 a .8 .8 0 0 0 .8 -.8 V 8.992 a .8 .8 0 0 0 -.8 -.801 Z "
        + "M 10.553 12.168 c .525 0 .571 -.374 .589 -.617 .011 -.179 .173 -.236 .369 -.236 .26 0 .38 .075 .38 .369 0 .698 -.57 1.142 -1.379 1.142 -.727 0 -1.327 -.357 -1.327 -1.322 v -1.61 c 0 -.963 .606 -1.322 1.333 -1.322 .802 0 1.374 .427 1.374 1.097 0 .3 -.121 .37 -.375 .37 -.214 0 -.37 -.064 -.375 -.238 -.012 -.178 -.052 -.57 -.6 -.57 -.387 0 -.606 .213 -.606 .663 v 1.61 c 0 .45 .219 .664 .617 .664 Z "
        + "M 15.256 12.556 c 0 .156 -.19 .237 -.375 .237 s -.375 -.081 -.375 -.237 V 10.9 h -1.299 v 1.656 c 0 .156 -.19 .237 -.375 .237 s -.375 -.081 -.375 -.237 V 8.804 c 0 -.161 .185 -.23 .375 -.23 s .375 .069 .375 .23 v 1.507 h 1.299 V 8.804 c 0 -.161 .185 -.23 .375 -.23 s .375 .069 .375 .23 v 3.752 Z "
        + "M 18.454 12.792 c -.127 0 -.225 -.04 -.254 -.15 l -.22 -.768 h -1.322 l -.219 .768 c -.029 .11 -.127 .15 -.254 .15 -.202 0 -.473 -.127 -.473 -.311 0 -.012 .006 -.035 .012 -.058 l 1.114 -3.63 c .051 -.173 .265 -.254 .478 -.254 s .433 .08 .485 .254 l 1.114 3.63 c .006 .023 .012 .04 .012 .058 0 .179 -.272 .311 -.473 .311 Z "
        + "M 21.443 9.249 h -.843 v 3.306 c 0 .156 -.19 .237 -.375 .237 s -.375 -.081 -.375 -.237 V 9.25 h -.848 c -.15 0 -.237 -.157 -.237 -.34 0 -.162 .075 -.336 .237 -.336 h 2.44 c .162 0 .238 .173 .238 .335 0 .18 -.087 .34 -.237 .34 Z";

    private const string MicrophoneGlyph =
        "M12,3 C10.3,3 9,4.3 9,6 L9,11 C9,12.7 10.3,14 12,14 C13.7,14 15,12.7 15,11 L15,6 C15,4.3 13.7,3 12,3 Z "
        + "M6,11 C6,14.3 8.7,17 12,17 C15.3,17 18,14.3 18,11 M12,17 L12,21 M9,21 L15,21";

    private const string HeadphonesGlyph =
        "M4,15 L4,12 C4,7.6 7.6,4 12,4 C16.4,4 20,7.6 20,12 L20,15 "
        + "M4,14 L8,14 L8,20 L6,20 C4.9,20 4,19.1 4,18 Z M20,14 L16,14 L16,20 L18,20 C19.1,20 20,19.1 20,18 Z";

    /// <summary>
    /// Clip with the Talk icons on its row, each saying where things stand now and coloured for
    /// it: the swap icon in the accent colour on a side, the Mute and Deafen icons struck through in the
    /// warning colour while on. A failure, if there is one, is the one line under them.
    /// </summary>
    /// <remarks>
    /// <para>Inside a headset there is no settings screen and no notification, so the row is the only place
    /// a failed press can say what went wrong. The icons are drawn only while Talk in Discord or VRChat is
    /// on; either part is left out when it is off.</para>
    /// <para>Each icon carries the words it used to say as its automation name.</para>
    /// <para>Clip is only as wide as its icon and word, on its own at the left. The swap icon, Mute and
    /// Deafen are the audio group at the right, with a gap between the two groups. While the microphone is
    /// on VRChat's side Mute is VRChat's and Deafen is not drawn but keeps its place, so the row does not
    /// shift; VRChat has no volume to turn down.</para>
    /// </remarks>
    private Control ClipsAndTalk(ClipButton clips, TalkRow talk)
    {
        if (!talk.IsVisible)
            return SaveClipBar(clips);

        var inVRChat = talk.Side is TalkSide.VRChat;
        var swapGlyph = talk.Side is TalkSide.Discord ? DiscordGlyph : VRChatGlyph;

        var audio = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                TalkIconFor(talk.SwapCaption, TalkButton.Swap, swapGlyph, filled: true, active: true, struck: false, T.AccentForegroundBrush),
                inVRChat
                    ? TalkIconFor(talk.VRChatMuteCaption, TalkButton.VRChatMute, MicrophoneGlyph, filled: false, talk.VRChatMuted, struck: talk.VRChatMuted, T.DangerBrush)
                    : TalkIconFor(talk.MuteCaption, TalkButton.DiscordMute, MicrophoneGlyph, filled: false, talk.DiscordMuted, struck: talk.DiscordMuted, T.DangerBrush),
                TalkIconFor(talk.DeafenCaption, TalkButton.DiscordDeafen, HeadphonesGlyph, filled: false, talk.DiscordDeafened, struck: talk.DiscordDeafened, T.DangerBrush, hidden: inVRChat),
            },
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(clips.IsVisible ? "*,Auto" : "Auto") };

        if (clips.IsVisible)
        {
            var save = SaveClipBar(clips, shared: true);
            save.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(save);

            // The gap that keeps the two groups apart.
            audio.Margin = new Thickness(24, 0, 0, 0);
            Grid.SetColumn(audio, 1);
        }

        row.Children.Add(audio);

        if (talk.Problem is not { Length: > 0 } problem)
            return Backed(row, fill: true);

        var line = Text(problem, T.Density.TextTiny, T.DangerBrush);
        line.TextWrapping = TextWrapping.Wrap;
        line.MaxLines = 2;

        return Backed(new StackPanel { Spacing = 6, Children = { row, line } }, fill: true);
    }

    /// <param name="caption">What the button used to say: its automation name.</param>
    /// <param name="filled">The icon is a solid mark (the Discord or VRChat logo), not a line drawing.</param>
    /// <param name="struck">A line through the icon, for a switch that is on.</param>
    /// <param name="hidden">Not drawn and not a target, but it keeps the room it takes.</param>
    private Control TalkIconFor(string caption, TalkButton button, string glyph, bool filled, bool active, bool struck, IBrush activeBrush, bool hidden = false)
    {
        var brush = active ? activeBrush : T.TextBrush;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { TalkGlyph(glyph, filled, struck, brush, T.Surface2Brush) },
        };

        var border = new Border
        {
            Tag = hidden ? null : new OverlayTarget.Talk(button),
            Opacity = hidden ? 0 : 1,
            IsHitTestVisible = !hidden,
            Background = T.Surface2Brush,
            BorderBrush = active ? activeBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,

            // Small, but still a target a hand in a headset can land on without aiming.
            MinWidth = 56,
            MinHeight = T.Density.RowHeight,
            Padding = new Thickness(10, 6),
            Child = content,
        };

        if (!hidden)
            AutomationProperties.SetName(border, caption);

        return border;
    }

    /// <summary>An icon on a 24-unit square, with a line through it when <paramref name="struck"/>, cut out of the button's own colour so it reads as a gap.</summary>
    private static Control TalkGlyph(string data, bool filled, bool struck, IBrush brush, IBrush ground)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        canvas.Children.Add(filled ? FilledGlyphPath(data, brush) : GlyphPath(data, brush, 1.8));

        if (struck)
        {
            canvas.Children.Add(GlyphPath("M4.5,4.5 L19.5,19.5", ground, 4.6));
            canvas.Children.Add(GlyphPath("M4.5,4.5 L19.5,19.5", brush, 1.8));
        }

        return new Viewbox { Width = 26, Height = 26, Child = canvas };
    }

    /// <summary>The clapperboard on the Save a clip control, drawn like the Talk icons and in the caption's colour.</summary>
    private static Control ClapperIcon(IBrush brush) => TalkGlyph(ClapperGlyph, filled: false, struck: false, brush, brush);

    // "F1" is the non-zero fill rule, which Simple Icons' marks are drawn for.
    private static Avalonia.Controls.Shapes.Path FilledGlyphPath(string data, IBrush brush) => new()
    {
        Data = Geometry.Parse("F1 " + data),
        Fill = brush,
    };

    private static Avalonia.Controls.Shapes.Path GlyphPath(string data, IBrush brush, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
    };
}
