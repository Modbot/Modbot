using System.Reflection;
using System.Text.RegularExpressions;

namespace Modbot.Client.Tests.Guards;

/// <summary>
/// Guards that scan the client's own source.
/// </summary>
/// <remarks>
/// These cannot be unit tests, because the things they forbid all compile perfectly well. They are
/// the enforcement behind two promises: that the client has one clock, and that its source is
/// honest about what it does with a moderator's machine.
/// </remarks>
public class ClientSourceGuardTests
{
    private static readonly Regex SystemClockUse = new(
        @"\bDateTime(Offset)?\s*\.\s*(UtcNow|Now|Today)\b",
        RegexOptions.Compiled);

    /// <summary>Anything that reads the disk, or puts bytes on the network.</summary>
    private static readonly Regex TouchesTheOutsideWorld = new(
        @"\b(File|Directory|FileStream|StreamReader|StreamWriter|HttpClient|HttpRequestMessage|HttpMessageHandler)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The plain-language disclosure a suspicious reader is owed: what is read, what is written, and
    /// what leaves the machine.
    /// </summary>
    private static readonly Regex Discloses = new(
        @"leaves the machine|transmit|is sent|sends|What this reads|written to your disk|never stored",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IEnumerable<string> ClientSources()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "Modbot.Client");

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
    [InlineData("System.Diagnostics.Process", "the client does not attach to, inspect or launch processes")]
    [InlineData("Microsoft.Win32.Registry", "the client does not read the registry")]
    [InlineData("localconfig.vdf", "the client does not read Steam's configuration -- M3 2.3.1")]
    [InlineData("GetAsyncKeyState", "the client does not read the keyboard")]
    [InlineData("Clipboard", "the client does not read the clipboard")]
    public void TheClientDoesNotDoTheThingsThatWouldMakeItAnActualInfostealer(string forbidden, string why)
    {
        // The client is, feature for feature, shaped like spyware: it runs unattended on a personal
        // PC, watches a file, and posts what it sees to a server. What separates it is that its
        // behaviour is bounded and checkable. These are the bounds, as a test.
        var offenders = ClientSources()
            .Where(f => File.ReadAllText(f).Contains(forbidden, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"{why}; found in {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheTwoDeclaredPlacesMakeOutboundRequests()
    {
        // "What does this program send, and where" should have exactly one answer, findable by
        // somebody who has never seen the codebase.
        var senders = ClientSources()
            .Where(f => File.ReadAllText(f).Contains("_http.SendAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["HttpIngestTransport.cs", "HttpServerTimeProbe.cs"], senders);
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
