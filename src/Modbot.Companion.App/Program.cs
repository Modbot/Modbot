using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Clips;
using Modbot.Companion.Credits;
using Modbot.Companion.Listening;
using Modbot.Companion.App.Listening;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;
using Modbot.Companion.Pairing;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Companion.Startup;
using Modbot.Companion.Overlay;
using Modbot.Companion.Time;
using Modbot.Companion.Voice;
using Modbot.Companion.App.Voice;
using Modbot.Core;
using Modbot.Core.Time;
using Modbot.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Views;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The tray application.
/// </summary>
/// <remarks>
/// <para><strong>What this program reads.</strong> One folder: VRChat's own log directory, and
/// within it only the files VRChat names <c>output_log_*.txt</c>. It opens them for reading,
/// shared, and never writes to them. Anybody suspicious can open the same file in Notepad and see
/// exactly what Modbot is looking at.</para>
/// <para><strong>What it writes to your disk.</strong> Its own folder under your user profile,
/// holding three things: which servers you paired with and their tokens (the tokens encrypted to
/// your Windows account), observations queued to send, and the plain-English record of what has
/// been sent — and, once you turn the voice on, the downloaded voice under <c>voices</c>; once
/// you turn Clips on, two rolling recordings under <c>clips</c> that are deleted as they are
/// replaced and when recording stops; and, once you turn Listening on, the small phrase model under
/// <c>phrases</c>. No sound from your microphone is ever written anywhere. Plus
/// one registry key under your own account saying that <c>modbot-companion://</c>
/// links open this program, which is how pairing from the browser reaches it, and — in an installed
/// copy, unless you turn it off — one value under your own account's startup list so Modbot starts
/// in the tray when you sign in. Nothing else on the machine is touched.</para>
/// <para><strong>What leaves the machine.</strong> Presence observations, to the Modbot servers
/// you paired with — and only for instances belonging to the group each of those servers manages;
/// a server is never sent a raw log line or anything about your private, friends-only or public
/// VRChat use. Separately, and whether or not anything is paired, the same presence events — for
/// every instance, private ones included, but never a raw log line — are backed up to Modbot Cloud,
/// unless you turn that off in <c>settings.json</c> or with <c>MODBOT_CLOUD_DISABLED</c> (see
/// <c>CloudEventBackup</c> and <c>CloudSettings</c>). And once, if you turn the voice on: one
/// download of the voice from GitHub, with nothing attached (see <c>VoiceDownload</c>); what the
/// voice then says is made and played on this PC and goes nowhere. And once, if you turn Listening
/// on: one download of the phrase model from GitHub, with nothing attached (see
/// <c>PhraseDownload</c>).
/// Never chat, never a recorded clip or any other picture of your screen, never a recording of
/// anything your microphone heard, never keystrokes, never your friends list and never a list of
/// your processes.</para>
/// <para><strong>It never reads the keyboard.</strong> Turning the desktop overlay on asks Windows
/// for exactly one keyboard combination, by name, so that panel can be brought up while VRChat has
/// the keyboard (<c>DesktopOverlayShortcut</c>). Windows then sends one message when those keys are
/// pressed and says nothing about any other key. There is no keyboard hook here and there will not
/// be one; <c>CompanionSourceGuardTests</c> fails the build if one appears.</para>
/// <para><strong>It can record VRChat's window, and only when you switch that on.</strong> Until
/// 2026-09-19 this paragraph said the program never captured a screen by any route. That is no
/// longer true, and the honest replacement is this. The Settings page has a <strong>Clips</strong>
/// switch. It is <strong>off</strong> in a fresh install and off in an updated one, and while it is
/// off nothing is captured and no recorder is even built.
/// <list type="bullet">
/// <item><description><strong>What is recorded.</strong> <strong>VRChat's window</strong>, not your
/// monitor — this program asks Windows for VRChat's own window by name and records the part of the
/// screen it is drawn in, shrunk to at most 1280 pixels wide, at 15 frames a second. Whatever is
/// drawn on top of VRChat while you are in it — a chat program's in-game overlay, a notification,
/// Modbot's own panel — is inside that rectangle and is in the clip; nothing outside it ever is, and
/// while you are working in another program the last picture of VRChat is written again rather than
/// what you moved to. <strong>No sound at all</strong> — the ban on every microphone, line-in and
/// loopback API stands untouched, so voice chat is still never recorded. Not the keyboard, not the
/// clipboard, not a list of the programs you are running or of their windows, and nothing read out
/// of VRChat's screenshot folder or any other folder.</description></item>
/// <item><description><strong>When.</strong> Only while VRChat is running, which this program knows
/// because lines are arriving in VRChat's own log — never by looking for a running program. VRChat
/// closing stops the recording and deletes what was kept.</description></item>
/// <item><description><strong>Where it goes.</strong> Two files under <c>%APPDATA%\Modbot\clips</c>,
/// each holding at most the two to five minutes you chose, each replaced by a fresh one as it fills,
/// and both deleted when recording stops or this program quits. Pressing <strong>Save a clip</strong>
/// moves the older of the two into your Videos folder under <c>Modbot Clips</c>, or wherever you
/// pointed it.</description></item>
/// <item><description><strong>What leaves the machine: nothing.</strong> No clip, no frame, and no
/// fact that a clip exists is sent to a paired server, to Modbot Cloud, or anywhere else. There is
/// no upload path in this program and it did not gain one. Attaching a clip to a moderation case is
/// still a deliberate human action taken in Modbot's web interface, in a browser, by choosing a
/// file.</description></item>
/// </list>
/// One file — <c>ScreenRecording.cs</c> — is allowed to record, one file — <c>ClipsFolder.cs</c> —
/// is allowed to name your Videos folder, and <c>CompanionSourceGuardTests</c> fails the build if
/// any other file the client ships learns either trick.</para>
/// <para><strong>It can listen for one phrase, and only when you switch that on.</strong> Until
/// 2026-09-19 this program could not open a microphone at all, and the build failed if any code
/// that could appeared in it. That ban is now narrowed by exactly one file, because a moderator
/// wearing a headset cannot reach a keyboard and asked to be able to say <strong>"Modbot, clip
/// that"</strong> instead. The Settings page has a <strong>Listening</strong> switch. It is
/// <strong>off</strong> in a fresh install and off in an updated one, and while it is off no
/// microphone is opened and nothing is asked of Windows' audio system at all.
/// <list type="bullet">
/// <item><description><strong>Nothing is recorded, kept or sent.</strong> Sound arrives in
/// fractions of a second, is checked against four short phrases, and is thrown away. It is never
/// written to a file, never held for more than a moment, and never sent anywhere — there is no
/// upload path in this program and it did not gain one. This is not a transcriber: the thing doing
/// the checking is a three-megabyte phrase matcher that can only answer "was one of those four
/// things just said".</description></item>
/// <item><description><strong>The microphone is shared, never taken.</strong> Windows is asked for
/// the microphone in shared mode — the same way VRChat and Discord ask for it — so VRChat keeps
/// working exactly as it did. Exclusive mode, the mode that would lock other programs out, is
/// never asked for.</description></item>
/// <item><description><strong>Which microphone is yours to pick.</strong> While the switch is on,
/// this program asks Windows which microphones your PC has so the Settings page can list them, and
/// opens the one you picked. Picking nothing means whichever Windows calls the default, followed
/// wherever Windows moves it, and one you picked that is not plugged in falls back to the default
/// rather than going quiet. The one file allowed to open a microphone is the only file allowed to
/// ask for that list, and the build fails if a second one learns to.</description></item>
/// <item><description><strong>When.</strong> Only while VRChat is running, the same rule the
/// recorder uses, so the microphone is not open whenever this program is. VRChat closing closes it.
/// While it is open this program says so at the top of every page of its own window.</description></item>
/// <item><description><strong>What it does.</strong> Saves a clip, the same as pressing
/// <strong>Save a clip</strong>, and says out loud whether that worked. If Clips is off or nothing
/// is being recorded, it says that instead of appearing to work.</description></item>
/// </list>
/// One file — <c>PhraseListening.cs</c> — is allowed to open a microphone, and
/// <c>CompanionSourceGuardTests</c> fails the build if any other file the client ships names a
/// recording API at all.</para>
/// <para><strong>It is always visible while it runs.</strong> Closing the window leaves a tray
/// icon; the program never becomes invisible, and pausing stops transmission immediately and shows
/// that it has.</para>
/// <para><strong>It runs once.</strong> Starting it again — which is what Windows does when a
/// browser opens a <c>modbot-companion://</c> link — hands the link to the copy already running and
/// exits. One tray icon, one log reader, one set of queues.</para>
/// </remarks>
internal sealed class ModbotCompanionApp : Application
{
    /// <summary>
    /// What this process was started with, if anything: a pairing link from the browser, or a
    /// request to show the window. Handled once the host is up.
    /// </summary>
    internal static string? StartupMessage { get; set; }

    /// <summary>Started by Windows at sign-in: stay in the tray and open no window.</summary>
    internal static bool StartHidden { get; set; }

    /// <summary>
    /// Avalonia draws a templated control -- a text box, a button, a check box -- only through a
    /// control theme, and an application with none draws nothing where those should be. The
    /// pairing token box was the first casualty anyone noticed: the card rendered its words and
    /// the box between them was simply absent. The colours and sizes on every control are still
    /// set by hand in Controls.cs; the theme supplies the templates those settings apply to.
    /// </summary>
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        // The theme paints focus rings and selections in the machine's own accent colour, which
        // on a Windows set to red gave every focused box a red edge. Modbot's accent instead,
        // in the three shades the theme derives from it.
        var accent = Ui.T.Palette.Accent;
        Resources["SystemAccentColor"] = accent;
        Resources["SystemAccentColorDark1"] = accent;
        Resources["SystemAccentColorDark2"] = accent;
        Resources["SystemAccentColorDark3"] = accent;
        Resources["SystemAccentColorLight1"] = accent;
        Resources["SystemAccentColorLight2"] = accent;
        Resources["SystemAccentColorLight3"] = accent;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window leaves the client reporting from the tray, which is the point of
            // it. Quitting is a deliberate act from the tray menu -- and quitting really does stop
            // reporting, rather than minimising to somewhere less visible.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Built here and not in a static initializer: the host owns the main window, and a
            // window can only exist once Avalonia's windowing platform is up. A static
            // initializer runs on the first touch of any static member of this class -- Main
            // setting StartupMessage was enough -- which is before Avalonia has started, and the
            // whole client then died at launch with "Unable to locate IWindowingPlatform".
            CrashGuard.InstallForUi();

            try
            {
                Host = new CompanionHost();

                // The lifetime shows its main window at start. A start from the startup entry has none,
                // so the client sits in the tray until the icon is clicked.
                if (!StartHidden)
                    desktop.MainWindow = Host.Window;
                Host.Start(desktop, StartupMessage);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "The companion could not start");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static CompanionHost? Host { get; private set; }
}

/// <summary>
/// Holds the window, the state behind it, and the timer that refreshes it.
/// </summary>
/// <remarks>
/// The engine that reads the log and reports is constructed by whatever composes this application;
/// this type owns only what the moderator sees and the actions they can take. Keeping the two
/// apart is what lets the reading and reporting half stay a small library that can be audited
/// without reading any UI code.
/// </remarks>
internal sealed class CompanionHost : IOverlayListener
{
    /// <summary>
    /// What a second copy sends when it was started with no link: the person double-clicked the
    /// icon again, and wants the window.
    /// </summary>
    internal const string ShowCommand = "show";

    /// <summary>
    /// How often the voice is given a turn: to finish a download, to start the next line. A turn
    /// with nothing waiting costs a few comparisons.
    /// </summary>
    private readonly DispatcherTimer _voiceLoop = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// Writes the voice settings a moment after the last change, so a volume slider being dragged
    /// is one write rather than a hundred.
    /// </summary>
    private readonly DispatcherTimer _voiceSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Which servers' token rejections the voice has already said, so each is said once.</summary>
    private readonly HashSet<string> _tokenRejectionsSpoken = new(StringComparer.Ordinal);

    private readonly IModbotClock _clock = new SystemModbotClock();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// How often the overlay loop is given a turn.
    /// </summary>
    /// <remarks>
    /// Short, because it is cheap: a turn with nothing to do reads nothing and draws nothing, and
    /// the reads inside it have their own intervals. What this rate actually buys is how quickly a
    /// finished long poll becomes a card on screen.
    /// </remarks>
    private readonly DispatcherTimer _overlayLoop = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// The controllers, looked at thirty times a second while a VR runtime is attached, so the
    /// cursor and a held panel move smoothly; a look costs nothing when none is attached.
    /// </summary>
    private readonly DispatcherTimer _inputLoop = new() { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>Writes the panel's placement half a second after it last changed.</summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>
    /// How often the reading half is given a turn.
    /// </summary>
    /// <remarks>
    /// A turn with nothing to do costs one <c>FileStream</c> open on a file whose length has not
    /// changed. What this rate buys is how soon the overlay learns the moderator has walked into a
    /// different instance — VRChat writes the transition and the overlay should follow it within a
    /// second or two, not within a batch interval.
    /// </remarks>
    private readonly DispatcherTimer _engineLoop = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>One queue file per paired server, kept so unpairing can delete the right one.</summary>
    private readonly Dictionary<string, FileEventBuffer> _buffers = new(StringComparer.Ordinal);

    /// <summary>Stops the inbox that second copies drop pairing links into, when this copy quits.</summary>
    private readonly CancellationTokenSource _inboxStop = new();

    private string _directory = string.Empty;
    private CompanionAppState? _state;
    private PairingCoordinator? _pairing;
    private Journal.SentJournal? _journal;
    private CompanionEngine? _engine;
    private VRChatLogTail? _tail;
    private string? _lastLoggedFile;
    private long _lastLoggedLines;
    private int _consecutiveTickFailures;
    private TrayIcon? _tray;
    private HttpClient? _http;
    private IIngestTransport? _transport;
    private OverlayDriver? _overlay;
    private OverlayHost? _overlayHost;
    private NotificationHost? _notifyHost;
    private OverlayPreviewWindow? _preview;

    /// <summary>The window that sits over VRChat on a monitor, and the key that brings it up.</summary>
    private DesktopOverlayWindow? _desktopOverlay;
    private DesktopOverlayShortcut? _desktopOverlayShortcut;

    /// <summary>
    /// The notification overlay on a monitor: its own window, in a corner, shown when there is
    /// something to say and hidden again when there is not. Nothing to do with the window above.
    /// </summary>
    private DesktopNotifyWindow? _desktopNotify;

    private OverlaySample? _pinnedSample;
    private DateTimeOffset? _overlayAttachedAt;
    private DateTimeOffset? _overlayLastDrewAt;
    private int _overlayFramesSeen;
    private DateTimeOffset? _notifyAttachedAt;
    private DateTimeOffset? _notifyLastDrewAt;
    private int _notifyFramesSeen;

    /// <summary>
    /// The pop-ups the notification overlay shows. Made once, at start, and kept whether or not
    /// either panel is up: the drive loop fills it, and the notification host draws it when there
    /// is one.
    /// </summary>
    private PopUps? _popUps;
    private Updates? _updates;
    private CloudCredits? _credits;
    private CloudEventBackup? _cloudBackup;
    private VoiceHost? _voice;

    /// <summary>The recorder, built only while Clips is on and VRChat is running; null otherwise.</summary>
    private ScreenRecording? _recorder;

    /// <summary>The clips folder's contents, for the settings screen and for making room.</summary>
    private ClipLibrary? _clipLibrary;

    /// <summary>Set once the recorder has failed to start this run, so it is not retried every second.</summary>
    private bool _clipsFailed;

    /// <summary>What the recorder said before it went, so the settings screen keeps saying it.</summary>
    private string? _clipsProblem;

    /// <summary>The last clip saved this run, kept for the same reason.</summary>
    private string? _clipsLastSaved;

    /// <summary>
    /// How the last Save a clip turned out, and when. The overlay says it for a few seconds,
    /// because a moderator in a headset has no other way to find out (clips design spec §11).
    /// </summary>
    private ClipSave? _clipSave;

    /// <summary>When Save a clip was last pressed, while the answer is still coming.</summary>
    private DateTimeOffset? _clipAskedAt;

    /// <summary>What the recorder had last saved, and last complained about, when it was pressed.</summary>
    private string? _clipSavedBefore;

    private string? _clipProblemBefore;

    /// <summary>
    /// How long a Save is given to produce a file before it is called a failure. The recorder acts
    /// on its next frame, which is a fifteenth of a second; this is generous on purpose, because
    /// saying "Clip not saved" about one that did land is the worse mistake.
    /// </summary>
    private static readonly TimeSpan ClipSaveAnswerWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The listener, built only while Listening is on and VRChat is running; null otherwise, which
    /// means no microphone is open.
    /// </summary>
    private PhraseListening? _listener;

    /// <summary>The one rule that stops one spoken sentence becoming three clips.</summary>
    private PhraseHeard? _phraseRule;

    /// <summary>The one download of the phrase model, while it is running.</summary>
    private Task<PhraseDownloadResult>? _phraseDownload;

    private bool _phrasePresent;
    private bool _phraseFailed;
    private double _phraseProgress;
    private string? _listeningProblem;
    private string? _listeningLastHeard;

    /// <summary>
    /// The microphones this PC has, as the Listening card's list shows them, and when they were
    /// last asked for.
    /// </summary>
    /// <remarks>
    /// Asked only while listening is switched on, so a switched-off client still asks Windows'
    /// audio system nothing at all, and asked every few seconds rather than on every render,
    /// because the answer only changes when somebody plugs something in.
    /// </remarks>
    private IReadOnlyList<Microphone> _microphones = [];
    private DateTimeOffset _microphonesAskedAt = DateTimeOffset.MinValue;

    /// <summary>How often the list of microphones is asked for again while listening is on.</summary>
    private static readonly TimeSpan AskAboutMicrophonesEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True when the Save a clip that is waiting for an answer was asked for out loud. A moderator
    /// who spoke to their PC has no screen to read, so the answer is spoken or played back to them
    /// (listening design §6); one that was pressed on a screen already has the screen.
    /// </summary>
    private bool _clipAskedBySpeaking;

    private bool _voiceTicking;
    private NotificationSound? _bleep;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private string _settingsPath = string.Empty;

    /// <summary>Stops the background tasks -- the event backup and the credits read -- when this copy quits.</summary>
    private readonly CancellationTokenSource _backupStop = new();
    private bool _overlayTicking;
    private bool _engineTicking;

    /// <summary>Whether the overlay's three timers already have their handlers; they are wired once.</summary>
    private bool _overlayLoopsWired;

    /// <summary>The main overlay's on/off switch; null until the client has read its settings.</summary>
    private OverlaySwitch? _overlaySwitch;

    /// <summary>The notification overlay's own switch. Independent of the one above.</summary>
    private OverlaySwitch? _notifySwitch;

    /// <summary>The switch for the window that sits over VRChat on a monitor. Independent of both.</summary>
    private OverlaySwitch? _desktopSwitch;

    /// <summary>The switch for the notification overlay on a monitor. Independent of the other three.</summary>
    private OverlaySwitch? _desktopNotifySwitch;

    public MainWindow Window { get; } = new();

    public void Start(IClassicDesktopStyleApplicationLifetime desktop, string? startupMessage)
    {
        _desktop = desktop;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _directory = Path.Combine(appData, "Modbot");

        _journal = new Journal.SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        _settingsPath = CompanionSettings.DefaultPath(appData);
        _state = new CompanionAppState(_clock, _journal, CompanionSettings.Load(_settingsPath));

        // MODBOT_DEBUG_MODE=1 adds the Debug page: the overlay's picture in a window, sample
        // screens to pin into it. Read once, at start, like the other environment switches.
        _state.DebugMode = Environment.GetEnvironmentVariable("MODBOT_DEBUG_MODE") is { } debug
            && (debug == "1" || debug.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (_state.DebugMode)
            Log.Information("Debug mode is on (MODBOT_DEBUG_MODE); the window has a Debug page");

        // Only the token is encrypted; the rest of the file is left readable on purpose, so a
        // suspicious moderator can open it and see exactly which servers this client talks to.
        var store = new DpapiPairingStore(
            DpapiPairingStore.DefaultPath(appData),
            PairingSecretProtectors.ForThisMachine(appData));

        // One client, kept for the life of the process. A disposed-per-use HttpClient exhausts
        // sockets under any real traffic, and this one is also the single place pairing requests
        // leave from.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Window.Pictures = new GroupPictures(_http, Render);
        _pairing = new PairingCoordinator(new HttpPairingClient(_http), store);
        _transport = new HttpIngestTransport(_http);

        StartCloudBackup(appData);
        StartCredits(appData);
        StartVoice();
        StartEngine();

        // A pairing whose token will not decrypt is shown by name rather than retried or hidden.
        // A token encrypted for a different Windows account is DPAPI working, not failing, and the
        // honest answer is "pair this one again".
        foreach (var pairing in store.Load())
        {
            if (pairing is { IsUsable: true, Pairing: { } usable })
                Connect(usable);
            else
                _state.UnusablePairings.Add(pairing);
        }

        // Off in settings means a panel is never built in the first place, and its own switch
        // brings it up or takes it down without a restart. Four panels -- the main headset one,
        // the notification one, the window over VRChat on a monitor and the notification overlay
        // beside it -- so four switches, and none of them can take another down.
        _popUps = new PopUps(_clock)
        {
            // The longest any surface keeps a card, so one is dropped only once nobody wants it;
            // each surface asks for its own number of seconds when it draws.
            Dwell = LongestPopUp(),

            // The Notifications card's Pop-up column, read at the moment of showing so a tick
            // changed mid-session takes effect at once.
            Wanted = kind => _state!.Settings.NotificationFilters.PopUpShows(kind),
        };
        _overlaySwitch = new OverlaySwitch(StartOverlay, StopOverlay, _state.Settings.OverlayOn);
        _notifySwitch = new OverlaySwitch(StartNotifyOverlay, StopNotifyOverlay, _state.Settings.NotifyOverlay.On);
        _desktopSwitch = new OverlaySwitch(StartDesktopOverlay, StopDesktopOverlay, _state.Settings.DesktopOverlay.On);
        _desktopNotifySwitch = new OverlaySwitch(
            StartDesktopNotifyOverlay, StopDesktopNotifyOverlay, _state.Settings.DesktopNotifyOverlay.On);
        _overlaySwitch.StartIfOn();
        _notifySwitch.StartIfOn();
        _desktopSwitch.StartIfOn();
        _desktopNotifySwitch.StartIfOn();

        InstallTray(desktop);
        ListenForLinks();
        StartUpdateChecks();
        ApplyStartWithWindows();

        _refresh.Tick += (_, _) => CrashGuard.Run("refreshing the window", Render);
        _refresh.Start();
        Render();

        // Started by a browser link: the whole reason this process exists is to pair, so do that
        // now, in front of the moderator, rather than sitting in the tray waiting to be found.
        if (startupMessage is not null)
            _ = CrashGuard.RunAsync("handling the pairing link", () => HandleMessageAsync(startupMessage));
    }

    /// <summary>
    /// Brings up the event backup to Modbot Cloud, on its own task.
    /// </summary>
    /// <remarks>
    /// <para><strong>What it sends, and where.</strong> The presence events the client reports, for
    /// every instance, to <c>https://cloud.modbot.co</c> or the Cloud named in <c>settings.json</c> or
    /// <c>MODBOT_CLOUD_ENDPOINT</c>, unless <c>settings.json</c> or <c>MODBOT_CLOUD_DISABLED</c> on
    /// this PC turns it off. Paired servers have no say in it. Read once, at start. The details are on
    /// <see cref="CloudEventBackup"/> and <see cref="CloudSettings"/>.</para>
    /// <para><strong>What it writes to your disk.</strong> Its queue under
    /// <c>%APPDATA%\Modbot\cloud</c>, capped at 20 MB, and <c>cloud-installs.json</c> with this
    /// client's install id and its secret, encrypted to your Windows account.</para>
    /// <para>Its own task, so nothing it does — disk, compression, a slow or missing Cloud — ever
    /// holds up the reading loop, which also feeds presence reporting and the overlay.</para>
    /// </remarks>
    private void StartCloudBackup(string appData)
    {
        var cloud = _state!.Settings.Cloud;

        if (cloud.RejectedEndpoint is { } rejected)
            Log.Warning("Ignoring the Modbot Cloud address {Endpoint}: it must be an https address; using {Default}", rejected, cloud.Endpoint);

        _cloudBackup = new CloudEventBackup(new CloudBackupOptions(
            Path.Combine(_directory, "cloud"),
            _clock,
            new HttpCloudLogClient(_http!, _clock),
            new DpapiCloudInstallStore(
                DpapiCloudInstallStore.DefaultPath(appData),
                PairingSecretProtectors.ForThisMachine(appData, SecretPurposes.CloudSecret)),
            ModbotVersion.Release,
            Endpoint: cloud.Endpoint,
            Enabled: !cloud.Disabled,
            Journal: _journal));

        var backup = _cloudBackup;
        _ = Task.Run(() => backup.RunAsync(
            _backupStop.Token,
            ex => Log.Warning(ex, "The event backup to Modbot Cloud hit a problem; it carries on")));

        if (cloud.Disabled)
            Log.Information("Event backup to Modbot Cloud is off");
        else
            Log.Information("Event backup to Modbot Cloud is on, to {Endpoint}", cloud.Endpoint);
    }

    /// <summary>
    /// Makes Windows' startup entry match "Start Modbot Companion when my computer starts".
    /// </summary>
    /// <remarks>
    /// <para><strong>Only an installed copy does anything.</strong> The installer (asked in Updates.cs) says whether this copy was
    /// installed; one run from source or a plain folder shows no switch and never reads or writes
    /// the registry. See <see cref="StartWithWindows"/> for the rules and
    /// <see cref="StartupRegistration"/> for the one key it writes.</para>
    /// <para>Run on every start, so the first run after install turns it on, and a stale entry left
    /// by an earlier copy is rewritten.</para>
    /// </remarks>
    private void ApplyStartWithWindows()
    {
        if (_state is null || !OperatingSystem.IsWindows())
            return;

        var launcher = Updates.InstalledLauncherPath();
        _state.Startup = new StartWithWindows(new StartupRegistration())
            .Apply(launcher is not null, launcher, _state.Settings.StartWithWindows);
    }

    /// <summary>
    /// Points the log reader at a folder the person named, or back at the well-known places when
    /// they clear it. Takes effect on the next pass, with no restart: whatever log is already in
    /// the new folder is history and is not reported.
    /// </summary>
    private void SetLogFolder(string? folder)
    {
        if (_state is null || _tail is null)
            return;

        var configured = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (string.Equals(configured, _state.Settings.VRChatLogFolder, StringComparison.Ordinal))
            return;

        _state.Settings = _state.Settings with { VRChatLogFolder = configured };

        if (!CompanionSettings.SaveText(_settingsPath, CompanionSettings.VRChatLogFolderField, configured))
            Log.Warning("Could not save the VRChat log folder to {Path}", _settingsPath);

        var resolved = VRChatLogFolders.Resolve(configured);
        _tail.Redirect(resolved);
        _state.LogFolder = resolved;
        _lastLoggedFile = null;
        Log.Information("Watching VRChat's log folder {Directory}", resolved);
        Render();
    }

    /// <summary>
    /// Brings up the voice: the sound output for this platform and the announcer the engine will
    /// feed. It says nothing until the settings say it may, and fetches the voice only then.
    /// </summary>
    /// <remarks>
    /// <para><strong>What leaves the machine.</strong> One download of the voice from GitHub, the
    /// first time the voice is turned on or tested, described on <see cref="VoiceDownload"/>.
    /// Nothing else: what is said is made and played on this PC.</para>
    /// <para>Built before the engine so the engine can be handed the announcer; the moderator's
    /// own id is read back from the engine, which exists by the time anything is observed.</para>
    /// </remarks>
    /// <summary>
    /// Reads the people the project thanks from Modbot Cloud, for the Credits page.
    /// </summary>
    /// <remarks>
    /// <para><strong>The client asks Cloud itself, never a paired server.</strong> Which Cloud, and
    /// whether to ask at all, comes from <c>settings.json</c> and the two Cloud environment
    /// variables on this PC — the same two values the event backup uses, and a paired Modbot has no
    /// say in either. A client with nothing paired still has a Credits page.</para>
    /// <para>What is sent, and the copy kept in <c>%APPDATA%\Modbot\credits.json</c> so the page
    /// works offline, are on <see cref="CloudCredits"/>. Its own task, and it asks Cloud at most
    /// once every six hours however often this runs.</para>
    /// </remarks>
    private void StartCredits(string appData)
    {
        var credits = new CloudCredits(
            _http!, _state!.Settings.Cloud, CloudCredits.DefaultPath(appData), _clock);

        _credits = credits;

        _ = Task.Run(async () =>
        {
            // Tried again while the client runs, because a PC that was offline when Modbot started
            // is the ordinary case and an empty Credits page for the rest of the session is a poor
            // answer to it. A read that is still fresh makes no request at all.
            while (!_backupStop.IsCancellationRequested)
            {
                await CrashGuard.RunAsync(
                    "reading the credits from Modbot Cloud",
                    () => credits.RefreshAsync(_backupStop.Token));

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), _backupStop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private void StartVoice()
    {
        _voice = new VoiceHost(
            _directory,
            _http!,
            _clock,
            () => _state!.Settings.Voice,
            () => _engine?.ModeratorId,
            filters: () => _state!.Settings.NotificationFilters);

        _voiceLoop.Tick += async (_, _) => await CrashGuard.RunAsync("speaking", VoiceTickAsync);
        _voiceLoop.Start();

        _voiceSave.Tick += (_, _) =>
        {
            _voiceSave.Stop();
            if (_state is not null && !CompanionSettings.SaveVoice(_settingsPath, _state.Settings.Voice))
                Log.Warning("Could not save the voice settings to {Path}", _settingsPath);
        };

        StartBleep();
    }

    /// <summary>
    /// Brings up the short sound the client makes when it has something to tell the moderator.
    /// </summary>
    /// <remarks>
    /// <para>Through the voice's own output object, so there is one audio device list in the
    /// process and the sound comes out of whatever the moderator chose for the voice. Its switch
    /// and its volume are its own (<see cref="NotificationSettings"/>).</para>
    /// <para>The sound is made from a formula in code — there is no audio file in the client — and
    /// played on this PC. No server is told it happened.</para>
    /// </remarks>
    private void StartBleep()
    {
        if (_voice is null)
            return;

        _bleep = new NotificationSound(
            _voice.Player,
            _voice.Devices,
            () => _state!.Settings.Notifications,
            () => _state!.Settings.Voice.OutputDeviceId,
            new BleepRule(_clock),
            line => Log.Information("Notification sound: {Line}", line),
            () => _state!.Settings.NotificationFilters);
    }

    /// <summary>The Notifications card changed. Takes effect at once; the file is written now.</summary>
    private void SetNotifications(NotificationSettings notifications)
    {
        if (_state is null || _state.Settings.Notifications == notifications)
            return;

        _state.Settings = _state.Settings with { Notifications = notifications };

        if (!CompanionSettings.SaveNotifications(_settingsPath, notifications))
            Log.Warning("Could not save the notification settings to {Path}", _settingsPath);

        Render();
    }

    /// <summary>The Notifications card's Test button: one bleep, whether or not the sound is on.</summary>
    private void TestBleep() => _bleep?.Ask(NotificationKind.Test);

    /// <summary>
    /// The Notifications card's filter list changed: which kinds of event raise a notification, by
    /// each of the three ways. Takes effect at once; the file is written now.
    /// </summary>
    /// <remarks>
    /// The <c>voice</c> object is written too, because its three switches are three of the ticks
    /// in the Voice column and the file must not say two different things (notification filters
    /// design 2026-09-19 §4.2).
    /// </remarks>
    private void SetNotificationFilters(NotificationFilters filters)
    {
        if (_state is null || _state.Settings.NotificationFilters.Equals(filters))
            return;

        var voice = filters.InStepWith(_state.Settings.Voice);
        _state.Settings = _state.Settings with { NotificationFilters = filters, Voice = voice };

        if (!CompanionSettings.SaveNotificationFilters(_settingsPath, filters))
            Log.Warning("Could not save the notification filters to {Path}", _settingsPath);
        else if (!CompanionSettings.SaveVoice(_settingsPath, voice))
            Log.Warning("Could not save the voice settings to {Path}", _settingsPath);
        Render();
    }

    /// Makes the recorder match what the Clips card and VRChat are doing right now.
    /// </summary>
    /// <remarks>
    /// <para><strong>Off means nothing is built.</strong> With the switch off there is no recorder
    /// object, no Direct3D device, no encoder and no thread — the same shape as the overlay
    /// switches, and for the same reason: off is a moderator saying they do not want it, so the
    /// honest answer is to do none of the work.</para>
    /// <para><strong>"While VRChat is running" is the log, not the process list.</strong> The
    /// reading half already knows, because lines are either arriving or they are not
    /// (<see cref="ClipRecordingRule"/>). Run on every render, so VRChat starting or stopping is
    /// followed within a second and nothing has to be restarted by hand.</para>
    /// <para>Every failure here — no Direct3D, no encoder, a folder that cannot be written to —
    /// becomes a line on the settings screen. None of them stops the client reading VRChat's log or
    /// reporting presence, which is the job that cannot be filled in later.</para>
    /// </remarks>
    private void ApplyClips()
    {
        if (_state is null)
            return;

        // A recorder whose thread died — no encoder, a monitor unplugged mid-session — has said so
        // in one sentence. Noticed here so it is not started again every second for the rest of the
        // evening; switching the card off and on again is a fresh go.
        if (_recorder is { IsRecording: false, LastProblem: { } died })
        {
            _clipsFailed = true;
            _clipsProblem = died;
        }

        var settings = _state.Settings.Clips;
        var folder = settings.On
            ? ClipsFolder.Check(ClipsFolder.Resolve(settings.Folder, fallback: _directory))
            : new ClipsFolderCheck(ClipsFolder.Resolve(settings.Folder, fallback: _directory), null);

        var wanted = ClipRecordingRule.Decide(
            settings,
            _state.LogHealth.Evaluate(_clock.UtcNow, CompanionAppState.LogSilenceThreshold),
            folder,
            ScreenRecording.Supported,
            _clipsFailed,
            _recorder?.WindowFound,
            _recorder?.AnyPictureTaken);

        if (ClipRecordingRule.ShouldRecord(wanted))
        {
            if (_recorder is null)
            {
                _recorder = new ScreenRecording(
                    _clock,
                    Path.Combine(_directory, "clips"),
                    (line, ex) =>
                    {
                        if (ex is null)
                            Log.Information("Clips: {Line}", line);
                        else
                            Log.Warning(ex, "Clips: {Line}", line);
                    });

                if (!_recorder.Start(settings.Length))
                {
                    _clipsFailed = true;
                    _clipsProblem = _recorder.LastProblem;
                    wanted = ClipRecordingState.NotOnThisMachine;
                }
            }
        }
        else if (_recorder is not null)
        {
            // Kept, because the recorder is about to go and what it had to say about itself is the
            // only thing on the settings screen explaining why nothing is being recorded.
            _clipsProblem ??= _recorder.LastProblem;
            _clipsLastSaved ??= _recorder.LastSaved;

            _recorder.Stop();
            _recorder.Dispose();
            _recorder = null;
        }

        var clips = _clipLibrary ??= new ClipLibrary(_clock);
        var saved = folder.IsUsable ? clips.List(folder.Path) : [];

        AnswerTheSave();

        _state.Clips = new ClipsStatus(
            settings,
            wanted,
            folder,
            saved.Count,
            saved.Sum(c => c.Bytes),
            _recorder?.LastSaved ?? _clipsLastSaved,
            _recorder?.LastProblem ?? _clipsProblem,
            ScreenRecording.Supported);

        // What the overlay's Save a clip control shows. Worked out here rather than in the overlay
        // because this is the half that owns the recorder; the panel is only told the answer.
        if (_overlay is not null)
            _overlay.Clips = ClipButtonRule.For(_state.Clips, _clock.UtcNow, _clipSave);
    }

    /// <summary>
    /// Turns a Save a clip that was asked for into a yes or a no, once the recorder has answered.
    /// </summary>
    /// <remarks>
    /// The recorder writes the file on its own thread, on its next frame, so the answer arrives a
    /// moment after the press. It is a yes when a new file name appears, a no when the recorder
    /// complains about something it did not complain about before, and a no after
    /// <see cref="ClipSaveAnswerWait"/> if neither happens — a recorder that has gone quiet must
    /// not leave a moderator in a headset believing a clip is on their disk.
    /// </remarks>
    private void AnswerTheSave()
    {
        if (_clipAskedAt is not { } asked)
            return;

        if (_recorder is { } recorder)
        {
            if (recorder.LastSaved is { } now && now != _clipSavedBefore)
            {
                _clipSave = new ClipSave(_clock.UtcNow, true);
                _clipAskedAt = null;
                AnswerOutLoud(true);
                return;
            }

            if (recorder.LastProblem is { } problem && problem != _clipProblemBefore)
            {
                _clipSave = new ClipSave(_clock.UtcNow, false);
                _clipAskedAt = null;
                AnswerOutLoud(false);
                return;
            }
        }

        if (_clock.UtcNow - asked < ClipSaveAnswerWait)
            return;

        _clipSave = new ClipSave(_clock.UtcNow, false);
        _clipAskedAt = null;
        AnswerOutLoud(false);
    }

    /// <summary>
    /// The Clips card changed. Saved as the whole <c>clips</c> object, then acted on at once: a
    /// length or a folder that changed rebuilds the recorder rather than waiting for a restart.
    /// </summary>
    private void SetClips(ClipSettings clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        if (_state is null)
            return;

        var clamped = clips.Clamped();
        if (_state.Settings.Clips == clamped)
            return;

        var before = _state.Settings.Clips;
        _state.Settings = _state.Settings with { Clips = clamped };

        if (!CompanionSettings.SaveClips(_settingsPath, clamped))
            Log.Warning("Could not save the Clips settings to {Path}", _settingsPath);

        // A recorder already running was built around the old length; the switch going off, or any
        // of the numbers changing, means the one that is running is the wrong one.
        if (_recorder is not null
            && (before.On != clamped.On || before.Minutes != clamped.Minutes || before.Folder != clamped.Folder))
        {
            _recorder.Stop();
            _recorder.Dispose();
            _recorder = null;
        }

        // A new switch-on gets a fresh go at a machine that failed last time, and a clean card.
        if (!before.On && clamped.On)
        {
            _clipsFailed = false;
            _clipsProblem = null;
        }

        Log.Information(
            clamped.On
                ? "Keeping the last few minutes is on: {Minutes} minutes into {Folder}"
                : "Keeping the last few minutes is off",
            clamped.Minutes,
            ClipsFolder.Resolve(clamped.Folder, fallback: _directory));

        ApplyClips();
        Render();
    }

    /// <summary>
    /// <strong>Save a clip</strong>: the last few minutes are written into the clips folder, and
    /// the oldest clips there are deleted if that would put the folder over its limit.
    /// </summary>
    /// <remarks>
    /// <para>Nothing is sent anywhere. The clip is a file on this PC, and attaching it to a case is
    /// a separate, deliberate act in Modbot's web interface — the client has no way to upload one
    /// (clips design spec §6).</para>
    /// <para>Reached from two places: the button on the Settings page, and the Save a clip control
    /// on the overlay panel, which is the one a moderator wearing a headset can actually press
    /// (clips design spec §11). Both end here, so there is one rule about room, one naming scheme
    /// and one line in the journal however it was asked for.</para>
    /// </remarks>
    private void SaveClip()
    {
        if (_state is null || _recorder is null || _clipLibrary is null)
        {
            AnswerOutLoud(false);
            return;
        }

        var settings = _state.Settings.Clips;
        var folder = ClipsFolder.Check(ClipsFolder.Resolve(settings.Folder, fallback: _directory));

        if (!folder.IsUsable)
        {
            Log.Warning("A clip could not be saved: {Problem}", folder.Problem);

            // Said on the panel as well as on the settings screen: inside a headset there is no
            // settings screen to read it on.
            _clipSave = new ClipSave(_clock.UtcNow, false);
            _clipAskedAt = null;
            AnswerOutLoud(false);
            Render();
            return;
        }

        // Room is made before the clip is written rather than after, so the disk never has to hold
        // the folder's limit plus one more clip at the same moment.
        _clipLibrary.MakeRoom(folder.Path, settings.KeepBytes, aboutToAdd: 0);

        // The world and the instance go into the file name so a moderator can find the right clip
        // afterwards — "The Black Cat_98874_2026-09-19 18-02-29.mp4". The world's readable name
        // when VRChat's log has said it, its id when it has not, and neither when there is no
        // instance. None of it is trusted to be a file name: a world name is whatever somebody
        // typed and VRChat's ids follow no structure (foundation 3.1.1), so the assembled name is
        // made safe once, whole, by ClipLibrary.
        var name = _clipLibrary.NameFor(
            worldName: CurrentWorldName,
            worldId: CurrentInstance?.WorldId,
            instanceId: CurrentInstance?.InstanceId,
            folder: folder.Path);

        // What the recorder had to say before being asked, so its next word can be read as the
        // answer to this press rather than as something it said earlier.
        _clipSavedBefore = _recorder.LastSaved;
        _clipProblemBefore = _recorder.LastProblem;
        _clipAskedAt = _clock.UtcNow;
        _clipSave = null;

        _recorder.AskToSave(Path.Combine(folder.Path, name));

        _journal?.RecordNote("this PC", $"Saved a clip of the last few minutes as {name}. It is on this PC only.");
        Render();
    }

    /// <summary>
    /// Makes the listener match what the Listening card and VRChat are doing right now.
    /// </summary>
    /// <remarks>
    /// <para><strong>Off means no microphone is open.</strong> With the switch off there is no
    /// listener object, no phrase model loaded and no thread — and, most of the point, nothing
    /// asked of Windows' audio system at all. The same shape as the recorder and the overlay
    /// switches, and here it is the whole of the promise: a microphone that is not open cannot hear
    /// anything.</para>
    /// <para><strong>Which microphone, and when the list is asked for.</strong> Only while the
    /// switch is on, and then every few seconds rather than on every render, because the answer
    /// only changes when somebody plugs something in. A microphone picked while it was already
    /// listening closes the open one and opens the picked one, so a moderator who swaps from their
    /// desk microphone to their headset does not have to switch listening off and on again.</para>
    /// <para><strong>"While VRChat is running" is the log, not the process list.</strong> The
    /// reading half already knows (<see cref="ListeningRule"/>). Run on every render, so VRChat
    /// closing closes the microphone within a second.</para>
    /// <para>Every failure here — no model, no microphone, one another program has taken for
    /// itself — becomes a line on the settings screen. None of them stops the client reading
    /// VRChat's log or reporting presence.</para>
    /// </remarks>
    private void ApplyListening()
    {
        if (_state is null)
            return;

        var settings = _state.Settings.Listening;
        var folder = PhraseModel.PhrasesFolder(_directory);

        // Asked once rather than every second: the answer only changes when the download finishes,
        // and that path sets it itself.
        if (settings.On && !_phrasePresent && _phraseDownload is null && !_phraseFailed)
        {
            _phrasePresent = PhraseModel.Default.IsPresent(folder);
            if (!_phrasePresent)
                StartPhraseDownload(folder);
        }

        if (_phraseDownload is { IsCompleted: true } finished)
        {
            _phraseDownload = null;
            FinishPhraseDownload(finished);
        }

        // Which microphones this PC has, and therefore which one the moderator's choice lands on.
        // Asked only while the switch is on: off means nothing at all is asked of Windows' audio
        // system, and that is the promise the picker was not allowed to cost.
        if (!settings.On)
        {
            _microphones = [];
            _microphonesAskedAt = DateTimeOffset.MinValue;
        }
        else if (_clock.UtcNow - _microphonesAskedAt >= AskAboutMicrophonesEvery)
        {
            _microphonesAskedAt = _clock.UtcNow;
            _microphones = PhraseListening.Microphones();
        }

        var choice = MicrophoneChoice.Resolve(settings.MicrophoneId, _microphones);

        var wanted = ListeningRule.Decide(
            settings,
            _state.LogHealth.Evaluate(_clock.UtcNow, CompanionAppState.LogSilenceThreshold),
            PhraseListening.Supported,
            _phrasePresent,
            _phraseDownload is not null,
            _listener?.IsListening);

        // A microphone picked while it was already listening, or one that came back after being
        // unplugged, means the open one is the wrong one. Closed here; opened again below.
        if (_listener is not null && !string.Equals(_listener.Using?.Id, choice.Microphone?.Id, StringComparison.Ordinal))
        {
            Log.Information("The microphone to listen on changed; opening the new one");
            _listener.Stop();
            _listener.Dispose();
            _listener = null;
        }

        if (ListeningRule.ShouldListen(wanted))
        {
            if (_listener is null)
            {
                _phraseRule ??= new PhraseHeard(_clock);
                _listener = new PhraseListening(HeardFromAnotherThread, LogListening);

                if (!_listener.Start(PhraseModel.Default, folder, choice.Microphone))
                {
                    _listeningProblem = _listener.LastProblem;
                    wanted = ListeningState.NotOnThisMachine;
                }
            }
        }
        else if (_listener is not null)
        {
            // Kept, because the listener is about to go and what it had to say about itself is the
            // only thing on the settings screen explaining why nothing is being listened for.
            _listeningProblem ??= _listener.LastProblem;
            _listeningLastHeard ??= _listener.LastHeard;

            _listener.Stop();
            _listener.Dispose();
            _listener = null;
            _phraseRule?.Forget();
        }

        _state.Listening = new ListeningStatus(
            settings,
            wanted,
            PhraseModel.Default.Spoken,
            _phraseProgress,
            PhraseModel.Default.Size,
            _listener?.LastHeard ?? _listeningLastHeard,
            _listener?.LastProblem ?? _listeningProblem,
            PhraseListening.Supported,
            _microphones,
            choice.FellBack);
    }

    /// <summary>
    /// The one download of the phrase model, started only because somebody turned listening on.
    /// </summary>
    private void StartPhraseDownload(string folder)
    {
        var model = PhraseModel.Default;
        _phraseProgress = 0;
        _listeningProblem = null;

        Log.Information(
            "Downloading the phrase model {Name} from {Url} ({Size:N0} bytes)", model.Name, model.Url, model.Size);

        var progress = new Progress<double>(p => _phraseProgress = p);
        _phraseDownload = Task.Run(() => new PhraseDownload(_http!).RunAsync(model, folder, progress));
    }

    private void FinishPhraseDownload(Task<PhraseDownloadResult> finished)
    {
        PhraseDownloadResult result;
        try
        {
            result = finished.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _phraseFailed = true;
            _listeningProblem = $"The phrase model could not be downloaded: {ex.Message}";
            Log.Warning(ex, "The phrase model download failed");
            return;
        }

        if (result.Ready)
        {
            _phrasePresent = true;
            _phraseProgress = 1;
            Log.Information("The phrase model is ready ({Outcome})", result.Outcome);
            return;
        }

        _phraseFailed = true;
        _listeningProblem = result.Outcome switch
        {
            PhraseDownloadOutcome.WrongFile => $"The download was not the expected file and was thrown away. {result.Detail}",
            PhraseDownloadOutcome.Unreachable => $"The phrase model could not be downloaded. {result.Detail}",
            _ => $"Listening could not be set up. {result.Detail}",
        };

        Log.Warning("The phrase model download failed: {Outcome} {Detail}", result.Outcome, result.Detail);
    }

    /// <summary>
    /// The listener heard a phrase, on its own thread. Brought onto the window's thread, because
    /// everything below saves a clip and redraws.
    /// </summary>
    private void HeardFromAnotherThread(string said)
        => Dispatcher.UIThread.Post(() => CrashGuard.Run("acting on a phrase", () => HeardAPhrase(said)));

    private static void LogListening(string line, Exception? ex)
    {
        if (ex is null)
            Log.Information("Listening: {Line}", line);
        else
            Log.Warning(ex, "Listening: {Line}", line);
    }

    /// <summary>
    /// Somebody said "Modbot, clip that": save a clip, and answer them.
    /// </summary>
    /// <remarks>
    /// <para><strong>It never looks like it worked when it did not.</strong> Inside a headset there
    /// is no settings screen and no file explorer, so a phrase that quietly did nothing would leave
    /// a moderator believing they had kept a moment. Every reason a clip cannot be saved is
    /// answered out loud instead (listening design §6), and a save that goes ahead is answered once
    /// the recorder has actually written the file, through the same wait the button uses.</para>
    /// <para><strong>One sentence, one clip.</strong> <see cref="PhraseHeard"/> refuses a second
    /// match within a few seconds, which covers the matcher offering the same words twice and a
    /// moderator repeating themselves.</para>
    /// <para>It is written into the client's own journal every time, so the Events page shows every
    /// occasion the microphone acted on something — including the ones where nothing was
    /// saved.</para>
    /// </remarks>
    private void HeardAPhrase(string said)
    {
        if (_state is null || _phraseRule?.Ask() is not true)
            return;

        var clips = _state.Clips;

        if (clips.CanSave)
        {
            _journal?.RecordNote(
                "this PC",
                $"Heard “{said}” and saved a clip. Nothing of what was said was recorded or sent.");

            _clipAskedBySpeaking = true;
            SaveClip();
            return;
        }

        var why = clips.State switch
        {
            ClipRecordingState.Off => "Clips are switched off, so there was nothing to save.",
            ClipRecordingState.Waiting => "VRChat is not running, so there was nothing to save.",
            ClipRecordingState.NoWindow => "Modbot has not found VRChat's window yet, so there was nothing to save.",
            ClipRecordingState.FolderUnusable => "The clips folder cannot be used, so nothing was saved.",
            ClipRecordingState.NotOnThisMachine => "This PC cannot record, so nothing was saved.",
            _ => "Recording has stopped, so nothing was saved.",
        };

        _journal?.RecordNote("this PC", $"Heard “{said}”, but no clip could be saved. {why}");
        Log.Information("Heard a phrase but no clip could be saved: {Why}", why);
        AnswerOutLoud(false, why);
        Render();
    }

    /// <summary>
    /// Answers a moderator who asked for a clip out loud, and nobody else.
    /// </summary>
    /// <remarks>
    /// <para>The voice says the sentence when the voice is on and reporting is not paused. When it
    /// is not, a saved clip gets the notification sound — as long as the sound itself is switched
    /// on — and a clip that was <em>not</em> saved gets nothing, because one short sound cannot say
    /// which of six reasons it was, and a sound that meant both "kept" and "not kept" would be
    /// worse than silence. The reason is on the Clips card, on the overlay's own Save a clip
    /// control for eight seconds, and in the Events page either way.</para>
    /// <para>A Save a clip that was <em>pressed</em> is never answered here: whoever pressed it is
    /// looking at the thing they pressed.</para>
    /// </remarks>
    private void AnswerOutLoud(bool saved, string? instead = null)
    {
        if (!_clipAskedBySpeaking)
            return;

        _clipAskedBySpeaking = false;

        var sentence = instead ?? (saved ? "Clip saved." : "The clip could not be saved.");

        if (_voice?.Announcer.Answer(sentence) is true)
            return;

        if (saved && _state?.Settings.Notifications.Bleep is true)
            _bleep?.Ask(NotificationKind.Test);
    }

    /// <summary>
    /// The Listening card changed. Saved as the whole <c>listenForPhrase</c> object, then acted on
    /// at once: turning it off closes the microphone now rather than at the next restart.
    /// </summary>
    private void SetListening(ListeningSettings listening)
    {
        ArgumentNullException.ThrowIfNull(listening);

        if (_state is null || _state.Settings.Listening == listening)
            return;

        var before = _state.Settings.Listening;
        _state.Settings = _state.Settings with { Listening = listening };

        if (!CompanionSettings.SaveListening(_settingsPath, listening))
            Log.Warning("Could not save the listening settings to {Path}", _settingsPath);

        // A new switch-on gets a fresh go at a download that failed last time, and a clean card.
        if (!before.On && listening.On)
        {
            _phraseFailed = false;
            _listeningProblem = null;
        }

        if (!listening.On && _listener is not null)
        {
            _listener.Stop();
            _listener.Dispose();
            _listener = null;
        }

        Log.Information(
            listening.On
                ? "Listening for a phrase is on: the microphone opens while VRChat runs"
                : "Listening for a phrase is off: no microphone is opened");

        ApplyListening();
        Render();
    }

    /// <summary>
    /// The window was closed with the X: the client is still here, still reporting, and the tray
    /// icon is where it now lives. Said the first few times only, and counted in settings.json so
    /// somebody who closes the window twenty times a day is told three times.
    /// </summary>
    private void ClosedToTray()
    {
        if (_state is null)
            return;

        var notifications = _state.Settings.Notifications;
        if (!notifications.ShowTrayNotice)
            return;

        var counted = notifications.WithTrayNoticeShown();
        _state.Settings = _state.Settings with { Notifications = counted };

        if (!CompanionSettings.SaveNotifications(_settingsPath, counted))
            Log.Warning("Could not save the notification settings to {Path}", _settingsPath);

        TrayNoticeWindow.Show("Modbot is minimised to the tray");
    }

    /// <summary>One turn of the voice, never overlapping itself: a line takes seconds to say.</summary>
    private async Task VoiceTickAsync()
    {
        if (_voice is null || _state is null || _voiceTicking)
            return;

        _voiceTicking = true;
        try
        {
            // Silent while any paired server is paused: pausing means "stop watching what I do",
            // and a voice narrating the instance would be watching.
            await _voice.TickAsync(_state.Connections.Any(c => c.IsPaused));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _voiceTicking = false;
        }
    }

    /// <summary>The Voice card changed. Takes effect at once; the file is written a moment later.</summary>
    private void SetVoice(VoiceSettings voice)
    {
        if (_state is null || _voice is null || _state.Settings.Voice == voice)
            return;

        var before = _state.Settings.Voice;
        _state.Settings = _state.Settings with { Voice = voice };
        _voice.Apply(before, voice);

        _voiceSave.Stop();
        _voiceSave.Start();
        Render();
    }

    private void TestVoice()
    {
        _voice?.Test();
        Render();
    }

    /// <summary>
    /// A server rejected this device's token: reporting to it has stopped and will not restart on
    /// its own, which is the one problem worth hearing in the headset. Said once per server,
    /// whichever half of the companion noticed first.
    /// </summary>
    private void AnnounceTokenRejected(string serverId)
    {
        if (!_tokenRejectionsSpoken.Add(serverId))
            return;

        _bleep?.Ask(NotificationKind.Problem, serverId);
        _voice?.Announcer.Problem($"{serverId} rejected this device. Modbot has stopped reporting to it.");
    }

    private void NoticeTokenRejections()
    {
        foreach (var stopped in _state?.Connections.Where(c => c.State is ConnectionState.Stopped) ?? [])
            AnnounceTokenRejected(stopped.ServerId);
    }

    void IOverlayListener.AlertShown(FlaggedJoinAlert alert)
    {
        // The same moment the overlay draws its card: a sound that points at something already on
        // screen, and one sound however many times the alert is noticed.
        _bleep?.Ask(NotificationKind.FlaggedJoin, alert.DisplayName ?? alert.SubjectId);
        _voice?.Announcer.FlaggedJoin(alert.DisplayName);
    }

    void IOverlayListener.TokenRejected(string label) => AnnounceTokenRejected(label);

    /// <summary>The Events page's filter bar changed. Remembered in settings.json so the page opens the way it was left.</summary>
    private void SetEventsFilters(EventFilterSet filters)
    {
        if (_state is null || _state.Settings.EventsFilters.Equals(filters))
            return;

        _state.Settings = _state.Settings with { EventsFilters = filters };

        if (!CompanionSettings.SaveEventsFilters(_settingsPath, filters))
            Log.Warning("Could not save the Events filters to {Path}", _settingsPath);
    }

    private void SetStartWithWindows(bool on)
    {
        if (_state is null || _state.Settings.StartWithWindows == on && _state.Startup.On == on)
            return;

        _state.Settings = _state.Settings with { StartWithWindows = on };

        if (!CompanionSettings.SaveSwitch(_settingsPath, CompanionSettings.StartWithWindowsField, on))
            Log.Warning("Could not save the start-with-Windows switch to {Path}", _settingsPath);

        ApplyStartWithWindows();
        Render();
    }

    /// <summary>
    /// Brings up the half that reads VRChat's log and reports what it sees.
    /// </summary>
    /// <remarks>
    /// <para><strong>One log, read once.</strong> A moderator staffing several groups runs one
    /// client, not one per group; everything after the read is per-server and separate — its own
    /// token, its own queue, its own pause switch.</para>
    /// <para><strong>It runs whether or not anything is paired.</strong> Nothing is transmitted
    /// until a server is, but the reading has to be happening for the client to know where the
    /// moderator is standing the moment they do pair.</para>
    /// </remarks>
    private void StartEngine()
    {
        var folder = VRChatLogFolders.Resolve(_state!.Settings.VRChatLogFolder);
        _tail = new VRChatLogTail(folder);
        _state.LogFolder = folder;
        Log.Information("Watching VRChat's log folder {Directory}", folder);

        var observer = new PresenceObserver(_tail, _clock);
        _engine = new CompanionEngine(
            observer,
            _clock,
            timeProbe: new HttpServerTimeProbe(_http!, _clock),
            backup: _cloudBackup,
            journal: _journal,
            voice: _voice?.Announcer,

            // The same observations, offered to the pop-up overlay and to the bleep. Both ask the
            // moderator's filters, and everything this adds is off until it is ticked on.
            notices: new EventNotifier(
                () => observer.ModeratorId,
                (popUp, kind) => _popUps?.Show(popUp, kind),
                (kind, about) => _bleep?.Ask(kind, about)));

        _engineLoop.Tick += async (_, _) => await CrashGuard.RunAsync("reading VRChat's log", EngineTickAsync);
        _engineLoop.Start();
    }

    /// <summary>
    /// One turn of the reading loop, never overlapping itself.
    /// </summary>
    /// <remarks>
    /// A turn can hold an outbound batch open for as long as the network takes, and stacking those
    /// on a timer would put several copies of the same batch in flight on a machine that is also
    /// running a game.
    /// </remarks>
    private async Task EngineTickAsync()
    {
        if (_engine is null || _engineTicking)
            return;

        _engineTicking = true;
        try
        {
            var tick = await _engine.TickAsync();
            _consecutiveTickFailures = 0;
            if (_state is not null)
                _state.ReadingFault = null;

            DescribeTick(tick);
            NoticeTokenRejections();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // This used to escape the timer's handler, and an exception that escapes an async
            // event handler ends the process -- silently, for a windowed program. Now it is
            // written down with everything known about where the reader was, shown in the window,
            // and the loop carries on; a fault that repeats stops the reader rather than the client.
            _consecutiveTickFailures++;
            var health = _engine.LogHealth;

            Log.Error(
                ex,
                "Reading VRChat's log failed (failure {Count} in a row). File {File}; lines read {Lines}; "
                + "behaviour lines {Behaviour}; recognised events {Recognised}; last line at {LastLine}",
                _consecutiveTickFailures, _tail?.CurrentFile, health.LinesRead, health.BehaviourLines,
                health.RecognisedEvents, health.LastLineAt);

            if (_state is not null)
                _state.ReadingFault = $"{ex.GetType().Name}: {ex.Message}";

            if (_consecutiveTickFailures >= 5)
            {
                _engineLoop.Stop();
                CrashGuard.Report(
                    new InvalidOperationException(
                        "Reading VRChat's log failed five times in a row, so the reader has been stopped. "
                        + "Restart the companion once the cause is fixed. Last error: " + ex.Message, ex),
                    "reading VRChat's log",
                    fatal: false);
            }
        }
        finally
        {
            _engineTicking = false;
        }
    }

    private void DescribeTick(EngineTick tick)
    {
        var file = _tail?.CurrentFile;
        if (!string.Equals(file, _lastLoggedFile, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("Reading VRChat log {File}", file ?? "(none yet)");
            _lastLoggedFile = file;
        }

        var health = _engine!.LogHealth;

        // The first pass over an existing log is history: it produces no observations, and it is
        // also the pass that reads the most and is likeliest to hit something unexpected. So
        // lines read is reported on its own whenever it moves, observations or not.
        if (health.LinesRead != _lastLoggedLines)
        {
            Log.Verbose(
                "Read {Lines} lines from VRChat's log this tick ({Total} so far: {Behaviour} behaviour lines, {Recognised} recognised events)",
                health.LinesRead - _lastLoggedLines, health.LinesRead, health.BehaviourLines, health.RecognisedEvents);
            _lastLoggedLines = health.LinesRead;
        }

        // Quiet ticks are the normal state and are not worth a line each; a tick that did
        // anything says exactly what.
        if (tick.Observed == 0 && tick.BatchesSent == 0)
            return;

        Log.Verbose(
            "Tick: {Observed} observations, {Routed} routed, {Dropped} dropped, {Sent} sent to servers; "
            + "totals: {Lines} lines read, {Behaviour} behaviour lines, {Recognised} recognised events",
            tick.Observed, tick.Routed, tick.Dropped, tick.BatchesSent,
            health.LinesRead, health.BehaviourLines, health.RecognisedEvents);
    }

    /// <summary>
    /// Gives one paired server everything it needs to be reported to, separately from every other.
    /// </summary>
    /// <remarks>
    /// <para><strong>What this puts on your disk.</strong> One queue file per server, under your
    /// own profile folder, holding observations that have not been sent yet. It is bounded by both
    /// size and age, and unpairing deletes it.</para>
    /// <para><strong>What it sends, and where.</strong> Those observations, to that one server's
    /// address and nowhere else, and only ever for instances belonging to the group that server
    /// declared it manages.</para>
    /// <para><strong>The machine's own timezone is supplied here</strong>, because VRChat's
    /// timestamps carry no offset at all and something has to say which instant <c>20:27:14</c>
    /// names. It is read from Windows, not asked of any server, and it is applied per server
    /// alongside that server's separately measured clock offset.</para>
    /// </remarks>
    private void Connect(ServerPairing pairing)
    {
        if (_engine is null || _state is null)
            return;

        var serverClock = new ServerClock(_clock);
        var buffer = new FileEventBuffer(QueuePath(pairing.ServerId), _clock);
        _buffers[pairing.ServerId] = buffer;

        var connection = new ServerConnection(
            pairing,
            buffer,
            new PresenceEventMapper(new LogTimestampConverter(), serverClock),
            serverClock,
            _transport!,
            _clock,
            ModbotVersion.Release,
            journal: _journal);

        _engine.Add(connection);
        _state.Connections.Add(connection);
    }

    /// <summary>
    /// Where one server's unsent observations wait.
    /// </summary>
    /// <remarks>
    /// The server id is the host name from the pairing token, so it is filtered down to characters
    /// a path can hold rather than trusted — a name is not a file name, and treating one as the
    /// other is how a stray character becomes a write somewhere unintended.
    /// </remarks>
    private string QueuePath(string serverId)
    {
        var safe = new string([.. serverId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);

        return Path.Combine(_directory, "queue", $"{safe}.jsonl");
    }

    /// <summary>
    /// Brings up the overlay: the drive loop, and whichever of its two panels are switched on --
    /// the headset panel, and the window that sits over VRChat on a monitor.
    /// </summary>
    /// <remarks>
    /// <para>No SteamVR is the ordinary case, not a fault: presence coverage comes from moderators
    /// reporting, and only some of them wear a headset. The loop runs either way -- it keeps the
    /// local cache warm and costs nothing when there is nothing to draw -- and the runtime simply
    /// reports that there is no headset to show it on.</para>
    /// <para>A failure to create the Direct3D surface is not allowed to take the client down with
    /// it. Reporting presence is the job that cannot be filled in later; the overlay is the one that
    /// can wait for a restart.</para>
    /// <para>With both panels switched off, none of this happens: no texture, no drawing loop, no
    /// controllers read, no window and no VR runtime connected to -- and, because the driver is
    /// what owns them, no live connection to a paired server either. Off is a moderator saying they
    /// do not want the overlay, so the honest answer is to do none of the work.</para>
    /// <para>With only the desktop overlay on, the drive loop runs and the headset half is not
    /// built: no texture, no controllers, no SteamVR. That is the moderator asking for the window
    /// and nothing else, which is a thing a great many of them will want, because most of them
    /// play on a monitor.</para>
    /// </remarks>
    private void StartOverlay()
    {
        {
            try
            {
                _overlayHost = OverlayHost.Create(placement: _state?.Settings.Overlay);
                _overlayHost.KeepLastFrame = _state?.DebugMode is true;
                AttachOverlay();
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or NotSupportedException)
            {
                Log.Information(ex, "The headset panel could not be set up on this machine; presence reporting is unaffected");
                _overlayHost = null;
            }
        }

        if (_overlayHost is not null)
        {
            // The group's picture out of the companion's own cache, so the panel in the headset
            // names the community the same way the window over VRChat does. The panel fetches
            // nothing.
            _overlayHost.GroupIcon = url => Window.Pictures?.For(url);

            // A controller's doing goes to the drive loop (taps, scrolling) and to settings (where
            // the panel was left), so it is where it was left next time.
            _overlayHost.Tapped += target => _overlay?.Tap(target);
            _overlayHost.RosterScrolled += rows => _overlay?.ScrollRoster(rows);
            // Saved once a change has settled rather than on every tick of a drag or a held grip: a
            // panel being moved changes thirty times a second, and the file needs the last one.
            _overlayHost.PlacementChanged += placement =>
            {
                if (_state is null)
                    return;

                _state.Settings = _state.Settings with { Overlay = placement };
                _placementSave.Stop();
                _placementSave.Start();
            };
        }

        StartDriver();

        WireOverlayLoops();
        _inputLoop.Start();
    }

    /// <summary>
    /// Takes the main headset panel down, for the SteamVR page's <strong>Overlay on</strong> switch.
    /// </summary>
    /// <remarks>
    /// The controller loop stops and disposing the host detaches the panel from the VR runtime and
    /// frees the texture. The drive loop keeps running while the notification overlay or the
    /// window over VRChat is on, because both are fed by the same reads and the same live link;
    /// with all three off it stops too, which closes the live connection to each paired server and
    /// drops the roster held in memory. What is left running either way is the half that reads
    /// VRChat's log and reports, which the overlay was never part of.
    /// </remarks>
    private void StopOverlay()
    {
        _inputLoop.Stop();
        _placementSave.Stop();

        _preview?.Close();
        _pinnedSample = null;


        _overlayHost?.Dispose();
        _overlayHost = null;

        _overlayAttachedAt = null;
        _overlayLastDrewAt = null;
        _overlayFramesSeen = 0;
        _overlayAttachTriedAt = DateTimeOffset.MinValue;

        StopDriverIfNobodyWantsIt();

        Log.Information("The main overlay was switched off");
    }

    /// <summary>
    /// Brings up the notification overlay: the pop-ups, fixed to a corner of the view.
    /// </summary>
    /// <remarks>
    /// <para>Its own host, its own texture and its own placement, worked out from the screen spot
    /// the moderator chose. It takes no controller input at all — it is never pointed at, so the
    /// controllers are not read for it and no cursor is drawn on it.</para>
    /// <para>It shares the drive loop with the main panel, because it is fed by the same reads and
    /// the same live link, and it goes on working when the main panel is switched off — which is
    /// the point of having two (two overlay modes design §1).</para>
    /// </remarks>
    private void StartNotifyOverlay()
    {
        var settings = _state?.Settings.NotifyOverlay ?? NotifyOverlaySettings.Default;

        try
        {
            _notifyHost = NotificationHost.Create(placement: settings.ToPlacement());
            AttachNotifyOverlay();
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or NotSupportedException)
        {
            Log.Information(ex, "The notification overlay could not be set up on this machine; presence reporting is unaffected");
            _notifyHost = null;
            return;
        }

        if (_popUps is not null)
            _popUps.Dwell = LongestPopUp();

        StartDriver();
        WireOverlayLoops();
    }

    /// <summary>Takes the notification overlay down, leaving the main panel as it was.</summary>
    private void StopNotifyOverlay()
    {
        _notifyHost?.Dispose();
        _notifyHost = null;

        _notifyAttachedAt = null;
        _notifyLastDrewAt = null;
        _notifyFramesSeen = 0;
        _notifyAttachTriedAt = DateTimeOffset.MinValue;

        // Only when nothing else is showing them. The notification overlay on a monitor reads the
        // same stack, and taking the headset's panel down must not wipe its cards.
        if (_desktopNotify is null)
            _popUps?.ClearAll();

        StopDriverIfNobodyWantsIt();

        Log.Information("The notification overlay was switched off");
    }

    /// <summary>
    /// Brings up the loop that reads from the paired servers, if it is not already up.
    /// </summary>
    /// <remarks>
    /// One loop serves both panels: it is the thing that polls a roster, waits on a server's live
    /// events and turns a flagged arrival into both a card on the main panel and a pop-up on the
    /// notification one. Either panel being on is reason enough for it to run.
    /// </remarks>
    private void StartDriver()
    {
        if (_overlay is not null)
            return;

        // One screen, every panel that shows it. Any of them may be absent, and the loop neither
        // knows nor cares.
        _overlay = new OverlayDriver(
            new OverlayScreens(() => _overlayHost, () => _desktopOverlay),
            new HttpOverlayReadClient(_http!, _clock),
            _clock,
            listener: this,
            sockets: new ClientLiveSocketFactory(),
            popUps: _popUps);

        // Save a clip on either panel. The loop only says that somebody asked; this half owns the
        // recorder, the folder and the limit on it, so this is where a clip is actually written.
        _overlay.SaveClipAsked += SaveClip;

        foreach (var connection in _state?.Connections ?? [])
            _overlay.Add(connection.Pairing, connection.Pairing.OverlayLabel);

        _overlayLoop.Start();
    }

    /// <summary>Stops the loop once neither panel is up, and lets go of what it held.</summary>
    private void StopDriverIfNobodyWantsIt()
    {
        if (_overlayHost is not null
            || _notifyHost is not null
            || _desktopOverlay is not null
            || _desktopNotify is not null)
        {
            return;
        }

        _overlayLoop.Stop();
        _overlay?.Dispose();
        _overlay = null;

        // The Servers page's live column reads this; with no driver there is no live connection,
        // and an empty list reads as "Off" rather than leaving the last word on screen.
        if (_state is not null)
            _state.LiveWords = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The timers outlive any one host -- a switch can put a new one in their place -- so they are
    /// wired once and each turn reads whatever host is there now.
    /// </summary>
    private void WireOverlayLoops()
    {
        if (_overlayLoopsWired)
            return;

        _overlayLoopsWired = true;

        _overlayLoop.Tick += async (_, _) => await CrashGuard.RunAsync("drawing the overlay", OverlayTickAsync);
        _placementSave.Tick += (_, _) =>
        {
            _placementSave.Stop();
            if (_state is null)
                return;

            if (!CompanionSettings.SaveOverlay(_settingsPath, _state.Settings.Overlay))
                Log.Warning("The panel's placement could not be saved to {Path}", _settingsPath);
        };
        _inputLoop.Tick += (_, _) => CrashGuard.Run(
            "reading the controllers",
            () => _overlayHost?.PollInput(TimeSpan.FromMilliseconds(Environment.TickCount64)));
    }

    /// <summary>
    /// The SteamVR page's <strong>Overlay on</strong> switch: saved, then acted on at once rather
    /// than at the next restart.
    /// </summary>
    private void SetOverlayOn(bool on)
    {
        if (_state is null || _state.Settings.OverlayOn == on)
            return;

        _state.Settings = _state.Settings with { OverlayOn = on };

        if (!CompanionSettings.SaveSwitch(_settingsPath, CompanionSettings.OverlayOnField, on))
            Log.Warning("Could not save the overlay switch to {Path}", _settingsPath);

        _overlaySwitch?.Set(on);
        Render();
    }

    /// <summary>
    /// The Settings page's Desktop overlay card: saved, then acted on at once.
    /// </summary>
    /// <remarks>
    /// Turning it on or off is its own switch, which builds or takes down the window and the
    /// keyboard shortcut and leaves the two headset panels alone. A change to the shortcut or the
    /// opacity alone does neither: the shortcut is asked for again, and the window is told its new
    /// opacity.
    /// </remarks>
    private void SetDesktopOverlay(DesktopOverlaySettings desktopOverlay)
    {
        if (_state is null || _state.Settings.DesktopOverlay == desktopOverlay)
            return;

        var before = _state.Settings.DesktopOverlay;
        _state.Settings = _state.Settings with { DesktopOverlay = desktopOverlay };

        if (!CompanionSettings.SaveDesktopOverlay(_settingsPath, desktopOverlay))
            Log.Warning("Could not save the desktop overlay settings to {Path}", _settingsPath);

        if (before.On != desktopOverlay.On)
        {
            _desktopSwitch?.Set(desktopOverlay.On);
        }
        else if (_desktopOverlay is not null)
        {
            _desktopOverlay.Apply(desktopOverlay);

            if (before.ShortcutOrDefault != desktopOverlay.ShortcutOrDefault)
                _desktopOverlayShortcut?.Ask(desktopOverlay.ShortcutOrDefault);
        }

        Render();
    }

    /// <summary>
    /// Builds the window that sits over VRChat, and asks Windows for the shortcut that brings it
    /// up. Neither failing stops anything else: a shortcut another program already holds is shown
    /// on the settings screen and the window can still be opened from there.
    /// </summary>
    private void StartDesktopOverlay()
    {
        var settings = _state!.Settings.DesktopOverlay;

        _desktopOverlay = new DesktopOverlayWindow
        {
            PlaceNear = Window,

            // The group's picture out of the companion's own cache — the same one the window's
            // server cards draw from. The overlay fetches nothing.
            GroupIcon = url => Window.Pictures?.For(url),
        };

        _desktopOverlay.Apply(settings);
        _desktopOverlay.PanelTapped += target => _overlay?.Tap(target);
        _desktopOverlay.RosterScrolled += rows => _overlay?.ScrollRoster(rows);

        _desktopOverlayShortcut = new DesktopOverlayShortcut(ToggleDesktopOverlay);
        _desktopOverlayShortcut.Ask(settings.ShortcutOrDefault);

        // The loop that fills the panel and answers its taps. It used to be started only by the
        // two headset panels, so a moderator who plays on a monitor and has both of those off got
        // a window with nothing in it whose every tap — the Events tab included — landed on a
        // loop that was not there.
        StartDriver();
        WireOverlayLoops();
    }

    /// <summary>Closes the window and gives the shortcut back to whoever wants it next.</summary>
    private void StopDesktopOverlay()
    {
        _desktopOverlayShortcut?.Dispose();
        _desktopOverlayShortcut = null;

        if (_desktopOverlay is not null)
        {
            _desktopOverlay.Dismiss();
            _desktopOverlay.Close();
            _desktopOverlay = null;
        }

        StopDriverIfNobodyWantsIt();
    }

    /// <summary>
    /// Brings up the notification overlay on a monitor: its own window, in the corner the
    /// moderator chose, shown when there is something to say.
    /// </summary>
    /// <remarks>
    /// It shares the drive loop with the other panels, because a flagged person arriving is the
    /// thing it exists to say and the live link is where that is heard. It goes on working when
    /// every other panel is off, which is the point of it having its own switch.
    /// </remarks>
    private void StartDesktopNotifyOverlay()
    {
        var settings = _state!.Settings.DesktopNotifyOverlay;

        _desktopNotify = new DesktopNotifyWindow { PlaceNear = Window };
        _desktopNotify.Apply(settings);

        if (_popUps is not null)
            _popUps.Dwell = LongestPopUp();

        StartDriver();
        WireOverlayLoops();
    }

    /// <summary>Takes it down, leaving every other panel as it was.</summary>
    private void StopDesktopNotifyOverlay()
    {
        if (_desktopNotify is not null)
        {
            _desktopNotify.Clear();
            _desktopNotify.Close();
            _desktopNotify = null;
        }

        StopDriverIfNobodyWantsIt();
    }

    /// <summary>
    /// The Settings page's notification overlay card: saved, then acted on at once.
    /// </summary>
    /// <remarks>
    /// The corner follows even while it is switched off — the settings are the truth, and the
    /// window is put where they say when it next has something to show.
    /// </remarks>
    private void SetDesktopNotifyOverlay(DesktopNotifySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_state is null)
            return;

        var clamped = settings.Clamped();
        if (_state.Settings.DesktopNotifyOverlay == clamped)
            return;

        var wasOn = _state.Settings.DesktopNotifyOverlay.On;
        _state.Settings = _state.Settings with { DesktopNotifyOverlay = clamped };

        if (!CompanionSettings.SaveDesktopNotifyOverlay(_settingsPath, clamped))
            Log.Warning("The notification overlay's settings could not be saved to {Path}", _settingsPath);

        if (_popUps is not null)
            _popUps.Dwell = LongestPopUp();

        _desktopNotify?.Apply(clamped);

        if (wasOn != clamped.On)
            _desktopNotifySwitch?.Set(clamped.On);

        Render();
    }

    /// <summary>
    /// The longest any surface keeps a pop-up, which is how long one is kept at all.
    /// </summary>
    /// <remarks>
    /// There are two surfaces now — the headset's notification overlay and the one on a monitor —
    /// and each has its own number of seconds. Dropping a card at the shorter of them would take
    /// it off the other one early.
    /// </remarks>
    private TimeSpan LongestPopUp()
    {
        var headset = _state?.Settings.NotifyOverlay.Dwell ?? NotifyOverlaySettings.Default.Dwell;
        var desktop = _state?.Settings.DesktopNotifyOverlay.Dwell ?? DesktopNotifySettings.Default.Dwell;
        return headset > desktop ? headset : desktop;
    }

    /// <summary>
    /// The shortcut, pressed: the overlay comes up over the game, or goes away again.
    /// </summary>
    /// <remarks>
    /// It is shown beside the client's own window so it lands on the screen that window is on,
    /// which is the closest thing to "the monitor the moderator is using" that can be known
    /// without going looking at other programs' windows.
    /// </remarks>
    private void ToggleDesktopOverlay()
    {
        if (_desktopOverlay is null)
            return;

        _desktopOverlay.Press();
        Render();
    }

    /// <summary>
    /// The settings card's <strong>Open</strong> button. It is how the overlay is reached when the
    /// shortcut belongs to another program, which is why it is there.
    /// </summary>
    private void ShowDesktopOverlay()
    {
        if (_desktopOverlay is null)
            return;

        _desktopOverlay.Summon();
        Render();
    }

    /// <summary>
    /// The notification overlay card changed: saved as the whole <c>notifyOverlay</c> object, then
    /// acted on at once rather than at the next restart.
    /// </summary>
    /// <remarks>
    /// The placement follows even while the overlay is switched off — the settings are the truth,
    /// and the panel is put where they say when it next comes up. That is what lets a moderator
    /// arrange the pop-ups before turning them on.
    /// </remarks>
    private void SetNotifyOverlay(NotifyOverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_state is null)
            return;

        var clamped = settings.Clamped();
        if (_state.Settings.NotifyOverlay == clamped)
            return;

        var wasOn = _state.Settings.NotifyOverlay.On;
        _state.Settings = _state.Settings with { NotifyOverlay = clamped };

        if (!CompanionSettings.SaveNotifyOverlay(_settingsPath, clamped))
            Log.Warning("The notification overlay's settings could not be saved to {Path}", _settingsPath);

        if (_popUps is not null)
            _popUps.Dwell = LongestPopUp();

        _notifyHost?.Place(clamped.ToPlacement());

        if (wasOn != clamped.On)
            _notifySwitch?.Set(clamped.On);

        Render();
    }

    /// <summary>
    /// One turn of the overlay loop, never overlapping itself.
    /// </summary>
    /// <remarks>
    /// A turn holds a long poll open across many timer ticks, so without this guard the timer
    /// would stack requests on a machine that is also running a game.
    /// </remarks>
    /// <summary>How often the companion looks for a SteamVR that was not running last time.</summary>
    private static readonly TimeSpan OverlayAttachInterval = TimeSpan.FromSeconds(10);

    private DateTimeOffset _overlayAttachTriedAt = DateTimeOffset.MinValue;

    private DateTimeOffset _notifyAttachTriedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Attaches to SteamVR if it is running, and says so once. Never launches it: a program that
    /// starts with the computer must not start SteamVR too.
    /// </summary>
    private void AttachOverlay()
    {
        if (_overlayHost is null)
            return;

        _overlayAttachTriedAt = _clock.UtcNow;
        var before = _overlayHost.Status;
        var status = _overlayHost.Start();

        if (status.State == before.State && status.Detail == before.Detail)
            return;

        switch (status.State)
        {
            case OverlayRuntimeState.Running:
                _overlayAttachedAt = _clock.UtcNow;
                Log.Information("The overlay is attached: {Detail}", status.Detail);
                break;
            case OverlayRuntimeState.NoRuntime:
                Log.Information("No VR runtime on this machine, so no overlay: {Detail}", status.Detail);
                break;
            case OverlayRuntimeState.Refused:
                Log.Warning("The VR runtime refused the overlay: {Detail}", status.Detail);
                break;
            default:
                Log.Information("No VR runtime is running; the overlay will attach when one is: {Detail}", status.Detail);
                break;
        }
    }

    /// <summary>
    /// Attaches the notification overlay if a VR runtime is running, and says so once. Never
    /// launches one, for the same reason the main panel does not.
    /// </summary>
    private void AttachNotifyOverlay()
    {
        if (_notifyHost is null)
            return;

        _notifyAttachTriedAt = _clock.UtcNow;
        var before = _notifyHost.Status;
        var status = _notifyHost.Start();

        if (status.State == before.State && status.Detail == before.Detail)
            return;

        if (status.State is OverlayRuntimeState.Running)
        {
            _notifyAttachedAt = _clock.UtcNow;
            Log.Information("The notification overlay is attached: {Detail}", status.Detail);
        }
        else
        {
            Log.Information("The notification overlay is not showing: {Detail}", status.Detail);
        }
    }

    private async Task OverlayTickAsync()
    {
        if (_overlay is null || _overlayTicking)
            return;

        _overlayTicking = true;
        try
        {
            // The headset panels only, and only when they were built: a moderator running the
            // window over VRChat alone has no runtime to poll and no panel to attach.
            //
            // A VR runtime closing detaches the overlay; one started since the last look is picked
            // up here, a few seconds after the moderator starts it.
            if (_overlayHost is not null)
            {
                var wasRunning = _overlayHost.Status.State is OverlayRuntimeState.Running;
                _overlayHost.Poll();
                if (wasRunning && _overlayHost.Status.State is not OverlayRuntimeState.Running)
                {
                    _overlayAttachedAt = null;
                    Log.Information("The VR runtime closed; the overlay has let go and will attach again when it is back: {Detail}", _overlayHost.Status.Detail);
                }

                if (_overlayHost.Status.State is OverlayRuntimeState.NotStarted
                    && _clock.UtcNow - _overlayAttachTriedAt >= OverlayAttachInterval)
                {
                    AttachOverlay();
                }
            }

            if (_notifyHost is not null)
            {
                var wasRunning = _notifyHost.Status.State is OverlayRuntimeState.Running;
                _notifyHost.Poll();
                if (wasRunning && _notifyHost.Status.State is not OverlayRuntimeState.Running)
                    _notifyAttachedAt = null;

                if (_notifyHost.Status.State is OverlayRuntimeState.NotStarted
                    && _clock.UtcNow - _notifyAttachTriedAt >= OverlayAttachInterval)
                {
                    AttachNotifyOverlay();
                }
            }

            // The instance the log reader last understood. The overlay follows the moderator: the
            // server that manages this instance is the only one it reads from or speaks for.
            _overlay.EnteredInstance(CurrentInstance);
            await _overlay.TickAsync();

            // The pop-ups, after the tick that may have made one: what is still within its time,
            // newest first. Empty draws nothing at all. Each surface asks for its own number of
            // seconds, because a moderator can want one to linger and the other to be brief.
            if (_popUps is not null)
            {
                if (_notifyHost is not null)
                {
                    _notifyHost.Update(new NotificationScreen(
                        _popUps.Current(_state?.Settings.NotifyOverlay.Dwell ?? NotifyOverlaySettings.Default.Dwell)));
                }

                if (_desktopNotify is not null)
                {
                    _desktopNotify.Update(new NotificationScreen(
                        _popUps.Current(_state?.Settings.DesktopNotifyOverlay.Dwell ?? DesktopNotifySettings.Default.Dwell)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _overlayTicking = false;
        }
    }

    /// <summary>
    /// Where the moderator is, as far as the log has said.
    /// </summary>
    /// <remarks>
    /// <para>Read from the engine rather than pushed into it, so there is exactly one answer and
    /// one place that decides it. Null means "not known", which covers VRChat not running, VRChat
    /// having stopped writing and being presumed gone, and the moderator standing in a public,
    /// friends-only or private instance — all of which correctly produce the idle screen and no
    /// contact with any server.</para>
    /// <para>Nothing is asked of a server to obtain it. It is the same parse of the same log lines
    /// the reporting half already made.</para>
    /// </remarks>
    public InstanceLocation? CurrentInstance => _engine?.CurrentInstance;

    /// <summary>
    /// The readable name of the world the moderator is standing in, or null while it is not known.
    /// </summary>
    /// <remarks>
    /// Read from the same log lines the reporting half already reads, and used for one thing: a
    /// saved clip is named after the world so the right one can be picked out of a folder
    /// afterwards. Nothing is asked of a server to obtain it and nothing is decided by it.
    /// </remarks>
    public string? CurrentWorldName => _engine?.CurrentWorldName;

    /// <summary>
    /// The tray icon, which is present for the whole life of the process.
    /// </summary>
    /// <remarks>
    /// Not decoration. Silent, windowless, tray-less background software is scored as hostile by
    /// antivirus heuristics — correctly — and is also precisely the thing a moderator is being
    /// asked to trust this program not to be. The same decision satisfies both, which is a good
    /// sign that neither is theatre.
    /// </remarks>
    private void InstallTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open Modbot");
        open.Click += (_, _) => ShowWindow();

        var quit = new NativeMenuItem("Quit — stops reporting");
        quit.Click += (_, _) =>
        {
            StopEverything();
            desktop.Shutdown();
        };

        _tray = new TrayIcon
        {
            Icon = Brand.Icon(),
            ToolTipText = "Modbot: reporting presence for your groups",
            IsVisible = true,
            Menu = [open, quit],
        };

        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    /// <summary>
    /// Stops every loop this client runs and lets go of everything it holds.
    /// </summary>
    /// <remarks>
    /// Quitting really does stop reporting: the log stops being read at these lines, not when the
    /// process eventually exits. The Settings page's Restart uses the same stop, in the same order,
    /// so a restart and a quit leave the machine in the same state — the difference is only that a
    /// fresh copy is already waiting to take over.
    /// </remarks>
    private void StopEverything()
    {
        _engineLoop.Stop();
        _overlayLoop.Stop();
        _inputLoop.Stop();
        _voiceLoop.Stop();
        _updates?.Stop();
        _inboxStop.Cancel();
        _backupStop.Cancel();
        _overlay?.Dispose();
        _overlayHost?.Dispose();
        _notifyHost?.Dispose();

        // Gives the keyboard shortcut back to Windows rather than leaving it claimed by a process
        // that is going away.
        _desktopOverlayShortcut?.Dispose();
        _desktopNotify?.Clear();
        _voice?.Dispose();

        // Stops the recording and deletes the two rolling files: a client that is going away must
        // not leave minutes of somebody's screen behind on the disk.
        _recorder?.Dispose();
        _recorder = null;

        // Closes the microphone. A client that is going away must not leave one open, and a client
        // that has quit must never be the reason a moderator's microphone is busy.
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>
    /// The Settings page's <strong>Restart Modbot Companion</strong>.
    /// </summary>
    /// <remarks>
    /// <para>The fresh copy is started first, because once this one has stopped there is nothing
    /// left to start it. It is started the way pairing links start this program: by asking Windows
    /// to open Modbot's own <c>modbot-companion://</c> address, which Windows answers from the
    /// registration this client wrote for itself. The client launches nothing and inspects nothing
    /// (<see cref="CompanionRestart"/>).</para>
    /// <para>A machine where that address has no handler — the registry refused the registration —
    /// gets no restart and keeps the client it has: nothing is stopped, and the card says so.</para>
    /// </remarks>
    /// <returns>True once a fresh copy has been asked for and this one is shutting down.</returns>
    private async Task<bool> RestartAsync()
    {
        var started = await Window.Launcher.LaunchUriAsync(new Uri(CompanionRestart.Link));
        if (!started)
        {
            Log.Warning("Could not start another copy of Modbot; this one carries on reporting");
            return false;
        }

        Log.Information("Restarting: a fresh copy has been asked for and is waiting for this one to go");
        StopEverything();
        _desktop?.Shutdown();
        return true;
    }

    /// <summary>
    /// Starts asking the release feed for newer versions, unless the moderator has turned that
    /// off. What arrives is downloaded and announced in the window; it is installed at the next
    /// start and never underneath a running session.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (_state?.Settings.CheckForUpdates is not true)
        {
            Log.Information("Update checks are turned off in settings.json; this companion will not look for newer versions");
            return;
        }

        _updates = new Updates(_state);
        _updates.Start();
    }

    /// <summary>
    /// Makes this the copy that browser links reach.
    /// </summary>
    /// <remarks>
    /// Two steps. The registry key tells Windows that <c>modbot-companion://</c> opens this
    /// executable; the inbox is where the copy Windows then starts drops the link before exiting.
    /// Either can fail on a locked-down machine, and neither failing stops the client doing its
    /// job — the moderator pairs by pasting the token instead.
    /// </remarks>
    private void ListenForLinks()
    {
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { Length: > 0 } executable)
            UrlSchemeRegistration.Register(executable);

        _ = new PairingLinkInbox().ListenAsync(
            message => Dispatcher.UIThread.InvokeAsync(() => CrashGuard.RunAsync("handling a pairing link", () => HandleMessageAsync(message))),
            _inboxStop.Token);
    }

    /// <summary>
    /// What arrives from a second copy of this program, or from this one's own command line: a
    /// pairing link, or a request to show the window.
    /// </summary>
    /// <remarks>
    /// Anything that is not the show command is treated as a pairing link and checked as strictly
    /// as a pasted token — it came from a browser, through Windows, and none of that is trusted.
    /// </remarks>
    private async Task HandleMessageAsync(string message)
    {
        ShowWindow();

        if (string.Equals(message, ShowCommand, StringComparison.Ordinal))
            return;

        await PairAsync(message);
    }

    private void ShowWindow()
    {
        Window.Show();
        Window.WindowState = WindowState.Normal;
        Window.Activate();
    }

    private void Render()
    {
        if (_state is null)
            return;

        if (_engine is not null)
            _state.LogHealth = _engine.LogHealth;

        _state.Overlay = DescribeOverlay();
        _state.NotifyOverlay = DescribeNotifyOverlay();
        _state.LiveWords = _overlay?.LiveWords() ?? _state.LiveWords;

        // Started and stopped here rather than on its own timer: the rule is "while VRChat is
        // running", and this is the place that already knows, once a second.
        CrashGuard.Run("keeping the last few minutes", ApplyClips);

        // Same place and same rule: the microphone is open only while VRChat is running, and this
        // is the loop that already knows whether it is.
        CrashGuard.Run("listening for a phrase", ApplyListening);

        _state.SoundProblem = _bleep?.LastProblem;

        if (_voice is not null)
            _state.Voice = _voice.Status();

        if (_credits is not null)
            _state.Credits = _credits.Current;

        if (_overlayHost is not null)
            _preview?.Refresh(_overlayHost);

        _state.DesktopOverlay = new DesktopOverlayStatus(
            _state.Settings.DesktopOverlay,
            _desktopOverlayShortcut?.State ?? ShortcutState.Off,
            _desktopOverlay?.IsVisible is true);

        var snapshot = _state.Snapshot();

        Window.Render(
            snapshot,
            new MainWindowActions(
                TogglePause, Unpair, PairAsync, OpenPairingPageAsync, SetStartWithWindows, SetLogFolder,
                AttachSteamVr, ShowOverlayWindow, PinOverlaySample, PlaceOverlay, AnchorOverlay, SetVoice, TestVoice)
            {
                SetEventsFilters = SetEventsFilters,
                SetOverlayOn = SetOverlayOn,
                SetNotifications = SetNotifications,
                SetNotificationFilters = SetNotificationFilters,
                TestBleep = TestBleep,
                ClosedToTray = ClosedToTray,
                RestartAsync = RestartAsync,
                SetDesktopOverlay = SetDesktopOverlay,
                ShowDesktopOverlay = ShowDesktopOverlay,
                SetNotifyOverlay = SetNotifyOverlay,
                SetDesktopNotifyOverlay = SetDesktopNotifyOverlay,
                SetClips = SetClips,
                SaveClip = SaveClip,
                SetListening = SetListening,
            });
    }

    /// <summary>The notification overlay in the window's words.</summary>
    private NotifyOverlayStatus DescribeNotifyOverlay()
    {
        var popUps = _popUps?.Current().Count ?? 0;

        if (_notifyHost is null)
            return NotifyOverlayStatus.None with { PopUps = popUps };

        if (_notifyHost.FramesDrawn != _notifyFramesSeen)
        {
            _notifyFramesSeen = _notifyHost.FramesDrawn;
            _notifyLastDrewAt = _clock.UtcNow;
        }

        var status = _notifyHost.Status;

        return new NotifyOverlayStatus(
            status.State is OverlayRuntimeState.Running,
            status.State switch
            {
                OverlayRuntimeState.Running => "attached",
                OverlayRuntimeState.NoRuntime => "SteamVR not installed",
                OverlayRuntimeState.Refused => "refused",
                _ => "SteamVR not running",
            },
            status.Detail ?? "",
            _notifyAttachedAt,
            _notifyHost.FramesDrawn,
            _notifyLastDrewAt,
            popUps);
    }

    /// <summary>The overlay in the window's words: whether it is up, what it shows, how often it has drawn.</summary>
    private OverlayStatus DescribeOverlay()
    {
        // Off and "could not be set up" are different answers to "why is there no panel", and the
        // page says which. Both carry the saved placement, because the Placement card stays on
        // the page either way: a moderator arranges where the panel will sit and then turns it on.
        var saved = _state?.Settings.Overlay;

        if (_state?.Settings.OverlayOn is false)
            return OverlayStatus.Off with { Placement = saved };

        if (_overlayHost is null)
            return OverlayStatus.None with { Placement = saved };

        if (_overlayHost.FramesDrawn != _overlayFramesSeen)
        {
            _overlayFramesSeen = _overlayHost.FramesDrawn;
            _overlayLastDrewAt = _clock.UtcNow;
        }

        var status = _overlayHost.Status;
        var screen = _overlayHost.Showing;
        var roster = screen.Roster;

        return new OverlayStatus(
            status.State is OverlayRuntimeState.Running,
            status.State switch
            {
                OverlayRuntimeState.Running => "attached",
                OverlayRuntimeState.NoRuntime => "SteamVR not installed",
                OverlayRuntimeState.Refused => "refused",
                _ => "SteamVR not running",
            },
            status.Detail ?? "",
            _overlayAttachedAt,
            _overlayHost.FramesDrawn,
            _overlayLastDrewAt,
            screen.GroupLabel ?? "Not in a group instance",
            roster.Value?.Members.Count ?? 0,
            roster.Describe(),
            screen.Alert is { } alert ? alert.DisplayName ?? alert.SubjectId : null,
            screen.Health,
            _overlay?.CurrentServer?.GroupLabel,
            _pinnedSample is { } sample ? OverlaySamples.Name(sample) : null,
            _overlayHost.Placement,
            _overlayHost.Holding switch
            {
                Modbot.Overlay.Interaction.Hand.Left => "left hand",
                Modbot.Overlay.Interaction.Hand.Right => "right hand",
                _ => null,
            });
    }

    /// <summary>
    /// The SteamVR page moving the panel: a size, an opacity, a curve, or back in front of the
    /// head.
    /// </summary>
    /// <remarks>
    /// It works with the overlay switched off, and that is deliberate: a moderator setting up for
    /// the first time arranges where the panel will sit and then turns it on, rather than the
    /// other way round. With nothing running there is no host to tell, so the change goes straight
    /// to settings and the panel is put there when it next comes up.
    /// </remarks>
    private void PlaceOverlay(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (_overlayHost is not null)
        {
            // The host raises PlacementChanged, which is what saves it.
            _overlayHost.Place(placement);
        }
        else
        {
            RememberPlacement(placement);
        }

        Render();
    }

    /// <summary>The SteamVR page fixing the panel to the head, a hand or the room.</summary>
    private void AnchorOverlay(OverlayAnchor anchor)
    {
        if (_overlayHost is not null)
        {
            _overlayHost.Anchor(anchor);
        }
        else if (_state is not null)
        {
            // With nothing running there is no head and no hand to put it in front of, so the
            // offset is the one that anchor starts at.
            var current = _state.Settings.Overlay;
            RememberPlacement(current with
            {
                Anchor = anchor,
                Offset = anchor switch
                {
                    OverlayAnchor.LeftHand or OverlayAnchor.RightHand => OverlayPlacement.HandOffset,
                    OverlayAnchor.World => current.Offset,
                    _ => OverlayPlacement.Default.Offset,
                },
            });
        }

        Render();
    }

    /// <summary>Keeps a placement made with no panel running, and saves it a moment later.</summary>
    private void RememberPlacement(OverlayPlacement placement)
    {
        if (_state is null)
            return;

        _state.Settings = _state.Settings with { Overlay = placement.Clamped() };
        WireOverlayLoops();
        _placementSave.Stop();
        _placementSave.Start();
    }

    /// <summary>The Debug page's "Attach to SteamVR now", and the SteamVR page's.</summary>
    private void AttachSteamVr()
    {
        AttachOverlay();
        Render();
    }

    /// <summary>Opens, or brings back, the window that shows the overlay's last frame.</summary>
    private void ShowOverlayWindow()
    {
        if (_overlayHost is null)
            return;

        if (_preview is null)
        {
            _preview = new OverlayPreviewWindow();
            _preview.Closed += (_, _) => _preview = null;
            _preview.Refresh(_overlayHost);
            _preview.Show();
        }

        _preview.Activate();
    }

    /// <summary>Pins a sample screen into the overlay, or, with null, lets the live screen back.</summary>
    private void PinOverlaySample(OverlaySample? sample)
    {
        if (_overlayHost is null)
            return;

        _pinnedSample = sample;
        _overlayHost.Pinned = sample is { } chosen ? OverlaySamples.Build(chosen) : null;
        Render();
    }

    private void TogglePause(string serverId)
    {
        if (_state?.Connections.FirstOrDefault(c => c.ServerId == serverId) is not { } connection)
            return;

        // Immediate. Pausing stops Modbot observing what you do from now on; it does not save it
        // up to report when you resume.
        connection.IsPaused = !connection.IsPaused;
        Render();
    }

    private void Unpair(string serverId)
    {
        if (_state is null || _pairing is null)
            return;

        _pairing.Unpair(serverId);
        Disconnect(serverId);
        _state.UnusablePairings.RemoveAll(p => p.ServerId == serverId);
        Render();
    }

    /// <summary>
    /// Takes one server out of the reporting loop: its connection, its queue file and its place in
    /// the overlay all go together, so nothing of that group's data is left behind.
    /// </summary>
    private void Disconnect(string serverId)
    {
        if (_state is null)
            return;

        _overlay?.Remove(serverId);

        if (_state.Connections.FirstOrDefault(c => c.ServerId == serverId) is { } connection)
        {
            _state.Connections.Remove(connection);

            if (_engine is not null && _buffers.TryGetValue(serverId, out var buffer))
                _engine.Remove(connection, buffer);

            _buffers.Remove(serverId);
        }
    }

    /// <summary>
    /// Pairs from a pairing link or a pasted token, and says how it went in the pairing card.
    /// </summary>
    /// <remarks>
    /// The same path whichever way the token arrived, and one request per token — a link is
    /// single-use, so nothing here retries. Pairing a server this client already has replaces the
    /// old pairing rather than adding a second one: the new token is the one the operator just
    /// issued, and two connections to one server would report everything twice.
    /// </remarks>
    private async Task<PairingAttemptResult> PairAsync(string linkOrToken)
    {
        if (_pairing is null || _state is null)
            return new PairingAttemptResult(false, "Not ready yet.");

        _state.LastPairing = new PairingNotice(PairingNoticeKind.Working, "Pairing…");
        Render();

        var result = await _pairing.PairAsync(linkOrToken);

        // A server paired mid-session starts being reported to and becomes visible to the overlay
        // immediately, so a moderator who pairs while already standing in that group's instance
        // does not have to restart to be covered or to see its roster.
        if (result is { Succeeded: true, Pairing: { } pairing })
        {
            Disconnect(pairing.ServerId);
            _state.UnusablePairings.RemoveAll(p => p.ServerId == pairing.ServerId);
            Connect(pairing);
            _overlay?.Add(pairing, pairing.OverlayLabel);
        }

        _state.LastPairing = new PairingNotice(
            result.Succeeded ? PairingNoticeKind.Succeeded : PairingNoticeKind.Failed,
            result.Message);
        Render();

        return result;
    }

    /// <summary>
    /// Opens the pairing page in the moderator's browser.
    /// </summary>
    /// <remarks>
    /// Through the UI toolkit's own "open this link" facility, which hands the address to Windows
    /// the same way clicking a link in any program does. The client does not start, inspect or
    /// attach to any process itself. If Windows cannot open a browser, the address is shown so the
    /// moderator can type it.
    /// </remarks>
    private async Task OpenPairingPageAsync()
    {
        if (_state is null)
            return;

        var page = _state.Settings.PairingPage;
        var opened = await Window.Launcher.LaunchUriAsync(page);

        _state.LastPairing = opened
            ? new PairingNotice(
                PairingNoticeKind.Working,
                "Your browser is open. Sign in there, press \"Open in Modbot\", and come back here.")
            : new PairingNotice(
                PairingNoticeKind.Failed,
                $"Could not open your browser. Open {page} yourself, sign in, and press \"Open in Modbot\" "
                + "— or copy the pairing token from that page and paste it below.");
        Render();
    }
}

internal static class Program
{
    /// <summary>
    /// Keeps the client to one running copy per Windows account. Named under <c>Local\</c> so two
    /// people signed in to the same PC each get their own.
    /// </summary>
    private const string SingleInstanceName = @"Local\Modbot.Companion";

    [STAThread]
    public static int Main(string[] args)
    {
        // First, before the log or the single-instance check: the installer starts this exe with
        // a flag while installing, updating or uninstalling, and this does what the flag asks and
        // exits. An ordinary start comes straight back.
        Updates.RunInstallerHooks(args);

        // Windows starts a fresh copy of this program to deliver a modbot-companion:// link. The
        // link is the first argument that looks like one; anything else on the command line is
        // Avalonia's business.
        var link = args.FirstOrDefault(PairingToken.LooksLikeLink);

        // One of those links is not a pairing link: it is this program asking Windows to start a
        // fresh copy of itself for the Settings page's Restart button. That copy waits for the one
        // that is quitting instead of handing it anything (CompanionRestart).
        var restarting = CompanionRestart.IsRestartLink(link);
        if (restarting)
            link = null;

        var startHidden = StartWithWindows.StartsHidden(args);

        CompanionLog.Start(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        CrashGuard.Install();

        using var single = new Mutex(initiallyOwned: true, SingleInstanceName, out var firstCopy);

        // Started to replace a copy that is on its way out: wait for it to go rather than handing
        // it a message and leaving. Its exit frees both the single-copy lock and the pairing link
        // inbox's pipe, and this copy needs both.
        if (!firstCopy && restarting)
        {
            Log.Information("Started to replace the copy that is quitting; waiting for it to go");
            firstCopy = CompanionRestart.WaitForTheOldCopyToGo(single);

            if (!firstCopy)
                Log.Warning("The copy that was quitting is still running; leaving it alone");
        }

        if (!firstCopy)
        {
            // Windows starting a second copy at sign-in wants nothing from the first one.
            if (startHidden && link is null)
                return 0;

            Log.Information("Another copy of the companion is running; handing it {What} and leaving",
                link is null ? "a request to show its window" : "the pairing link");
            // Another copy owns the tray icon and the log. Hand it the link -- or, with no link,
            // ask it to show its window, which is what somebody double-clicking the icon again
            // wanted -- and leave. A few tries, because the other copy may still be starting.
            var message = link ?? CompanionHost.ShowCommand;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (PairingLinkInbox.TrySendAsync(message, timeout: TimeSpan.FromSeconds(1)).GetAwaiter().GetResult())
                    break;
            }

            return 0;
        }

        // Only the copy that will own the tray icon installs a waiting update, and only here,
        // before the window exists: nothing is being recorded yet, so the restart costs nothing,
        // and a second copy started by a browser link never swaps the files under the first.
        Updates.InstallDownloadedUpdate(args);

        ModbotCompanionApp.StartupMessage = link;
        ModbotCompanionApp.StartHidden = startHidden && link is null;

        try
        {
            OverlayHost.ConfigureAvalonia<ModbotCompanionApp>()
                .UsePlatformDetect()
                .ConfigureFonts(Brand.RegisterFonts)
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Reported here, once, rather than rethrown into the runtime's own crash dialog: the
            // moderator gets one box that names the log file, not two that name nothing.
            CrashGuard.Report(ex, "while starting", fatal: true);
            return 1;
        }
        finally
        {
            Log.Information("Modbot Companion exiting");
            CompanionLog.Stop();
        }

        return 0;
    }
}
