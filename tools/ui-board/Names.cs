namespace Modbot.UiBoard;

/// <summary>Where a picture is: the companion app's own windows, the headset (SteamVR), or VRChat's desktop window.</summary>
internal enum Side
{
    Companion,
    Vr,
    Desktop,
}

/// <summary>One picture on the board. <paramref name="Id"/> is its file name (without .png); <paramref name="Look"/> is empty or the look it is drawn in.</summary>
internal sealed record Tile(string Id, string Name, Side Side, string Look = "");

/// <summary>One surface on the board: its pictures left to right (VR first, then desktop).</summary>
internal sealed record Row(string Name, IReadOnlyList<Tile> Tiles);

/// <summary>A band of rows with a heading.</summary>
internal sealed record Group(string Name, IReadOnlyList<Row> Rows);

/// <summary>
/// The one list of what the board shows and what each thing is called. A rename is an edit here and
/// nowhere else: the board page, the manifest and the file names all read it. What draws each tile is
/// in <c>Surfaces*.cs</c>, keyed by the tile's <see cref="Tile.Id"/>.
/// </summary>
internal static class Names
{
    // The plain names, as the user says them.

    /// <summary>"The overlay": the main panel with the Instance list and the Audit Log.</summary>
    public const string Overlay = "Overlay";

    /// <summary>The small Hide / Show panel (with the Dictate button).</summary>
    public const string ButtonPanel = "Button panel";

    /// <summary>The pop-ups.</summary>
    public const string Notifications = "Notifications";

    /// <summary>The chat pop-up.</summary>
    public const string ChatPopUp = "Chat pop-up";

    public const string ModbotLook = "Modbot look";

    public const string VRChatLook = "VRChat look";

    public const string VrAndDesktop = "VR and desktop";

    /// <summary>A surface drawn in the headset's two looks, so the pair can be compared with the desktop's.</summary>
    private static Row Vr(string id, string name)
        => new(name,
        [
            new Tile(id + "-vr", name, Side.Vr, ModbotLook),
            new Tile(id + "-vr-vrchat", name, Side.Vr, VRChatLook),
        ]);

    public static IReadOnlyList<Group> Groups { get; } =
    [
        new Group(VrAndDesktop,
        [
            Vr("overlay-instance", Overlay + ": Instance list"),
            Vr("overlay-auditlog", Overlay + ": Audit Log"),
            Vr("overlay-choices", Overlay + ": Instance list, Rank choices open"),
            Vr("button-hide", ButtonPanel + ": Hide"),
            Vr("button-show", ButtonPanel + ": Show"),
            Vr("button-dictate-idle", ButtonPanel + ": Dictate, ready"),
            Vr("button-dictate-starting", ButtonPanel + ": Dictate, starting"),
            Vr("button-dictate-listening", ButtonPanel + ": Dictate, listening"),
            Vr("button-dictate-writing", ButtonPanel + ": Dictate, writing"),
            Vr("button-dictate-sending", ButtonPanel + ": Dictate, sending"),
            Vr("button-dictate-getting-ready", ButtonPanel + ": Dictate, getting ready"),
            Vr("button-dictate-notice", ButtonPanel + ": Dictate, short notice"),
            Vr("notifications", Notifications),
            Vr("chat-empty", ChatPopUp + ": no messages"),
            Vr("chat-conversation", ChatPopUp + ": conversation"),
            Vr("chat-error", ChatPopUp + ": answer failed"),
        ]),
    ];

    public static IEnumerable<Tile> Tiles => Groups.SelectMany(g => g.Rows).SelectMany(r => r.Tiles);
}
