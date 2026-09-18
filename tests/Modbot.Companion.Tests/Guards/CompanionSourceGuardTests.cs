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
    private static readonly Regex ScreenCapture = new(
        @"\b(CopyFromScreen|BitBlt|PrintWindow|GetDC|GraphicsCaptureItem|GraphicsCapturePicker"
        + @"|Direct3D11CaptureFrame|DwmGetWindow|RenderTargetBitmap|CaptureScreen|Screenshot)\b",
        RegexOptions.Compiled);

    /// <summary>Anywhere a screenshot would be sitting on disk waiting to be read.</summary>
    private static readonly Regex ScreenshotFolders = new(
        @"(SpecialFolder\s*\.\s*(MyPictures|MyVideos|MyDocuments|Desktop)"
        + @"|VRChat[\\/]{1,2}Screenshots|VRChat[\\/]{1,2}Pictures|""Screenshots"")",
        RegexOptions.Compiled);

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
        Assert.Matches(ScreenshotFolders, @"Path.Combine(pictures, ""VRChat\Screenshots"")");
        Assert.Matches(ScreenshotFolders, "Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)");
        Assert.Matches(SoundCapture, "using var capture = new WasapiCapture(device);");
        Assert.Matches(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)");
        Assert.Matches(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)");
        Assert.Matches(SoundCapture, "var mic = capture.CaptureOpenDevice(null, 16000, BufferFormat.Mono16, 1024);");
        Assert.DoesNotMatch(SoundCapture, "enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)");
    }

    [Fact]
    public void NothingTheClientShipsCanCaptureAScreen()
    {
        // M3 3.1.1 draws this line explicitly, and it is the one capability that would make the
        // client's resemblance to an infostealer complete rather than superficial. Screenshots are
        // in the never-transmitted column beside chat, keystrokes and the process list.
        //
        // Attaching evidence to a moderation case is a real feature -- it just is not this
        // program's. A moderator does it in the web UI, in a browser, by choosing a file: a
        // deliberate human action in an application people already trust with a file dialog.
        // The distinction is the whole point, so it is a test rather than a paragraph.
        var offenders = EverythingTheClientShips()
            .Where(f => ScreenCapture.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The client must never capture the screen; found capture APIs in "
            + string.Join(", ", offenders));
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
        // The client plays; it never listens. Voice chat is in the never-transmitted column beside
        // screenshots, keystrokes and the process list (M3 10), and the way to keep it there is to
        // make the recording APIs fail the build rather than a review.
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
