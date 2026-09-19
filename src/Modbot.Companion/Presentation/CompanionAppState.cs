using Modbot.Companion.Clips;
using Modbot.Companion.Credits;
using Modbot.Companion.Ingest;
using Modbot.Companion.Journal;
using Modbot.Companion.Overlay;
using Modbot.Companion.Pairing;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Sounds;
using Modbot.Companion.Startup;
using Modbot.Companion.Voice;
using Modbot.Core.Time;

namespace Modbot.Companion.Presentation;

/// <summary>One paired server, as the window shows it.</summary>
/// <param name="Detail">
/// A plain sentence for the moderator. Not a status code, not a stack trace: this window exists
/// for somebody deciding whether to keep trusting the program.
/// </param>
/// <param name="GroupName">The group's name, or its id until the server has said.</param>
/// <param name="GroupIconUrl">The group's icon, or null.</param>
/// <param name="Live">
/// The overlay's live link to this server, in one word: Off, Connecting, Live, Polling or Stopped.
/// </param>
public sealed record ServerRow(
    string ServerId,
    string Address,
    string ManagedGroupId,
    string GroupName,
    string? GroupIconUrl,
    ConnectionState State,
    bool IsPaused,
    int Pending,
    int AcceptedTotal,
    int DeduplicatedTotal,
    string Detail,
    string Live = "Off");

/// <summary>How loudly a warning should be shown.</summary>
public enum WarningSeverity
{
    /// <summary>Worth knowing. Nothing is broken.</summary>
    Info,

    /// <summary>Something needs attention before it costs history.</summary>
    Warning,

    /// <summary>
    /// Presence is not being recorded and will not start again on its own. This is the one worth
    /// interrupting somebody for: the gap it creates cannot be filled in later.
    /// </summary>
    Critical,
}

public sealed record CompanionWarning(WarningSeverity Severity, string Message);

/// <summary>Where the most recent pairing attempt got to.</summary>
public enum PairingNoticeKind
{
    /// <summary>A request is in flight. One, never more.</summary>
    Working,

    Succeeded,

    Failed,
}

/// <summary>
/// The last thing pairing had to say, whichever way the token arrived — pressed in a browser or
/// pasted into the window.
/// </summary>
/// <remarks>
/// Kept here rather than inside the window so a link that Windows hands to the running client
/// while the window is closed has somewhere to put its answer, and so the window can be rebuilt
/// on a timer without losing it.
/// </remarks>
public sealed record PairingNotice(PairingNoticeKind Kind, string Message);

/// <summary>The SteamVR overlay, as the window's SteamVR page shows it.</summary>
/// <remarks>
/// Plain words and counts from the overlay host, with none of its types: this library does not
/// know OpenVR, and the page needs only what a moderator can check against the headset.
/// </remarks>
/// <param name="Attached">Whether the panel is up in SteamVR right now.</param>
/// <param name="State">One short phrase: attached, SteamVR not running, SteamVR not installed, refused, not set up, off.</param>
/// <param name="Detail">The sentence under it, from the overlay runtime.</param>
/// <param name="Showing">The group the panel speaks for, or the idle screen's wording.</param>
/// <param name="People">How many people the roster lists.</param>
/// <param name="RosterAge">How fresh the roster is, in the overlay's own words.</param>
/// <param name="Alert">Who the alert card names, or null.</param>
/// <param name="Problem">The problem banner's text, or null.</param>
/// <param name="FollowingServer">The server the overlay reads from, or null when not in a group instance.</param>
/// <param name="PinnedSample">The sample screen the debug page has pinned over the live one, or null.</param>
/// <param name="On">
/// Whether the overlay is switched on. Off is the moderator's own choice on the SteamVR page:
/// nothing is drawn and no VR runtime is connected to, which is a different thing from being on
/// with no SteamVR running.
/// </param>
public sealed record OverlayStatus(
    bool Attached,
    string State,
    string Detail,
    DateTimeOffset? AttachedAt,
    int FramesDrawn,
    DateTimeOffset? LastDrawnAt,
    string Showing,
    int People,
    string RosterAge,
    string? Alert,
    string? Problem,
    string? FollowingServer,
    string? PinnedSample = null,
    OverlayPlacement? Placement = null,
    string? Holding = null,
    bool On = true)
{
    /// <summary>Where the panel is, never null: the default until the host has said.</summary>
    public OverlayPlacement PlacementOrDefault => Placement ?? OverlayPlacement.Default;

    /// <summary>Before the overlay exists, or when it could not be set up on this PC.</summary>
    public static OverlayStatus None { get; } = new(
        false, "not set up", "The overlay could not be set up on this PC.", null, 0, null,
        "Not in a group instance", 0, "not loaded", null, null, null);

    /// <summary>The overlay switched off on the SteamVR page: nothing is running to report on.</summary>
    public static OverlayStatus Off { get; } = new(
        false, "off", "", null, 0, null,
        "Not in a group instance", 0, "not loaded", null, null, null, On: false);
}

/// <summary>The notification overlay, for the SteamVR page.</summary>
/// <param name="Attached">Whether it is showing in a headset right now.</param>
/// <param name="State">The runtime's answer in one word.</param>
/// <param name="Detail">The runtime's answer in a sentence.</param>
/// <param name="FramesDrawn">How many pop-up frames have been drawn.</param>
/// <param name="PopUps">How many pop-ups are up right now.</param>
/// <param name="Settings">
/// The moderator's own choices, so the card can be filled in whether or not anything is running.
/// </param>
public sealed record NotifyOverlayStatus(
    bool Attached,
    string State,
    string Detail,
    DateTimeOffset? AttachedAt,
    int FramesDrawn,
    DateTimeOffset? LastDrawnAt,
    int PopUps,
    NotifyOverlaySettings? Settings = null)
{
    /// <summary>The moderator's choices, never null.</summary>
    public NotifyOverlaySettings SettingsOrDefault => Settings ?? NotifyOverlaySettings.Default;

    /// <summary>Whether the moderator wants it. The switch lives in the settings themselves.</summary>
    public bool On => SettingsOrDefault.On;

    /// <summary>Before the host exists, or when it could not be set up on this PC.</summary>
    public static NotifyOverlayStatus None { get; } = new(false, "not set up", "", null, 0, null, 0);
}

/// <summary>What the client window is showing right now.</summary>
/// <param name="Events">
/// Every event this client processed, newest first — one row each, whatever became of it at each
/// destination.
/// </param>
/// <param name="PairingPage">Where "Pair with a server" sends the browser. Shown so nobody has to guess.</param>
/// <param name="LogFolder">The folder being watched for VRChat's log.</param>
/// <param name="LogFolderConfigured">
/// The folder named in settings, or null when the companion is looking in the well-known places.
/// </param>
/// <param name="Overlay">The SteamVR overlay, for the SteamVR page.</param>
/// <param name="DebugMode">Whether the companion was started with <c>MODBOT_DEBUG_MODE=1</c>, which adds the Debug page.</param>
/// <param name="Voice">The voice, for the Settings page.</param>
/// <param name="EventsFilters">The Events page's chips as settings remember them; the window takes them once, when it first draws the page.</param>
/// <param name="Credits">The people the project thanks, for the Credits page.</param>
/// <param name="Notifications">The bleep and the tray notice, for the Settings page.</param>
public sealed record CompanionAppSnapshot(
    IReadOnlyList<ServerRow> Servers,
    IReadOnlyList<JournalRow> Events,
    LogHealthStatus LogStatus,
    string LogDetail,
    long LinesRead,
    long BehaviourLines,
    long RecognisedEvents,
    IReadOnlyList<CompanionWarning> Warnings,
    PairingNotice? LastPairing,
    string PairingPage,
    StartupState? Startup = null,
    string LogFolder = "",
    string? LogFolderConfigured = null,
    OverlayStatus? Overlay = null,
    bool DebugMode = false,
    VoiceStatus? Voice = null,
    EventFilterSet? EventsFilters = null,
    CreditsList? Credits = null,
    NotificationSettings? Notifications = null,
    DesktopOverlayStatus? DesktopOverlay = null,
    NotifyOverlayStatus? NotifyOverlay = null,
    ClipsStatus? Clips = null)
{
    /// <summary>The Clips card, never null: off with the default settings until the host has said.</summary>
    public ClipsStatus ClipsOrNone => Clips ?? ClipsStatus.None;

    /// <summary>The desktop overlay row, never null: <see cref="DesktopOverlayStatus.None"/> until the host has said.</summary>
    public DesktopOverlayStatus DesktopOverlayOrNone => DesktopOverlay ?? DesktopOverlayStatus.None;

    /// <summary>The overlay row, never null: <see cref="OverlayStatus.None"/> until the host has said.</summary>
    public OverlayStatus OverlayOrNone => Overlay ?? OverlayStatus.None;

    /// <summary>The notification overlay's row, never null.</summary>
    public NotifyOverlayStatus NotifyOverlayOrNone => NotifyOverlay ?? NotifyOverlayStatus.None;

    /// <summary>The voice, never null: <see cref="VoiceStatus.None"/> until the host has said.</summary>
    public VoiceStatus VoiceOrNone => Voice ?? VoiceStatus.None;

    /// <summary>The remembered chips, never null.</summary>
    public EventFilterSet EventsFiltersOrNone => EventsFilters ?? EventFilterSet.Empty;

    /// <summary>The three lists, never null: empty until Cloud has been read.</summary>
    public CreditsList CreditsOrNone => Credits ?? CreditsList.Empty;

    /// <summary>The bleep and the tray notice, never null: the defaults until settings have been read.</summary>
    public NotificationSettings NotificationsOrDefault => Notifications ?? NotificationSettings.Default;

    public static CompanionAppSnapshot Empty { get; } =
        new([], [], LogHealthStatus.Idle, "Starting up.", 0, 0, 0, [], null, CompanionSettings.DefaultPairingPage);
}

/// <summary>
/// Everything the window renders, with no Avalonia in it.
/// </summary>
/// <remarks>
/// <para>Separate from the window, and in the library rather than the executable, on purpose.
/// What the moderator is told about what this program does is the substance of the trust argument,
/// so the wording and the rules behind it are worth testing directly rather than only through a
/// rendered control — and the window itself is Windows-only, while this is not.</para>
/// <para><strong>It reads nothing and sends nothing.</strong> It reports counters the engine
/// already keeps and lines the journal already holds. There is no path from this type to the
/// network or to the disk.</para>
/// </remarks>
public sealed class CompanionAppState
{
    private readonly IModbotClock _clock;
    private readonly SentJournal _journal;

    /// <summary>
    /// How long without a recognised line before the log reader is called unhealthy.
    /// </summary>
    /// <remarks>
    /// A log parser that silently stops matching is the worst outcome available: presence history
    /// stops accruing, nobody notices for weeks, and the gap cannot be filled in later. Long enough not
    /// to fire while somebody sits alone in a quiet instance; short enough to catch a format
    /// change within one session.
    /// </remarks>
    public static readonly TimeSpan LogSilenceThreshold = TimeSpan.FromMinutes(10);

    public CompanionAppState(IModbotClock clock, SentJournal journal, CompanionSettings? settings = null)
    {
        _clock = clock;
        _journal = journal;
        Settings = settings ?? CompanionSettings.Default;
        Connections = [];
        UnusablePairings = [];
    }

    public CompanionSettings Settings { get; set; }

    /// <summary>The folder the log reader is watching right now.</summary>
    public string LogFolder { get; set; } = string.Empty;

    /// <summary>How the start-with-Windows switch should look; hidden unless this copy is installed.</summary>
    public StartupState Startup { get; set; } = StartupState.Hidden;

    /// <summary>The SteamVR overlay as of the last render; set by the host that owns it.</summary>
    public OverlayStatus Overlay { get; set; } = OverlayStatus.None;

    /// <summary>The notification overlay; set by the host that owns it.</summary>
    public NotifyOverlayStatus NotifyOverlay { get; set; } = NotifyOverlayStatus.None;

    /// <summary>Each server's live link in one word, by server id; set by the host that owns the overlay.</summary>
    public IReadOnlyDictionary<string, string> LiveWords { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The voice as of the last render; set by the host that owns it.</summary>
    public VoiceStatus Voice { get; set; } = VoiceStatus.None;

    /// <summary>The desktop overlay as of the last render; set by the host that owns its window.</summary>
    public DesktopOverlayStatus DesktopOverlay { get; set; } = DesktopOverlayStatus.None;

    /// <summary>
    /// The people the project thanks, as of the last read; set by the host that owns the reader.
    /// Empty until Modbot Cloud has answered, and empty for good when this PC has Cloud turned off.
    /// </summary>
    public CreditsList Credits { get; set; } = CreditsList.Empty;

    /// <summary>
    /// Keeping the last few minutes of the screen, as of the last render; set by the host that owns
    /// the recorder. Off until it says otherwise, which is also what a client that cannot record
    /// shows.
    /// </summary>
    public ClipsStatus Clips { get; set; } = ClipsStatus.None;

    /// <summary>Started with <c>MODBOT_DEBUG_MODE=1</c>: the window gets a Debug page.</summary>
    public bool DebugMode { get; set; }

    public List<ServerConnection> Connections { get; }

    /// <summary>The last pairing attempt's outcome, or null when there has not been one this run.</summary>
    public PairingNotice? LastPairing { get; set; }

    /// <summary>
    /// Pairings that loaded but cannot be used — almost always a token encrypted for a different
    /// Windows account. Shown by name so the moderator knows which server to re-pair, rather than
    /// being told that something, somewhere, is wrong.
    /// </summary>
    public List<LoadedPairing> UnusablePairings { get; }

    public LogHealth LogHealth { get; set; } = LogHealth.Empty;

    /// <summary>
    /// Why the last attempt to read VRChat's log failed, in one line, or null while reading works.
    /// The full story is in the client's own log file; this is the pointer to it.
    /// </summary>
    public string? ReadingFault { get; set; }

    /// <summary>
    /// The version of Modbot that has been downloaded and is waiting to be installed the next
    /// time the client starts, or null when there is none. Set by the updater; shown as a line
    /// in the window, never as a restart.
    /// </summary>
    public string? UpdateReady { get; set; }

    public CompanionAppSnapshot Snapshot()
    {
        var logStatus = LogHealth.Evaluate(_clock.UtcNow, LogSilenceThreshold);

        return new CompanionAppSnapshot(
            [.. Connections.Select(Describe)],
            _journal.Events(200),
            logStatus,
            DescribeLog(logStatus),
            LogHealth.LinesRead,
            LogHealth.BehaviourLines,
            LogHealth.RecognisedEvents,
            [.. Warnings(logStatus)],
            LastPairing,
            Settings.PairingPage.ToString(),
            Startup,
            LogFolder,
            Settings.VRChatLogFolder,
            Overlay,
            DebugMode,
            Voice,
            Settings.EventsFilters,
            Credits,
            Settings.Notifications,
            DesktopOverlay,
            NotifyOverlay with { Settings = Settings.NotifyOverlay },
            Clips with { Settings = Settings.Clips });
    }

    private IEnumerable<CompanionWarning> Warnings(LogHealthStatus logStatus)
    {
        if (ReadingFault is { } fault)
        {
            yield return new CompanionWarning(
                WarningSeverity.Critical,
                $"Reading VRChat's log failed: {fault}. Nothing is being recorded until this is fixed. "
                + "The details are in the client's log file under %APPDATA%\\Modbot\\logs.");
        }

        if (logStatus is LogHealthStatus.NotUnderstood)
        {
            // Critical, because it is the silent-failure case: nothing errors, presence simply
            // stops accruing, and the history lost while nobody noticed cannot be recovered.
            yield return new CompanionWarning(
                WarningSeverity.Critical,
                "VRChat is running and writing to its log, but Modbot has not recognised anything "
                + "in it recently. Either VRChat was started without its verbose logging flags, or "
                + "its log format has changed and this client needs updating. Nothing is being "
                + "recorded until this is fixed.");
        }

        foreach (var unusable in UnusablePairings)
        {
            yield return new CompanionWarning(
                WarningSeverity.Critical,
                unusable.Fault switch
                {
                    PairingFault.TokenUndecryptable =>
                        $"The saved credential for “{unusable.ServerId}” cannot be decrypted on this "
                        + "account. That is what happens when the settings file is copied "
                        + "between accounts or machines. Pair this server again.",
                    _ => $"The saved settings for “{unusable.ServerId}” could not be read. Pair this server again.",
                });
        }

        foreach (var stopped in Connections.Where(c => c.State is ConnectionState.Stopped))
        {
            yield return new CompanionWarning(
                WarningSeverity.Critical,
                $"“{stopped.ServerId}” rejected this device. Reporting to it has stopped and will "
                + "not restart on its own. If you were removed from that group's staff, this is "
                + "expected and there is nothing to fix.");
        }

        foreach (var stale in Connections.Where(c => c.State is ConnectionState.NeedsRenegotiation))
        {
            yield return new CompanionWarning(
                WarningSeverity.Warning,
                $"“{stale.ServerId}” has been upgraded past what this client can speak to. Update "
                + "Modbot's client; observations are still being queued in the meantime.");
        }

        // Counted rather than silent. "We quietly lost some" is precisely the failure this
        // subsystem must not have, and the buffer forgets on purpose when it fills or ages out.
        foreach (var connection in Connections.Where(c => c.MalformedBatches > 0))
        {
            yield return new CompanionWarning(
                WarningSeverity.Warning,
                $"“{connection.ServerId}” refused {connection.MalformedBatches:N0} batch(es) as "
                + "malformed and they were dropped rather than retried. That is a bug worth "
                + "reporting.");
        }

        // Informational, and it stays informational: an update is never installed under a
        // running client, so the moderator chooses the moment by quitting and reopening.
        if (UpdateReady is { } update)
        {
            yield return new CompanionWarning(
                WarningSeverity.Info,
                $"Modbot {update} has been downloaded and will be installed the next time Modbot "
                + "starts. Quit from the tray icon and open Modbot again whenever suits you; "
                + "nothing changes while it is running, and your pairings are kept.");
        }
    }

    private string DescribeLog(LogHealthStatus status) => status switch
    {
        LogHealthStatus.Idle => "VRChat is not running, so there is nothing to read.",
        LogHealthStatus.Healthy =>
            $"Reading VRChat's log: {LogHealth.LinesRead:N0} lines seen, "
            + $"{LogHealth.BehaviourLines:N0} of them the kind Modbot looks at, "
            + $"{LogHealth.RecognisedEvents:N0} recognised.",
        _ => "VRChat is writing to its log and Modbot no longer recognises any of it.",
    };

    private ServerRow Describe(ServerConnection connection) => new(
        connection.ServerId,
        connection.Pairing.BaseUri.ToString(),
        connection.ManagedGroupId,
        connection.Pairing.GroupLabel,
        connection.Pairing.ManagedGroupIconUrl,
        connection.State,
        connection.IsPaused,
        connection.Pending,
        connection.AcceptedTotal,
        connection.DeduplicatedTotal,
        Detail(connection),
        LiveWords.GetValueOrDefault(connection.ServerId, "Off"));

    /// <summary>
    /// The sentence under each server.
    /// </summary>
    /// <remarks>
    /// The distinction this exists to make legible is "buffering, will send later" against
    /// "stopped, will never send". On a screen that is not moving those look identical and they
    /// mean opposite things: one needs nothing doing, and the other means this moderator's
    /// coverage has quietly ended.
    /// </remarks>
    private static string Detail(ServerConnection connection) => connection.State switch
    {
        ConnectionState.Paused =>
            "Paused. Nothing about what you do is being captured or sent to this server. Pausing "
            + "stops Modbot reporting what you do from now on; it does not save it up to report "
            + "later. Anything queued before you paused is still owed to this server and goes out "
            + "when you resume.",

        ConnectionState.Stopped =>
            "Stopped. This server rejected the device token, so nothing more will be sent and "
            + "nothing is being queued for it.",

        ConnectionState.NeedsRenegotiation =>
            "This server has been upgraded past what this client speaks. Observations are still "
            + "being queued, and will be sent once the client is updated.",

        ConnectionState.Waiting =>
            connection.Pending == 0
                ? "Could not reach this server on the last attempt. Waiting to retry."
                : $"Could not reach this server on the last attempt. Waiting to retry; "
                  + $"{connection.Pending:N0} observations are queued and none are lost.",

        _ => connection.Pending == 0
            ? "Up to date. Everything observed for this group has been reported."
            : $"{connection.Pending:N0} observations queued to send.",
    };
}
