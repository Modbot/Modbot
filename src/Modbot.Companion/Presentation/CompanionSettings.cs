using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Clips;
using Modbot.Companion.Listening;
using Modbot.Companion.Overlay;
using Modbot.Companion.Pairing;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Presentation;

/// <summary>
/// What a moderator can change about the client itself: which page "Pair with a server" opens,
/// whether it checks for newer versions of itself, whether it starts with Windows, whether it
/// draws the headset panel, where its event backup to Modbot Cloud goes, and what its voice says.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> One file, <c>settings.json</c>, in Modbot's own
/// folder under your user profile — beside <c>pairings.json</c>. It is plain JSON with optional fields:
/// <c>pairingPage</c>, <c>checkForUpdates</c>, <c>startWithWindows</c>, <c>vrchatLogFolder</c>, <c>overlayOn</c>,
/// <c>overlay</c>, <c>cloud</c>
/// (<c>{ "endpoint": "…", "disabled": true }</c>), <c>voice</c>
/// (<c>{ "on": true, "joins": true, "leaves": true, "flaggedJoins": true, "volume": 80, "outputDevice": "…", "name": "Bella" }</c>)
/// and <c>eventsFilters</c> (the Events page's filter chips, one line each, such as <c>"kind:is:joined,left"</c>).
/// The notification overlay is its own object, <c>notifyOverlay</c>, kept apart from the main overlay's so
/// either can be changed without touching the other.
/// If it is missing or unreadable the defaults are
/// used. The client writes it only when a switch on the settings screen is changed, and then changes
/// only that switch's field — the whole <c>voice</c> object for the voice card — leaving anything
/// else in the file as it was. The <c>cloud</c> object is
/// never written by the client; the environment variables <c>MODBOT_CLOUD_ENDPOINT</c> and
/// <c>MODBOT_CLOUD_DISABLED</c> are also read, and win over it (<see cref="CloudSettings"/>).
/// There is also <c>notifications</c>
/// (<c>{ "bleep": true, "volume": 70, "trayNoticesShown": 0 }</c>), written whole the same way, and
/// <c>desktopOverlay</c> (<c>{ "on": true, "shortcut": "mod+alt+m", "opacity": 90 }</c>), the
/// window that sits over VRChat on a monitor, written whole the same way the voice is. There is
/// also <c>notificationFilters</c>
/// (<c>{ "popUp": ["flagged join"], "sound": ["flagged join"], "voice": ["joined", "left"] }</c>),
/// which kinds of event raise a notification by each of the three ways; and <c>clips</c>
/// (<c>{ "on": false, "minutes": 3, "folder": "…", "keepGigabytes": 5 }</c>), keeping the last few
/// minutes of the screen while VRChat runs — off unless somebody turns it on. Both are written
/// whole the same way too. There is also <c>desktopNotifyOverlay</c>
/// (<c>{ "on": true, "spot": "bottomright", "seconds": 6 }</c>), the notification overlay on a
/// monitor — on for a machine with no settings file yet, and off for one whose file was written
/// before this window existed; and <c>listenForPhrase</c> (<c>{ "on": false }</c>), whether the client listens for a
/// spoken phrase while VRChat runs — off unless somebody turns it on, with an optional
/// <c>"microphone"</c> naming which one to open. Both are written whole the same way.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The pairing page address is what the client
/// opens in your browser when you press the button; no server is told what it is. The switches decide
/// what the client does; they are not reported anywhere.</para>
/// <para><strong>Why it exists.</strong> The default page is the project's own, which forwards a
/// signed-in moderator to their group's server. A tester with only their own server points the
/// button at <c>https://their-server/pair</c> directly. The update switch is for a group whose policy
/// is to pin a version (M3 9.2). The event backup is on unless the person running the client turns it
/// off here or in the environment (cloud event backup spec 3.1).</para>
/// </remarks>
/// <param name="CheckForUpdates">
/// Whether an installed client asks the release feed for newer versions. On unless
/// <c>"checkForUpdates": false</c> is in the file.
/// </param>
/// <param name="StartWithWindows">
/// "Start Modbot Companion when my computer starts". On unless <c>"startWithWindows": false</c> is in the file,
/// and only acted on by an installed copy.
/// </param>
public sealed record CompanionSettings(Uri PairingPage, bool CheckForUpdates = true, bool StartWithWindows = true)
{
    /// <summary>
    /// VRChat's log folder, when the person running the companion has named one; null to look in
    /// the well-known places (<see cref="LogReading.VRChatLogFolders"/>). Saved from the settings
    /// screen as <c>vrchatLogFolder</c>.
    /// </summary>
    public string? VRChatLogFolder { get; init; }

    /// <summary>
    /// Where the event backup goes, and whether it is sent: the environment, then the file's
    /// <c>cloud</c> object, then on to <c>https://cloud.modbot.co</c>.
    /// </summary>
    public CloudSettings Cloud { get; init; } = CloudSettings.Default;

    /// <summary>The voice: off until turned on, then which events it speaks, how loud, and through what.</summary>
    public VoiceSettings Voice { get; init; } = VoiceSettings.Default;

    /// <summary>
    /// Which kinds of event raise a notification, and by which of the three ways: the pop-up
    /// overlay, the sound, and the voice. Saved as the <c>notificationFilters</c> object.
    /// </summary>
    public NotificationFilters NotificationFilters { get; init; } = NotificationFilters.Default;

    /// <summary>
    /// The desktop overlay: off until turned on, then the shortcut that brings it up over VRChat
    /// and how solid its background is. Saved as the <c>desktopOverlay</c> object.
    /// </summary>
    public DesktopOverlaySettings DesktopOverlay { get; init; } = DesktopOverlaySettings.Default;

    /// <summary>
    /// Whether the headset panel is drawn at all. On unless <c>"overlayOn": false</c> is in the
    /// file, which is what the SteamVR page's <strong>Overlay on</strong> switch writes.
    /// </summary>
    /// <remarks>
    /// Its own field rather than a member of the <c>overlay</c> object below, because that object
    /// is rewritten whole every time a controller moves the panel, and the switch must not be able
    /// to be lost in one of those writes.
    /// </remarks>
    public bool OverlayOn { get; init; } = true;

    /// <summary>
    /// Where the headset panel is and how big. Saved as the <c>overlay</c> object whenever a
    /// controller moves it or the settings page changes it, so it is where it was left.
    /// </summary>
    public OverlayPlacement Overlay { get; init; } = OverlayPlacement.Default;

    /// <summary>
    /// The notification overlay: whether it is drawn, where on the screen it sits, how big, and
    /// how long one pop-up stays. Saved as the <c>notifyOverlay</c> object.
    /// </summary>
    /// <remarks>
    /// Its switch lives inside the object, unlike the main overlay's, because nothing but the
    /// settings page ever writes this one — a controller cannot move it, so there is no write it
    /// could be lost in (two overlay modes design §5).
    /// </remarks>
    public NotifyOverlaySettings NotifyOverlay { get; init; } = NotifyOverlaySettings.Default;

    /// <summary>
    /// The Events page's filter chips, saved as <c>eventsFilters</c> whenever the bar changes,
    /// so the page opens the way it was left.
    /// </summary>
    public EventFilterSet EventsFilters { get; init; } = EventFilterSet.Empty;

    /// <summary>
    /// Being told things on this PC: the short sound, its own volume, and how many times the tray
    /// notice has been shown. Saved as the <c>notifications</c> object.
    /// </summary>
    public NotificationSettings Notifications { get; init; } = NotificationSettings.Default;

    /// <summary>
    /// Keeping the last few minutes of VRChat's window and its sound while VRChat runs: off until
    /// turned on, then how many minutes, whether Discord's sound goes in too, where saved clips go
    /// and how much room they may take. Saved as the <c>clips</c> object.
    /// </summary>
    /// <remarks>
    /// Off is the default and a missing object means off, so a client that is updated into a
    /// version that can record does not start recording. See the clips design spec, §2. Discord's
    /// sound is its own field and is off in the same way, and there is no field anywhere for the
    /// machine's own sound (§14).
    /// </remarks>
    public ClipSettings Clips { get; init; } = ClipSettings.Default;

    /// <summary>
    /// The notification overlay on a monitor: whether it is drawn, which corner of the screen it
    /// sits in and how long one notification stays. Saved as the <c>desktopNotifyOverlay</c>
    /// object, and on unless the file says otherwise or predates it
    /// (<see cref="DesktopNotifySettings.NotAskedFor"/>).
    /// </summary>
    public DesktopNotifySettings DesktopNotifyOverlay { get; init; } = DesktopNotifySettings.Default;

    /// <summary>
    /// Listening for a spoken phrase while VRChat runs, so a clip can be saved from inside a
    /// headset. Saved as the <c>listenForPhrase</c> object.
    /// </summary>
    /// <remarks>
    /// Off is the default and a missing object means off, so a client that is updated into a
    /// version that can listen does not open a microphone. See the listening design spec, §4.
    /// </remarks>
    public ListeningSettings Listening { get; init; } = ListeningSettings.Default;

    /// <summary>
    /// my.modbot.co's redirect route, pointed at <c>/pair</c>: it picks one of the moderator's saved
    /// servers and opens that server's own pairing page.
    /// </summary>
    public const string DefaultPairingPage = "https://my.modbot.co/go?redir=/pair";

    public const string StartWithWindowsField = "startWithWindows";

    public const string VRChatLogFolderField = "vrchatLogFolder";

    public const string OverlayOnField = "overlayOn";

    public const string OverlayField = "overlay";

    public const string NotifyOverlayField = "notifyOverlay";

    public const string VoiceField = "voice";

    public const string NotificationFiltersField = "notificationFilters";

    public const string DesktopOverlayField = "desktopOverlay";

    public const string EventsFiltersField = "eventsFilters";

    public const string NotificationsField = "notifications";

    public const string ClipsField = "clips";

    public const string DesktopNotifyOverlayField = "desktopNotifyOverlay";

    public const string ListeningField = "listenForPhrase";

    public static CompanionSettings Default { get; } = new(new Uri(DefaultPairingPage));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How the file is written: indented, and without the escaping the default encoder applies to
    /// characters that only matter inside a web page. A shortcut would otherwise be saved as
    /// <c>mod+alt+m</c>, and this file is meant to be one a moderator can open and edit.
    /// </summary>
    private static readonly JsonSerializerOptions Written = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The default location: <c>%APPDATA%\Modbot\settings.json</c>.</summary>
    public static string DefaultPath(string applicationData)
        => Path.Combine(applicationData, "Modbot", "settings.json");

    /// <summary>
    /// Reads the file and the two Cloud environment variables. A missing or unreadable file counts as
    /// empty. Never throws for a bad file: a typo in a settings file must not stop the client
    /// reporting.
    /// </summary>
    /// <param name="environment">Reads one environment variable. Null reads this process's own.</param>
    public static CompanionSettings Load(string path, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        var shape = ReadFile(path);
        var voice = FromShape(shape?.Voice);

        return FromPairingPage(shape?.PairingPage) with
        {
            CheckForUpdates = shape?.CheckForUpdates ?? true,
            StartWithWindows = shape?.StartWithWindows ?? true,
            VRChatLogFolder = string.IsNullOrWhiteSpace(shape?.VRChatLogFolder) ? null : shape.VRChatLogFolder.Trim(),
            Cloud = CloudSettings.Resolve(shape?.Cloud?.Endpoint, shape?.Cloud?.Disabled, environment),
            OverlayOn = shape?.OverlayOn ?? true,
            Overlay = OverlayPlacement.FromJson(shape?.Overlay),
            NotifyOverlay = NotifyOverlaySettings.FromJson(shape?.NotifyOverlay),
            Voice = voice,
            NotificationFilters = NotificationFilters.FromJson(shape?.NotificationFilters, voice),
            DesktopOverlay = FromShape(shape?.DesktopOverlay),
            EventsFilters = EventFilterSet.FromJson(shape?.EventsFilters),
            Notifications = FromShape(shape?.Notifications),
            Clips = FromShape(shape?.Clips),
            // The notification overlay on a monitor is on by default, and a default is only a
            // default on a first run. A machine that already has a settings file has been set up
            // by somebody, and a window appearing over whatever is on their screen at the next
            // update would be a change rather than a default, so a file that exists and says
            // nothing about this window means off.
            DesktopNotifyOverlay = DesktopNotifySettings.FromJson(
                shape?.DesktopNotifyOverlay,
                shape is null ? DesktopNotifySettings.Default : DesktopNotifySettings.NotAskedFor),
            Listening = FromShape(shape?.Listening),
        };
    }

    private static ListeningSettings FromShape(ListeningShape? listening) => listening is null
        ? ListeningSettings.Default
        : new ListeningSettings(
            listening.On ?? false,
            string.IsNullOrWhiteSpace(listening.Microphone) ? null : listening.Microphone.Trim());

    /// <summary>
    /// Writes the whole <c>listenForPhrase</c> object, keeping every other field in the file. The
    /// same rules as <see cref="SaveClips"/>: a file that cannot be read as JSON is left alone.
    /// </summary>
    public static bool SaveListening(string path, ListeningSettings listening)
    {
        ArgumentNullException.ThrowIfNull(listening);

        var shape = new JsonObject { ["on"] = listening.On };

        // Left out when it is the Windows default, the same rule the clips folder follows: the
        // file says nothing rather than pinning a device id that would then stop following the
        // machine.
        if (!string.IsNullOrWhiteSpace(listening.MicrophoneId))
            shape["microphone"] = listening.MicrophoneId;

        return SaveField(path, ListeningField, shape);
    }

    /// <summary>
    /// Writes the whole <c>desktopNotifyOverlay</c> object, keeping every other field in the file.
    /// The same rules as <see cref="SaveNotifyOverlay"/>: a file that cannot be read as JSON is
    /// left alone.
    /// </summary>
    public static bool SaveDesktopNotifyOverlay(string path, DesktopNotifySettings desktopNotifyOverlay)
    {
        ArgumentNullException.ThrowIfNull(desktopNotifyOverlay);
        return SaveField(path, DesktopNotifyOverlayField, desktopNotifyOverlay.ToJson());
    }

    private static ClipSettings FromShape(ClipsShape? clips) => clips is null
        ? ClipSettings.Default
        : new ClipSettings(
            clips.On ?? false,
            ClipSettings.ClampMinutes(clips.Minutes ?? ClipSettings.DefaultMinutes),
            string.IsNullOrWhiteSpace(clips.Folder) ? null : clips.Folder.Trim(),
            ClipSettings.ClampKeepGigabytes(clips.KeepGigabytes ?? ClipSettings.DefaultKeepGigabytes),
            clips.DiscordSound ?? false);

    /// <summary>
    /// Writes the whole <c>clips</c> object, keeping every other field in the file. The same rules
    /// as <see cref="SaveVoice"/>: a file that cannot be read as JSON is left alone. The folder is
    /// left out when it is the usual one, so the file says nothing rather than pinning a path that
    /// would then stop following the machine.
    /// </summary>
    public static bool SaveClips(string path, ClipSettings clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var shape = new JsonObject
        {
            ["on"] = clips.On,
            ["minutes"] = ClipSettings.ClampMinutes(clips.Minutes),
            ["keepGigabytes"] = ClipSettings.ClampKeepGigabytes(clips.KeepGigabytes),

            // Always written, false included, so a file somebody opens says plainly whether
            // Discord's sound is being recorded rather than leaving them to know the default.
            ["discordSound"] = clips.DiscordSound,
        };

        if (!string.IsNullOrWhiteSpace(clips.Folder))
            shape["folder"] = clips.Folder.Trim();

        return SaveField(path, ClipsField, shape);
    }

    /// <summary>
    /// Writes the Events page's chips as the <c>eventsFilters</c> array, keeping every other
    /// field. No chips removes the field, so the file says nothing rather than saying <c>[]</c>.
    /// </summary>
    public static bool SaveEventsFilters(string path, EventFilterSet filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return SaveField(path, EventsFiltersField, filters.ToJson());
    }

    /// <summary>Writes the panel's placement as the <c>overlay</c> object, keeping every other field.</summary>
    public static bool SaveOverlay(string path, OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return SaveField(path, OverlayField, placement.Clamped().ToJson());
    }

    /// <summary>
    /// Writes the whole <c>desktopOverlay</c> object, keeping every other field in the file. A
    /// shortcut nobody could register is not written: the file would then hold a combination the
    /// client silently ignores.
    /// </summary>
    public static bool SaveDesktopOverlay(string path, DesktopOverlaySettings desktopOverlay)
    {
        ArgumentNullException.ThrowIfNull(desktopOverlay);

        return SaveField(path, DesktopOverlayField, new JsonObject
        {
            ["on"] = desktopOverlay.On,
            ["shortcut"] = desktopOverlay.ShortcutOrDefault,
            ["opacity"] = DesktopOverlaySettings.ClampOpacity(desktopOverlay.Opacity),
        });
    }

    private static DesktopOverlaySettings FromShape(DesktopOverlayShape? desktopOverlay) => desktopOverlay is null
        ? DesktopOverlaySettings.Default
        : new DesktopOverlaySettings(
            desktopOverlay.On ?? false,
            string.IsNullOrWhiteSpace(desktopOverlay.Shortcut)
                ? DesktopOverlaySettings.DefaultShortcut
                : desktopOverlay.Shortcut.Trim(),
            DesktopOverlaySettings.ClampOpacity(desktopOverlay.Opacity ?? DesktopOverlaySettings.DefaultOpacity));

    /// <summary>
    /// Writes the whole <c>notifyOverlay</c> object, keeping every other field in the file. The
    /// same rules as <see cref="SaveSwitch"/>: a file that cannot be read as JSON is left alone.
    /// </summary>
    public static bool SaveNotifyOverlay(string path, NotifyOverlaySettings notifyOverlay)
    {
        ArgumentNullException.ThrowIfNull(notifyOverlay);
        return SaveField(path, NotifyOverlayField, notifyOverlay.ToJson());
    }

    private static VoiceSettings FromShape(VoiceShape? voice) => voice is null
        ? VoiceSettings.Default
        : new VoiceSettings(
            voice.On ?? false,
            voice.Joins ?? true,
            voice.Leaves ?? true,
            voice.FlaggedJoins ?? true,
            VoiceSettings.ClampVolume(voice.Volume ?? VoiceSettings.DefaultVolume),
            string.IsNullOrWhiteSpace(voice.OutputDevice) ? null : voice.OutputDevice.Trim(),
            string.IsNullOrWhiteSpace(voice.Name) ? VoiceModel.DefaultName : voice.Name.Trim());

    /// <summary>
    /// Writes the whole <c>voice</c> object, keeping every other field in the file. The same rules
    /// as <see cref="SaveSwitch"/>: a file that cannot be read as JSON is left alone.
    /// </summary>
    public static bool SaveVoice(string path, VoiceSettings voice)
    {
        ArgumentNullException.ThrowIfNull(voice);

        var shape = new JsonObject
        {
            ["on"] = voice.On,
            ["joins"] = voice.Joins,
            ["leaves"] = voice.Leaves,
            ["flaggedJoins"] = voice.FlaggedJoins,
            ["volume"] = VoiceSettings.ClampVolume(voice.Volume),
            ["name"] = string.IsNullOrWhiteSpace(voice.VoiceName) ? VoiceModel.DefaultName : voice.VoiceName.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(voice.OutputDeviceId))
            shape["outputDevice"] = voice.OutputDeviceId.Trim();

        return SaveField(path, VoiceField, shape);
    }

    /// <summary>
    /// Writes the whole <c>notificationFilters</c> object, keeping every other field in the file.
    /// The same rules as <see cref="SaveVoice"/>: a file that cannot be read as JSON is left alone.
    /// </summary>
    public static bool SaveNotificationFilters(string path, NotificationFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return SaveField(path, NotificationFiltersField, filters.ToJson());
    }

    private static NotificationSettings FromShape(NotificationsShape? notifications) => notifications is null
        ? NotificationSettings.Default
        : new NotificationSettings(
            notifications.Bleep ?? true,
            NotificationSettings.ClampVolume(notifications.Volume ?? NotificationSettings.DefaultVolume),
            Math.Clamp(notifications.TrayNoticesShown ?? 0, 0, NotificationSettings.TrayNoticesToShow),
            string.IsNullOrWhiteSpace(notifications.Sound) ? null : notifications.Sound.Trim());

    /// <summary>
    /// Writes the whole <c>notifications</c> object, keeping every other field in the file. The
    /// same rules as <see cref="SaveVoice"/>: a file that cannot be read as JSON is left alone.
    /// </summary>
    public static bool SaveNotifications(string path, NotificationSettings notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        var shape = new JsonObject
        {
            ["bleep"] = notifications.Bleep,
            ["volume"] = NotificationSettings.ClampVolume(notifications.Volume),
            ["trayNoticesShown"] = Math.Clamp(notifications.TrayNoticesShown, 0, NotificationSettings.TrayNoticesToShow),
        };

        // Left out when there is none, so the file says nothing rather than saying "" — the same
        // rule the clips folder follows.
        if (notifications.SoundOrNone is { } sound)
            shape["sound"] = sound;

        return SaveField(path, NotificationsField, shape);
    }

    private static FileShape? ReadFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes one switch to the file, keeping every other field in it. Returns false, and changes
    /// nothing, when the file cannot be read as JSON or cannot be written: a hand-edited file with a
    /// typo is not overwritten.
    /// </summary>
    public static bool SaveSwitch(string path, string field, bool value)
        => SaveField(path, field, JsonValue.Create(value));

    /// <summary>
    /// Writes one text field the same way. Blank removes the field, so the file says nothing
    /// rather than saying "".
    /// </summary>
    public static bool SaveText(string path, string field, string? value)
        => SaveField(path, field, string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value.Trim()));

    private static bool SaveField(string path, string field, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject existing)
                    return false;

                root = existing;
            }
            else
            {
                root = [];
            }

            if (value is null)
                root.Remove(field);
            else
                root[field] = value;

            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, root.ToJsonString(Written), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the same address rule as pairing itself: HTTPS, or plain HTTP only to this machine.
    /// A button that opened an insecure page would be handing the moderator's sign-in to whoever
    /// is on the network.
    /// </summary>
    public static CompanionSettings FromPairingPage(string? pairingPage)
    {
        if (string.IsNullOrWhiteSpace(pairingPage))
            return Default;

        return Uri.TryCreate(pairingPage.Trim(), UriKind.Absolute, out var page) && ServerAddresses.IsAllowed(page)
            ? new CompanionSettings(page)
            : Default;
    }

    private sealed record FileShape(
        [property: JsonPropertyName("pairingPage")] string? PairingPage,
        [property: JsonPropertyName("checkForUpdates")] bool? CheckForUpdates,
        [property: JsonPropertyName("startWithWindows")] bool? StartWithWindows,
        [property: JsonPropertyName("vrchatLogFolder")] string? VRChatLogFolder,
        [property: JsonPropertyName("cloud")] CloudShape? Cloud,
        [property: JsonPropertyName("overlayOn")] bool? OverlayOn,
        [property: JsonPropertyName("overlay")] JsonObject? Overlay,
        [property: JsonPropertyName("notifyOverlay")] JsonObject? NotifyOverlay,
        [property: JsonPropertyName("voice")] VoiceShape? Voice,
        [property: JsonPropertyName("notificationFilters")] JsonObject? NotificationFilters,
        [property: JsonPropertyName("desktopOverlay")] DesktopOverlayShape? DesktopOverlay,
        [property: JsonPropertyName("eventsFilters")] JsonArray? EventsFilters,
        [property: JsonPropertyName("notifications")] NotificationsShape? Notifications,
        [property: JsonPropertyName("clips")] ClipsShape? Clips,
        [property: JsonPropertyName("desktopNotifyOverlay")] JsonObject? DesktopNotifyOverlay,
        [property: JsonPropertyName("listenForPhrase")] ListeningShape? Listening);

    private sealed record CloudShape(
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("disabled")] bool? Disabled);

    private sealed record VoiceShape(
        [property: JsonPropertyName("on")] bool? On,
        [property: JsonPropertyName("joins")] bool? Joins,
        [property: JsonPropertyName("leaves")] bool? Leaves,
        [property: JsonPropertyName("flaggedJoins")] bool? FlaggedJoins,
        [property: JsonPropertyName("volume")] int? Volume,
        [property: JsonPropertyName("outputDevice")] string? OutputDevice,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record DesktopOverlayShape(
        [property: JsonPropertyName("on")] bool? On,
        [property: JsonPropertyName("shortcut")] string? Shortcut,
        [property: JsonPropertyName("opacity")] int? Opacity);

    private sealed record NotificationsShape(
        [property: JsonPropertyName("bleep")] bool? Bleep,
        [property: JsonPropertyName("volume")] int? Volume,
        [property: JsonPropertyName("trayNoticesShown")] int? TrayNoticesShown,
        [property: JsonPropertyName("sound")] string? Sound);

    private sealed record ClipsShape(
        [property: JsonPropertyName("on")] bool? On,
        [property: JsonPropertyName("minutes")] int? Minutes,
        [property: JsonPropertyName("folder")] string? Folder,
        [property: JsonPropertyName("keepGigabytes")] int? KeepGigabytes,
        [property: JsonPropertyName("discordSound")] bool? DiscordSound);

    private sealed record ListeningShape(
        [property: JsonPropertyName("on")] bool? On,
        [property: JsonPropertyName("microphone")] string? Microphone);
}
