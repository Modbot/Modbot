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

/// <summary>One surface on the board: its pictures left to right (VR first, then desktop; the Modbot look, then VRChat's).</summary>
internal sealed record Row(string Name, IReadOnlyList<Tile> Tiles);

/// <summary>What a VR picture is a picture of; <see cref="Sizes"/> turns it into the panel's size in the headset.</summary>
internal enum Panel
{
    /// <summary>The main panel: the Instance list and the Audit Log.</summary>
    Main,

    /// <summary>The small Hide / Show panel.</summary>
    Button,

    /// <summary>The pop-ups' panel.</summary>
    Notifications,

    /// <summary>The chat panel.</summary>
    Chat,

    /// <summary>Not a headset panel: a window, shown at its own pixel size.</summary>
    Window,
}

/// <summary>One tab of the board: a page of rows. <paramref name="DefaultPercent"/> is how big the size slider starts, picked so the tab fits one screen.</summary>
internal sealed record Tab(string Name, Panel Panel, int DefaultPercent, IReadOnlyList<Row> Rows);

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

    /// <summary>The tab with the chat pop-up's pictures.</summary>
    public const string Chat = "Chat";

    public const string ModbotLook = "Modbot look";

    public const string VRChatLook = "VRChat look";

    public const string CompanionApp = "Companion app";

    /// <summary>A surface drawn in the headset's two looks, so the pair can be compared with the desktop's.</summary>
    private static Row Vr(string id, string name)
        => new(name,
        [
            new Tile(id + "-vr", name, Side.Vr, ModbotLook),
            new Tile(id + "-vr-vrchat", name, Side.Vr, VRChatLook),
        ]);

    /// <summary>A surface in the headset and in VRChat's window, each in the two looks: the four side by side in one row.</summary>
    private static Row Both(string id, string name)
        => new(name,
        [
            new Tile(id + "-vr", name, Side.Vr, ModbotLook),
            new Tile(id + "-desktop", name, Side.Desktop, ModbotLook),
            new Tile(id + "-vr-vrchat", name, Side.Vr, VRChatLook),
            new Tile(id + "-desktop-vrchat", name, Side.Desktop, VRChatLook),
        ]);

    /// <summary>One card of the companion's Settings page: the heading it carries in the window (how the tool finds it) and its tile.</summary>
    internal sealed record SettingsCard(string Title, string Id, string Name);

    /// <summary>The Settings page's cards, top to bottom.</summary>
    public static IReadOnlyList<SettingsCard> SettingsCards { get; } =
    [
        Card("Settings", "general", "Start with Windows and Close to tray"),
        Card("Desktop overlay", "desktop-overlay"),
        Card("Notification overlay", "notification-overlay"),
        Card("Notifications", "notifications"),
        Card("Tell me about", "tell-me-about"),
        Card("Voice", "voice"),
        Card("Clips", "clips"),
        Card("Listening", "listening"),
        Card("Talk in Discord or VRChat", "talk"),
        Card("VRChat log folder", "log-folder"),
        Card("Restart", "restart"),
    ];

    private static SettingsCard Card(string title, string id, string? name = null)
        => new(title, "companion-settings-" + id, "Settings: " + (name ?? title));

    private static Row One(string id, string name)
        => new(name, [new Tile(id, name, Side.Companion)]);

    /// <summary>The tabs across the top of the board, left to right.</summary>
    public static IReadOnlyList<Tab> Tabs { get; } =
    [
        new Tab(Overlay, Panel.Main, 60,
        [
            Both("overlay-instance", Overlay + ": Instance list"),
            Both("overlay-auditlog", Overlay + ": Audit Log"),
            Both("overlay-choices", Overlay + ": Instance list, Rank choices open"),
        ]),

        new Tab(ButtonPanel, Panel.Button, 70,
        [
            // The Hide / Show button panel exists in the headset only.
            Vr("button-hide", ButtonPanel + ": Hide"),
            Vr("button-show", ButtonPanel + ": Show"),

            // The desktop's Dictate button sits on the overlay's top strip, so its tiles are that strip.
            Both("button-dictate-idle", "Dictate button: ready"),
            Both("button-dictate-starting", "Dictate button: starting"),
            Both("button-dictate-listening", "Dictate button: listening"),
            Both("button-dictate-writing", "Dictate button: writing"),
            Both("button-dictate-sending", "Dictate button: sending"),
            Both("button-dictate-getting-ready", "Dictate button: getting ready"),
            Both("button-dictate-notice", "Dictate button: short notice"),
        ]),

        new Tab(Notifications, Panel.Notifications, 150,
        [
            Both("notifications", Notifications),
        ]),

        new Tab(Chat, Panel.Chat, 60,
        [
            Both("chat-empty", ChatPopUp + ": no messages"),
            Both("chat-conversation", ChatPopUp + ": conversation"),
            Both("chat-error", ChatPopUp + ": answer failed"),
        ]),

        // The companion app's own window: its pages as it opens, then each Settings card on its own.
        new Tab(CompanionApp, Panel.Window, 40,
        [
            One("companion-servers", "Servers"),
            One("companion-add-server", "Servers: Add a server"),
            One("companion-audit-log", "Audit Log"),
            One("companion-steamvr", "SteamVR"),
            One("companion-log", "Log"),
            One("companion-credits", "Credits"),
            .. SettingsCards.Select(card => One(card.Id, card.Name)),
        ]),
    ];

    public static IEnumerable<Tile> Tiles => Tabs.SelectMany(t => t.Rows).SelectMany(r => r.Tiles);
}
