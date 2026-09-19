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

    /// <summary>Anything that asks Windows about another program's window.</summary>
    /// <remarks>
    /// Added on 2026-09-19, when a clip became VRChat's window rather than the whole monitor. The
    /// recorder has to be told where VRChat is drawn and whether it is the window in front, which
    /// means asking Windows about a window that is not Modbot's. That is one named ask for one
    /// named window; the client still never enumerates windows and never enumerates processes, and
    /// exactly one file is allowed to match this.
    /// </remarks>
    private static readonly Regex AsksAboutAnotherWindow = new(
        @"\b(FindWindowW|FindWindowExW|EnumWindows|EnumChildWindows|GetForegroundWindow|GetClientRect"
        + @"|GetWindowRect|ClientToScreen|IsIconic|GetWindowThreadProcessId|WindowFromPoint)\b",
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
        Assert.Matches(AsksAboutAnotherWindow, "var handle = FindWindowW(null, \"VRChat\");");
        Assert.Matches(AsksAboutAnotherWindow, "EnumWindows(callback, IntPtr.Zero);");
        Assert.DoesNotMatch(AsksAboutAnotherWindow, "var placement = Window.Position;");
        Assert.DoesNotMatch(ScreenshotFolders, "Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)");
        Assert.Matches(SoundCapture, "using var capture = new WasapiCapture(device);");
        Assert.Matches(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)");
        Assert.Matches(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)");
        Assert.Matches(SoundCapture, "var mic = capture.CaptureOpenDevice(null, 16000, BufferFormat.Mono16, 1024);");
        Assert.Matches(SoundCapture, "var recorder = new WasapiRecorderBuilder().WithSharedMode().Build();");
        Assert.Matches(SoundCapture, "var matcher = new KeywordSpotter(config);");
        Assert.Matches(SoundCapture, "stream.AcceptWaveform(16000, samples);");
        Assert.DoesNotMatch(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)");
        Assert.DoesNotMatch(SoundCapture, "await player.PlayAsync(clip, device, cancellationToken);");
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
    public void RecordingIsWindowsOnlyAndSaysSoRatherThanFailingQuietly()
    {
        // Linux was looked at again on 2026-09-19 and left alone, and the reasoning is in the clips
        // design spec: capturing one window is possible under X11 and under the Wayland portal, but
        // there is no encoder a client that may not start a process can reach, and a recorder that
        // wrote corrupt files would be worse than an honest "only on Windows".
        //
        // So the answer has to stay one plain sentence rather than a silent nothing, and the
        // decision has to stay in the one place that can make it.
        var source = File.ReadAllText(
            EverythingTheClientShips().Single(f => Path.GetFileName(f) == RecordingFile));

        Assert.Contains("Supported => OperatingSystem.IsWindows()", source, StringComparison.Ordinal);
        Assert.Contains("only works on Windows", source, StringComparison.Ordinal);

        // And nothing anywhere in the client runs another program to do the encoding, which is the
        // route a Linux recorder would have had to take. The Process ban above is the whole of it;
        // this names the two libraries somebody would reach for instead.
        foreach (var forbidden in new[] { "ffmpeg", "libavcodec" })
        {
            var offenders = EverythingTheClientShips()
                .Where(f => File.ReadAllText(f).Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Order()
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"The client does not ship or drive an encoder of its own; found {forbidden} in "
                + string.Join(", ", offenders));
        }
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
    public void TheOnlyFileThatAsksWindowsAboutVRChatsWindowIsScreenRecordingCs()
    {
        // A clip is VRChat's window, so the recorder has to be told where that window is drawn and
        // whether it is the one in front. That is a real capability -- knowing something about a
        // program that is not Modbot -- and it is held to the same shape as the recording itself:
        // one named file, asking for one named window.
        //
        // What is still banned everywhere, this file included, is a *list*: the client does not
        // enumerate windows and does not enumerate processes (the System.Diagnostics.Process ban
        // above). "What does this program know about the rest of your PC" therefore still has a
        // short answer -- VRChat's window, its size, and whether you are looking at it.
        var asking = EverythingTheClientShips()
            .Where(f => AsksAboutAnotherWindow.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal([RecordingFile], asking);

        var source = File.ReadAllText(
            EverythingTheClientShips().Single(f => Path.GetFileName(f) == RecordingFile));

        // It looks for VRChat by name, rather than walking what else is open.
        Assert.Contains("\"VRChat\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumWindows", source, StringComparison.Ordinal);
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
    public void OnlyTheNineDeclaredPlacesMakeOutboundRequests()
    {
        // "What does this program send, and where" should have a short, complete answer findable
        // by somebody who has never seen the codebase. Nine files, each with a remarks block
        // saying what it sends: one posts observations, one asks the time, one trades a pairing
        // code for a token, one reads the overlay's context, one holds the overlay's live
        // WebSocket open, one backs the client's events up to Modbot Cloud (cloud event backup
        // spec), one fetches the voice -- once, from one pinned address, with nothing attached --
        // one fetches the phrase model the same way, added on 2026-09-19, and one reads the
        // sponsors, early adopters and contributors the Credits page shows. Nothing else reaches
        // the network.
        //
        // The ninth is a download and nothing else. Nothing a microphone hears reaches the network
        // from anywhere in this client, and the file that opens the microphone cannot reach it at
        // all (NothingTheClientShipsCanKeepOrSendWhatAMicrophoneHeard).
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
                "PhraseDownload.cs",
                "VoiceDownload.cs",
            ],
            senders);
    }

    /// <summary>Anything that would open a microphone, a line-in or a loopback, by any route.</summary>
    /// <remarks>
    /// The list grew on 2026-09-19 with the routes the phrase listener itself uses — the WASAPI
    /// recorder, the callback it hands sound over on, and the phrase matcher and its neighbours in
    /// the same library — so that the way in which this ban was narrowed cannot be used a second
    /// time by a second file. Exactly one file is allowed to match this, and
    /// <see cref="TheOnlyFileThatCanListenIsPhraseListeningCs"/> names it.
    /// </remarks>
    private static readonly Regex SoundCapture = new(
        @"\b(WaveIn|WaveInEvent|WaveInProvider|WasapiCapture|WasapiLoopbackCapture|AudioCaptureClient|IAudioCaptureClient"
        + @"|CaptureOpenDevice|CaptureStart|CaptureSamples|CaptureStop|CaptureCloseDevice|alcCaptureOpenDevice"
        + @"|MediaCapture|AudioRecord|SoundRecorder|eCapture"
        + @"|WasapiRecorder|WasapiRecorderBuilder|CaptureDataAvailableHandler|CaptureBufferLease"
        + @"|WithLoopbackCapture|WithProcessLoopback|StartRecording"
        + @"|KeywordSpotter|KeywordSpotterConfig|KeywordResult|OnlineRecognizer|VoiceActivityDetector"
        + @"|AcceptWaveform|SoundArrived)\b"
        + @"|DataFlow\s*\.\s*(Capture|All)\b"
        + @"|Extensions\s*\.\s*EXT\s*\.\s*Capture\b",
        RegexOptions.Compiled);

    /// <summary>The one file allowed to open a microphone.</summary>
    private const string ListeningFile = "PhraseListening.cs";

    [Fact]
    public void TheOnlyFileThatCanListenIsPhraseListeningCs()
    {
        // This ban used to be total. The voice gave the client an audio library, an audio library
        // has a recording half, and the rule was that the client plays and never listens: voice
        // chat sat in the never-recorded column beside keystrokes and the process list (M3 10).
        //
        // On 2026-09-19 a moderator asked to be able to say "Modbot, clip that" inside a headset,
        // where there is no keyboard and no settings screen to reach, and the rule was narrowed
        // rather than dropped: the capability exists, in one file, off unless a person switches it
        // on, only while VRChat is running, with nothing recorded, kept or sent. The listening
        // design spec carries the whole argument.
        //
        // The shape of the narrowing matters as much as the narrowing. A capability in one named
        // file is answerable -- "show me where this program listens" has a one-word answer. So the
        // ban stands everywhere else, the list of routes above grew to include the ones the new
        // file uses, and a second file learning any of them fails the build.
        //
        // What did NOT move: clips are still silent. A clip records no sound at all, and this file
        // records none either -- it matches four phrases and throws the sound away. The client
        // still has no way to keep or send a recording of anybody's voice, which is most of what
        // keeps both of these narrow.
        var listening = EverythingTheClientShips()
            .Where(f => SoundCapture.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal([ListeningFile], listening);

        var source = File.ReadAllText(
            EverythingTheClientShips().Single(f => Path.GetFileName(f) == ListeningFile));

        // The same plain-language disclosure every reading and sending file carries, and the three
        // claims that make this one bounded: nothing kept, nothing sent, and a shared device.
        Assert.Contains("<remarks>", source, StringComparison.Ordinal);
        Assert.Matches(Discloses, source);
        Assert.Contains("Nothing is recorded. Nothing is kept. Nothing is sent.", source, StringComparison.Ordinal);
        Assert.Contains("What leaves the machine: nothing", source, StringComparison.Ordinal);

        // Shared mode, never exclusive. This is the line that keeps VRChat's own microphone
        // working while Modbot listens, and the call that would break it must never appear.
        Assert.Contains("WithSharedMode()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WithExclusiveMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioClientShareMode.Exclusive", source, StringComparison.Ordinal);

        // And it listens to a microphone, never to what the PC is playing.
        Assert.DoesNotContain("Loopback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ListeningIsOnlyEverBuiltInOnePlaceAndIsOffUntilItIsSwitchedOn()
    {
        // "What does this program start, and when" has to stay answerable now that one of the
        // answers is a microphone. One place builds the listener, that place is the same one that
        // owns every other switch, and the settings it reads default to off -- so an existing
        // client updated into a version that can listen does not open a microphone.
        var building = EverythingTheClientShips()
            .Where(f => File.ReadAllText(f).Contains("new PhraseListening(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["Program.cs"], building);

        var settings = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Modbot.Companion", "Listening", "ListeningSettings.cs"));

        Assert.Contains("bool On = false", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOnlyEndpointsAskedForAreOutputs()
    {
        // Belt and braces for the ban above: every place the Windows audio system is asked for
        // devices names the render direction explicitly, so nothing enumerates "all" and picks.
        //
        // This one did not have to move when the microphone ban was narrowed, and that is worth
        // saying out loud: the listener asks Windows to route whichever device is the default
        // microphone rather than asking which microphones this PC has, so the client still never
        // enumerates capture endpoints at all -- the same shape as never enumerating windows and
        // never enumerating processes.
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
    public void NothingTheClientShipsCanKeepOrSendWhatAMicrophoneHeard()
    {
        // The narrowing above is "it may listen", not "it may record". So the one file allowed to
        // open a microphone is held to a second rule: it writes nothing and it sends nothing.
        // Everything else in the client is still barred from a microphone entirely, so this is the
        // whole of the surface that has to be checked.
        var source = File.ReadAllText(
            EverythingTheClientShips().Single(f => Path.GetFileName(f) == ListeningFile));

        foreach (var forbidden in new[]
                 {
                     "File.", "FileStream", "StreamWriter", "Directory.",
                     "HttpClient", "ClientWebSocket", "WaveFileWriter",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
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
        Assert.Contains("new OverlaySwitch(StartDesktopOverlay, StopDesktopOverlay", program, StringComparison.Ordinal);

        foreach (var direct in new[]
                 {
                     "StartOverlay();", "StopOverlay();",
                     "StartNotifyOverlay();", "StopNotifyOverlay();",
                     "StartDesktopOverlay();", "StopDesktopOverlay();",
                 })
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
