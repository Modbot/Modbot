using System.Globalization;
using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Presentation;

/// <summary>
/// The notification overlay on a monitor: whether it is drawn, which corner of the screen it sits
/// in, and how long one notification stays.
/// </summary>
/// <remarks>
/// <para><strong>Why it is a separate thing from the overlay over VRChat.</strong> The window the
/// shortcut brings up is read when a moderator decides to look; this one is only ever there to tell
/// them something they were not looking for. One window could not be both, which is the same
/// argument that split the headset panel in two (two overlay modes design §1). So it has its own
/// switch, its own corner and its own lifetime, and neither can take the other down.</para>
/// <para><strong>It is the pop-up way of being told, on a monitor.</strong> The kinds of event that
/// raise a notification are the ones ticked in the Pop-up column of the Notifications card, the
/// same column the headset's notification overlay reads. One decision, two surfaces — a moderator
/// who asked to be shown a card when a flagged person arrives asked once, not once per screen they
/// happen to be wearing (desktop overlay design §7.3).</para>
/// <para><strong>The six spots are the headset's six.</strong> <see cref="ScreenSpot"/> is reused
/// rather than a second vocabulary invented, so "bottom right" means the same thing wherever a
/// moderator reads it.</para>
/// <para>Saved as the <c>desktopNotifyOverlay</c> object in <c>settings.json</c>
/// (<see cref="CompanionSettings.SaveDesktopNotifyOverlay"/>), which is rewritten whole and leaves
/// every other field in the file alone.</para>
/// </remarks>
/// <param name="On">Whether the window exists at all. Off means none of it is built.</param>
/// <param name="Spot">Which corner of the screen it sits in.</param>
/// <param name="Seconds">How long one notification stays before it clears itself.</param>
public sealed record DesktopNotifySettings(
    bool On = true,
    ScreenSpot Spot = ScreenSpot.BottomRight,
    float Seconds = DesktopNotifySettings.DefaultSeconds)
{
    public const float DefaultSeconds = 6f;

    public const float MinSeconds = NotifyOverlaySettings.MinSeconds;

    public const float MaxSeconds = NotifyOverlaySettings.MaxSeconds;

    /// <summary>How far in from the screen's edge the window sits.</summary>
    public const int EdgeMargin = 24;

    /// <summary>
    /// On, in the bottom right: what a machine with no settings file yet gets.
    /// </summary>
    /// <remarks>
    /// <para>This was off until 2026-09-19, on the reasoning that a window over everything else on
    /// the machine is a thing to be asked for. What that missed is that being told is the whole
    /// reason somebody installs this: a moderator who has not found the Notifications card gets no
    /// pop-up at all and has no way of knowing there was one to switch on. The headset's
    /// notification panel has been on by default since it was built, for the same reason, and
    /// having the two disagree was an accident rather than a decision.</para>
    /// <para>Which kinds of event actually raise one is still the Notifications card's Pop-up
    /// column, so "on" here is a window that exists, not a window that is constantly in the way.
    /// </para>
    /// </remarks>
    public static DesktopNotifySettings Default { get; } = new();

    /// <summary>
    /// Off, in the bottom right: what a settings file written before this switch existed gets.
    /// </summary>
    /// <remarks>
    /// <para><strong>A default is a default on a first run, and a change on every other one.</strong>
    /// Somebody who already has a <c>settings.json</c> has set this machine up, and a window
    /// appearing over whatever is on their monitor the next time they update is not a default —
    /// it is the client changing something while their back was turned. So a file that exists and
    /// says nothing about this window means off, and only a machine with no file at all takes the
    /// new default.</para>
    /// <para>The headset's notification panel is not treated the same way, and the asymmetry is
    /// the point: a panel a moderator can only see while they are wearing a headset interrupts
    /// nothing, and it has been on by default since it was built, so nothing about it moves.</para>
    /// <para>Switching it on or off writes the whole object, so after that this never applies
    /// again — the file says plainly which it is.</para>
    /// </remarks>
    public static DesktopNotifySettings NotAskedFor { get; } = Default with { On = false };

    /// <summary>The same settings with every number inside its bounds.</summary>
    public DesktopNotifySettings Clamped() => this with
    {
        Seconds = float.IsFinite(Seconds) ? Math.Clamp(Seconds, MinSeconds, MaxSeconds) : DefaultSeconds,
    };

    /// <summary>How long one notification stays.</summary>
    public TimeSpan Dwell => TimeSpan.FromSeconds(Clamped().Seconds);

    /// <summary>The spot in plain words, for the settings page.</summary>
    public static string Name(ScreenSpot spot) => NotifyOverlaySettings.Name(spot);

    /// <summary>
    /// Where the window goes on a screen, in that screen's own pixels.
    /// </summary>
    /// <remarks>
    /// <para>The work area rather than the whole screen, so the taskbar is not sat on. A left spot
    /// hugs the left edge, a right spot the right, and a middle spot is centred between them; a top
    /// spot hugs the top and a bottom spot the bottom. The margin is applied on every side, so the
    /// window never touches an edge.</para>
    /// <para>Plain numbers and no window, because which corner lands where is worth testing without
    /// a screen.</para>
    /// </remarks>
    /// <param name="spot">Which of the six.</param>
    /// <param name="areaX">The work area's left edge.</param>
    /// <param name="areaY">The work area's top edge.</param>
    /// <param name="areaWidth">How wide the work area is.</param>
    /// <param name="areaHeight">How tall the work area is.</param>
    /// <param name="width">How wide the window is.</param>
    /// <param name="height">How tall the window is.</param>
    /// <param name="margin">How far in from the edge.</param>
    public static (int X, int Y) Corner(
        ScreenSpot spot,
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        int width,
        int height,
        int margin = EdgeMargin)
    {
        var across = Math.Max(0, areaWidth - width - (margin * 2));
        var down = Math.Max(0, areaHeight - height - (margin * 2));

        var x = NotifyOverlaySettings.AcrossOf(spot) switch
        {
            < 0f => areaX + margin,
            > 0f => areaX + margin + across,
            _ => areaX + margin + (across / 2),
        };

        // Down is +1 for a top spot and -1 for a bottom one, the same as the headset's.
        var y = NotifyOverlaySettings.DownOf(spot) > 0f
            ? areaY + margin
            : areaY + margin + down;

        return (x, y);
    }

    /// <summary>The <c>desktopNotifyOverlay</c> object as it is written to the file.</summary>
    public JsonObject ToJson()
    {
        var settings = Clamped();

        return new JsonObject
        {
            ["on"] = settings.On,
            ["spot"] = settings.Spot.ToString().ToLowerInvariant(),
            ["seconds"] = settings.Seconds,
        };
    }

    /// <summary>
    /// Reads the <c>desktopNotifyOverlay</c> object. Anything missing or unreadable takes
    /// <paramref name="whenAbsent"/> for that field, and a number outside its bounds is brought
    /// inside them, so a hand-edited file cannot put the window somewhere it cannot be found.
    /// </summary>
    /// <param name="node">The object out of the settings file, or null when there is none.</param>
    /// <param name="whenAbsent">
    /// What a missing object means. <see cref="Default"/> on a machine with no settings file at
    /// all, and <see cref="NotAskedFor"/> on one that has a file written before this window
    /// existed — see the remarks on <see cref="NotAskedFor"/> for why those differ.
    /// </param>
    public static DesktopNotifySettings FromJson(JsonNode? node, DesktopNotifySettings? whenAbsent = null)
    {
        var absent = whenAbsent ?? Default;

        if (node is not JsonObject json)
            return absent;

        var spot = Enum.TryParse<ScreenSpot>(Text(json, "spot"), ignoreCase: true, out var parsed)
            ? parsed
            : absent.Spot;

        return new DesktopNotifySettings(
            Flag(json, "on", absent.On),
            spot,
            Number(json, "seconds", absent.Seconds)).Clamped();
    }

    private static string? Text(JsonObject json, string field)
        => json[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonObject json, string field, bool fallback)
        => json[field] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static float Number(JsonObject json, string field, float fallback)
    {
        if (json[field] is not JsonValue value)
            return fallback;

        if (value.TryGetValue<float>(out var number) && float.IsFinite(number))
            return number;

        if (value.TryGetValue<string>(out var text)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && float.IsFinite(number))
        {
            return number;
        }

        return fallback;
    }
}
