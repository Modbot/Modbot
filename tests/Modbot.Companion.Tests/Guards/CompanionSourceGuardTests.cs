using System.Reflection;
using System.Text.RegularExpressions;

namespace Modbot.Companion.Tests.Guards;

/// <summary>
/// Guards that scan the client's own source.
/// </summary>
/// <remarks>
/// These cannot be unit tests, because the things they forbid all compile perfectly well. They are
/// the enforcement behind two promises: that the client has one clock, and that its source is
/// honest about what it does with a moderator's machine.
/// </remarks>
public class CompanionSourceGuardTests
{
    private static readonly Regex SystemClockUse = new(
        @"\bDateTime(Offset)?\s*\.\s*(UtcNow|Now|Today)\b",
        RegexOptions.Compiled);

    /// <summary>Anything that reads the disk, or puts bytes on the network -- sockets included.</summary>
    private static readonly Regex TouchesTheOutsideWorld = new(
        @"\b(File|Directory|FileStream|StreamReader|StreamWriter|HttpClient|HttpRequestMessage|HttpMessageHandler|ClientWebSocket)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The plain-language disclosure a suspicious reader is owed: what is read, what is written, and
    /// what leaves the machine.
    /// </summary>
    private static readonly Regex Discloses = new(
        @"leaves the machine|transmit|is sent|sends|What this reads|written to your disk|never stored",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The engine: the half that reads the log and reports.
    /// </summary>
    private static IEnumerable<string> ClientSources() => SourcesUnder("Modbot.Companion");

    /// <summary>
    /// The engine <em>and</em> the window.
    /// </summary>
    /// <remarks>
    /// The bans below cover both, because a UI project is exactly where somebody would add "attach
    /// a screenshot" with entirely good intentions. Everything else here is about the engine,
    /// which is deliberately kept small enough that its claims stay checkable.
    /// </remarks>
    private static IEnumerable<string> EverythingTheClientShips()
        => SourcesUnder("Modbot.Companion").Concat(SourcesUnder("Modbot.Companion.App"));

    private static IEnumerable<string> SourcesUnder(string project)
    {
        var root = Path.Combine(FindRepoRoot(), "src", project);

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void NoFileInTheClientReadsTheSystemClock()
    {
        // The client is the reason IModbotClock exists: its timestamps come from machines Modbot
        // does not control, and a PC ten minutes out would silently corrupt every session duration
        // rather than fail. Foundation 4.4.
        var offenders = ClientSources()
            .Where(f => SystemClockUse.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These client files read the system clock instead of IModbotClock: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryFileThatTouchesDiskOrNetworkSaysWhatItDoes()
    {
        // Foundation 3.2: every function that reads from disk, captures data or transmits it
        // carries a plain-language comment saying what it reads, why, what leaves the machine and
        // what does not -- written for a suspicious moderator, not for a compiler. This checks the
        // shape of that promise, not its prose; a reviewer still has to read the words.
        var offenders = new List<string>();

        foreach (var file in ClientSources())
        {
            var source = File.ReadAllText(file);
            if (!TouchesTheOutsideWorld.IsMatch(source))
                continue;

            if (!source.Contains("<remarks>", StringComparison.Ordinal) || !Discloses.IsMatch(source))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            "These client files read or send data without saying so in plain language: "
            + string.Join(", ", offenders.Order()));
    }

    [Theory]
    [InlineData("System.Diagnostics.Process", "the client does not attach to, inspect or launch processes itself -- the installer's own updater is started by Velopack, from Updates.cs only (see TheOnlyFileThatTalksToTheInstallerIsUpdatesCs)")]
    [InlineData("localconfig.vdf", "the client does not read Steam's configuration -- M3 2.3.1")]
    [InlineData("GetAsyncKeyState", "the client does not read the keyboard")]
    [InlineData("Clipboard", "the client does not read the clipboard")]
    public void TheClientDoesNotDoTheThingsThatWouldMakeItAnActualInfostealer(string forbidden, string why)
    {
        // The client is, feature for feature, shaped like spyware: it runs unattended on a personal
        // PC, watches a file, and posts what it sees to a server. What separates it is that its
        // behaviour is bounded and checkable. These are the bounds, as a test.
        var offenders = EverythingTheClientShips()
            .Where(f => File.ReadAllText(f).Contains(forbidden, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"{why}; found in {string.Join(", ", offenders)}");
    }

    /// <summary>The two files allowed to touch the registry, and the keys each may touch.</summary>
    private const string SchemeRegistrationFile = "UrlSchemeRegistration.cs";

    private const string SchemeRegistrationKey = @"Software\Classes\";

    private const string StartupRegistrationFile = "StartupRegistration.cs";

    [Fact]
    public void TheOnlyRegistryKeysTheClientTouchesAreItsLinkRegistrationAndItsStartupEntry()
    {
        // Pairing starts in the browser, and a browser can only hand a modbot-companion:// link to
        // this program if Windows has been told the scheme is ours: one key under Software\Classes.
        // Starting with Windows is one value in the current user's own Run key, plus reading
        // Task Manager's record of whether the user turned it off. That is the whole of what the
        // client does with the registry: it does not read Steam's keys, VRChat's, or anybody else's.
        //
        // The ban used to be total, then narrowed to the link registration; it is widened by exactly
        // one file for the startup entry, so neither file can quietly grow a second purpose.
        var touching = EverythingTheClientShips()
            .Where(f => File.ReadAllText(f).Contains("Microsoft.Win32.Registry", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal([StartupRegistrationFile, SchemeRegistrationFile], touching);

        var registration = File.ReadAllText(EverythingTheClientShips()
            .Single(f => Path.GetFileName(f) == SchemeRegistrationFile));

        Assert.Contains(SchemeRegistrationKey, registration, StringComparison.Ordinal);

        var startup = File.ReadAllText(EverythingTheClientShips()
            .Single(f => Path.GetFileName(f) == StartupRegistrationFile));

        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Run", startup, StringComparison.Ordinal);

        // Task Manager's record is read and never written: the one write-capable call in that file
        // opens the Run key, not StartupApproved.
        Assert.DoesNotContain("ApprovedKeyPath, writable", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSubKey(ApprovedKeyPath", startup, StringComparison.Ordinal);

        foreach (var source in new[] { registration, startup })
        {
            Assert.DoesNotContain("LocalMachine", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HKEY_LOCAL_MACHINE", source, StringComparison.Ordinal);
        }
    }

    /// <summary>The one file allowed to talk to the installer and updater.</summary>
    private const string UpdatesFile = "Updates.cs";

    [Fact]
    public void TheOnlyFileThatTalksToTheInstallerIsUpdatesCs()
    {
        // The client ships inside Velopack's installer, and Velopack is how it learns about and
        // downloads newer versions of itself. That library is also the one thing in the client
        // that starts another program: its own Update.exe, from Modbot's install folder, to swap
        // the files while Modbot is not running. The ban on System.Diagnostics.Process above still
        // covers every file the client ships -- including this one -- so the client's own code
        // never launches anything; and the library that does is reachable from exactly one file,
        // which has to carry the plain-language disclosure of what it reads, downloads and never
        // does (M3 9.2: updates are visible, never forced, and can be turned off).
        var touching = EverythingTheClientShips()
            .Where(f => File.ReadAllText(f).Contains("Velopack", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal([UpdatesFile], touching);

        var updates = File.ReadAllText(EverythingTheClientShips().Single(f => Path.GetFileName(f) == UpdatesFile));

        Assert.Contains("<remarks>", updates, StringComparison.Ordinal);
        Assert.Matches(Discloses, updates);
        Assert.Contains("never restarts Modbot while Modbot is running", updates, StringComparison.Ordinal);
        Assert.Contains("SetAutoApplyOnStartup(false)", updates, StringComparison.Ordinal);
    }

    /// <summary>Anything that would capture what is on a screen, by any route.</summary>
    /// <remarks>
    /// The list grew on 2026-09-19 with the routes the clips recorder itself uses — the desktop
    /// duplication and the encoder — so that the way in which the ban was narrowed cannot be used a
    /// second time by a second file. Exactly one file is allowed to match this, and
    /// <see cref="TheOnlyFileThatCanRecordIsScreenRecordingCs"/> names it.
    /// </remarks>
    private static readonly Regex ScreenCapture = new(
        @"\b(CopyFromScreen|BitBlt|PrintWindow|GetDC|GraphicsCaptureItem|GraphicsCapturePicker"
        + @"|Direct3D11CaptureFrame|DwmGetWindow|RenderTargetBitmap|CaptureScreen|Screenshot"
        + @"|DuplicateOutput|IDXGIOutputDuplication|AcquireNextFrame|MFCreateSinkWriterFromURL"
        + @"|IMFSinkWriter|MFCreateSinkWriterFromMediaSink)\b",
        RegexOptions.Compiled);

    /// <summary>Anywhere a screenshot would be sitting on disk waiting to be read.</summary>
    /// <remarks>
    /// The Videos folder came out of this list on 2026-09-19 and got a rule of its own
    /// (<see cref="TheOnlyFileThatNamesYourVideosFolderIsClipsFolderCs"/>), because one file now
    /// writes clips there. Pictures, Documents, the Desktop and VRChat's own screenshot folder are
    /// still banned everywhere, that file included: writing a clip a moderator asked for is a
    /// different act from reading pictures they did not.
    /// </remarks>
    private static readonly Regex ScreenshotFolders = new(
        @"(SpecialFolder\s*\.\s*(MyPictures|MyDocuments|Desktop)"
        + @"|VRChat[\\/]{1,2}Screenshots|VRChat[\\/]{1,2}Pictures|""Screenshots"")",
        RegexOptions.Compiled);

    /// <summary>The moderator's own video folder, which one file writes saved clips into.</summary>
    private static readonly Regex VideosFolder = new(
        @"SpecialFolder\s*\.\s*MyVideos\b",
        RegexOptions.Compiled);

    /// <summary>The one file allowed to record a picture of a screen.</summary>
    private const string RecordingFile = "ScreenRecording.cs";

    /// <summary>The one file allowed to name the moderator's own Videos folder.</summary>
    private const string ClipsFolderFile = "ClipsFolder.cs";

    [Fact]
    public void TheGuardsAreActuallyLookingAtSomething()
    {
        // A scan over an empty file set passes every ban in this class while proving nothing, and
        // it would pass silently the day somebody renames a project directory.
        Assert.True(ClientSources().Count() > 20, "The engine's source files were not found.");
        Assert.True(EverythingTheClientShips().Count() > ClientSources().Count(),
            "The window's source files were not found.");

        // And the bans catch what they are aimed at, rather than being regexes that match nothing.
        Assert.Matches(ScreenCapture, "var bitmap = Graphics.CopyFromScreen(0, 0, 0, 0, size);");
        Assert.Matches(ScreenCapture, "var duplication = output1.DuplicateOutput(device);");
        Assert.Matches(ScreenshotFolders, @"Path.Combine(pictures, ""VRChat\Screenshots"")");
        Assert.Matches(ScreenshotFolders, "Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)");
        Assert.Matches(VideosFolder, "Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)");
        Assert.DoesNotMatch(ScreenshotFolders, "Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)");
        Assert.Matches(SoundCapture, "using var capture = new WasapiCapture(device);");
        Assert.Matches(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)");
        Assert.Matches(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)");
        Assert.Matches(SoundCapture, "var mic = capture.CaptureOpenDevice(null, 16000, BufferFormat.Mono16, 1024);");
        Assert.DoesNotMatch(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)");
    }

    [Fact]
    public void TheOnlyFileThatCanRecordIsScreenRecordingCs()
    {
        // This ban used to be total. M3 3.1.1 said screen capture was forbidden permanently, and
        // that was the right rule for a client that had no reason to want it. On 2026-09-19 a
        // moderator asked to be able to keep the last few minutes and save them when something
        // happens, and the rule was narrowed rather than dropped: the capability exists, in one
        // file, off unless a person switches it on, with nothing it records ever leaving the PC.
        // The clips design spec carries the whole argument.
        //
        // The shape of the narrowing matters as much as the narrowing. A capability in one named
        // file is answerable -- "show me where this program records" has a one-word answer. A
        // capability anywhere is not. So the ban stands everywhere else, this list of routes grew
        // to include the ones the new file uses, and a second file learning any of them fails the
        // build.
        var recording = EverythingTheClientShips()
            .Where(f => ScreenCapture.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal([RecordingFile], recording);

        var source = File.ReadAllText(
            EverythingTheClientShips().Single(f => Path.GetFileName(f) == RecordingFile));

        // The same plain-language disclosure every reading and sending file carries, and the two
        // claims that make this one bounded: no sound, and nothing sent.
        Assert.Contains("<remarks>", source, StringComparison.Ordinal);
        Assert.Matches(Discloses, source);
        Assert.Contains("No sound", source, StringComparison.Ordinal);
        Assert.Contains("What leaves the machine: nothing", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecorderIsOnlyEverBuiltInOnePlaceAndIsOffUntilItIsSwitchedOn()
    {
        // "What does this program start, and when" has to stay answerable now that one of the
        // answers is a screen recorder. One place builds it, that place is the same one that owns
        // every other switch, and the settings it reads default to off -- so an existing client
        // updated into a version that can record does not start recording.
        var building = EverythingTheClientShips()
            .Where(f => File.ReadAllText(f).Contains("new ScreenRecording(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["Program.cs"], building);

        var settings = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Modbot.Companion", "Clips", "ClipSettings.cs"));

        Assert.Contains("bool On = false", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingTheClientShipsGoesLookingForScreenshotsOnDisk()
    {
        // Reading a folder full of screenshots is the same disclosure as taking one, reached by a
        // different route, and it is the route somebody would take while believing they had
        // avoided the ban above.
        var offenders = EverythingTheClientShips()
            .Where(f => ScreenshotFolders.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The client reads VRChat's log directory and nothing else; found picture or screenshot "
            + "paths in " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheOnlyFileThatNamesYourVideosFolderIsClipsFolderCs()
    {
        // Saved clips go into the machine's own Videos folder, so exactly one file asks Windows
        // where that is -- to write into it, never to read what is already there. Pictures,
        // Documents, the Desktop and VRChat's own screenshot folder stay banned everywhere by the
        // rule above, this file included, because a folder a moderator asked Modbot to write into
        // is a different thing from a folder full of things they did not.
        var naming = EverythingTheClientShips()
            .Where(f => VideosFolder.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal([ClipsFolderFile], naming);
    }

    [Fact]
    public void OnlyTheEightDeclaredPlacesMakeOutboundRequests()
    {
        // "What does this program send, and where" should have a short, complete answer findable
        // by somebody who has never seen the codebase. Eight files, each with a remarks block
        // saying what it sends: one posts observations, one asks the time, one trades a pairing
        // code for a token, one reads the overlay's context, one holds the overlay's live
        // WebSocket open, one backs the client's events up to Modbot Cloud (cloud event backup
        // spec), one fetches the voice -- once, from one pinned address, with nothing attached --
        // and one reads the sponsors, early adopters and contributors the Credits page shows.
        // Nothing else reaches the network.
        //
        // Two of the eight are the overlay's: HttpOverlayReadClient and LiveSocket. Neither runs
        // while both overlays are switched off, because nothing then builds the driver that owns
        // them (TheOverlaysAreOnlyEverBuiltThroughTheirOnOffSwitches). The notification overlay is
        // fed by the same two, so it is reason enough on its own for them to be running -- which
        // is exactly what a moderator asking to be told about flagged arrivals has asked for.
        var senders = ClientSources()
            .Where(f => Regex.IsMatch(
                File.ReadAllText(f),
                @"_http\.(SendAsync|GetAsync|PostAsync|PutAsync|DeleteAsync)|new ClientWebSocket\("))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(
            [
                "CloudCredits.cs",
                "HttpCloudLogClient.cs",
                "HttpIngestTransport.cs",
                "HttpOverlayReadClient.cs",
                "HttpPairingClient.cs",
                "HttpServerTimeProbe.cs",
                "LiveSocket.cs",
                "VoiceDownload.cs",
            ],
            senders);
    }

    /// <summary>Anything that would open a microphone, a line-in or a loopback, by any route.</summary>
    private static readonly Regex SoundCapture = new(
        @"\b(WaveIn|WaveInEvent|WaveInProvider|WasapiCapture|WasapiLoopbackCapture|AudioCaptureClient|IAudioCaptureClient"
        + @"|CaptureOpenDevice|CaptureStart|CaptureSamples|CaptureStop|CaptureCloseDevice|alcCaptureOpenDevice"
        + @"|MediaCapture|AudioRecord|SoundRecorder|eCapture)\b"
        + @"|DataFlow\s*\.\s*(Capture|All)\b"
        + @"|Extensions\s*\.\s*EXT\s*\.\s*Capture\b",
        RegexOptions.Compiled);

    [Fact]
    public void NothingTheClientShipsCanRecordSound()
    {
        // The voice gave the client an audio library, and an audio library has a recording half.
        // The client plays; it never listens. Voice chat is in the never-recorded column beside
        // keystrokes and the process list (M3 10), and the way to keep it there is to make the
        // recording APIs fail the build rather than a review.
        //
        // This ban did NOT move when the screen one did. Clips are silent on purpose: what a
        // moderator asked for was to be able to show what happened, and a recording of everyone's
        // voice in the instance is a different and much larger thing to take off a PC. Keeping the
        // sound ban total is most of what keeps the clips one narrow.
        var offenders = EverythingTheClientShips()
            .Where(f => SoundCapture.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The client must never record sound; found capture APIs in " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheOnlyEndpointsAskedForAreOutputs()
    {
        // Belt and braces for the ban above: every place the Windows audio system is asked for
        // devices names the render direction explicitly, so nothing enumerates "all" and picks.
        var audio = EverythingTheClientShips()
            .Select(File.ReadAllText)
            .Where(source => source.Contains("MMDeviceEnumerator", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(audio);

        foreach (var source in audio)
        {
            foreach (Match ask in Regex.Matches(source, @"(EnumerateAudioEndPoints|GetDefaultAudioEndpoint|HasDefaultAudioEndpoint)\s*\(\s*DataFlow\s*\.\s*(\w+)"))
                Assert.Equal("Render", ask.Groups[2].Value);
        }
    }

    [Fact]
    public void TheOverlaysAreOnlyEverBuiltThroughTheirOnOffSwitches()
    {
        // "What does this program start, and when" should stay as answerable as "what does it
        // send". A panel connects to SteamVR and the driver behind it opens a live connection to a
        // paired server, so a moderator who switched an overlay off must not get either by some
        // second path that forgot to ask. There are three switches now -- the main headset panel,
        // the notification panel and the desktop overlay -- and one place builds each; only its own
        // switch calls that place, and the driver behind them is asked whether anything still
        // wants it rather than being stopped because one switch went off.
        var building = EverythingTheClientShips()
            .Where(f => File.ReadAllText(f).Contains("OverlayHost.Create", StringComparison.Ordinal)
                || File.ReadAllText(f).Contains("NotificationHost.Create", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["Program.cs"], building);

        var program = File.ReadAllText(EverythingTheClientShips().Single(f => Path.GetFileName(f) == "Program.cs"));

        Assert.Contains("new OverlaySwitch(StartOverlay, StopOverlay", program, StringComparison.Ordinal);
        Assert.Contains("new OverlaySwitch(StartNotifyOverlay, StopNotifyOverlay", program, StringComparison.Ordinal);

        foreach (var direct in new[] { "StartOverlay();", "StopOverlay();", "StartNotifyOverlay();", "StopNotifyOverlay();" })
            Assert.DoesNotContain(direct, program, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowHasNoModbotCloudSwitch()
    {
        // Where the event backup goes, and whether it is sent, is set in settings.json or the
        // environment on this PC (cloud event backup spec 3.1) -- never a control in the window.
        // The Events page is allowed to say that Modbot Cloud is one of the places an event went
        // (spec: "show every event the client handled"), so the guard looks for a toggle bound to
        // it rather than for the word itself.
        var window = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Modbot.Companion.App", "MainWindow.cs"));

        var toggles = window.Split('\n')
            .Where(line => line.Contains("CheckBox", StringComparison.Ordinal)
                || line.Contains("ToggleSwitch", StringComparison.Ordinal)
                || line.Contains("ToggleButton", StringComparison.Ordinal));

        Assert.DoesNotContain(toggles, line => line.Contains("Cloud", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheOnlyFilesReadAreVRChatsOwnLogs()
    {
        // One glob, in one place. Everything else on the disk is untouched.
        var patterns = ClientSources()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""\*?[\w\-]*\*[\w\-]*\.\w+""")
                .Select(m => m.Value))
            .Distinct()
            .ToList();

        Assert.Equal(["\"output_log_*.txt\""], patterns);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Modbot.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
