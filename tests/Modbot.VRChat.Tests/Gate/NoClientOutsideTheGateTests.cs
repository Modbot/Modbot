using System.Reflection;
using System.Text.RegularExpressions;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Spec 4.1's absolute rule, enforced by reading the source.
/// </summary>
/// <remarks>
/// A client built anywhere else is a client that bypasses the rate limiter, the priority queue and
/// the single session — and nothing about it looks wrong at the call site. It compiles, it works
/// in development, and the first sign of trouble is an opaque multi-minute rate limit in
/// production. The same reasoning as the system-clock guard in <c>Modbot.Core.Tests</c>: the
/// violation is invisible to the compiler, so it needs a test that reads the code.
/// </remarks>
public class NoClientOutsideTheGateTests
{
    private static readonly Regex ClientConstruction = new(
        @"new\s+VRChatClientBuilder\b|VRChatClientBuilder\s*\.\s*From\b|VRChatClient\s*\.\s*Create\b",
        RegexOptions.Compiled);

    [Fact]
    public void OnlyTheClientFactoryConstructsAVRChatClient()
    {
        var repoRoot = FindRepoRoot();
        var allowed = Path.Combine(
            repoRoot, "src", "Modbot.VRChat", "Session", "VRChatClientFactory.cs");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase))
            .Where(file => ClientConstruction.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files build a VRChat client outside IVRChatGate's factory, bypassing the rate "
            + $"limiter (spec 4.1):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void NothingCallsTheSdksLoginHelpers()
    {
        var repoRoot = FindRepoRoot();

        // Spec 4.1.1: LoginAsync discards the status, so a 401, a 403, a 429 and a WAF block all
        // arrive as the same bare null -- and TryLoginAsync inverts its own result, reporting a
        // successful login as a failure with no exception to investigate.
        var helpers = new Regex(@"\b(TryLoginAsync|LoginAsync|LoginWithExternalCodeAsync)\s*\(");

        // The rule is about the VRChat SDK, so only files that bring it into scope are checked.
        // Discord.Net's socket client has a LoginAsync of its own, and the Discord bot calling
        // it says nothing about the gate.
        var sdkInScope = new Regex(@"^\s*using\s+VRChat\.API\b", RegexOptions.Multiline);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(f => sdkInScope.IsMatch(f.Text) && helpers.IsMatch(f.Text))
            .Select(f => f.File)
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files call the SDK's convenience login helpers, which discard the HTTP status "
            + $"the gate needs (spec 4.1.1):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Modbot.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
