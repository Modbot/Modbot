using System.Reflection;
using System.Text.RegularExpressions;

namespace Modbot.Core.Tests.Time;

public class NoSystemClockTests
{
    private static readonly Regex SystemClockUse = new(
        @"\bDateTime(Offset)?\s*\.\s*(UtcNow|Now|Today)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Walks the actual source tree. A unit test cannot catch this because the violation compiles
    /// perfectly well -- it just silently produces wrong timestamps.
    /// </summary>
    [Fact]
    public void NoSourceFileReadsTheSystemClock()
    {
        var repoRoot = FindRepoRoot();
        var allowed = Path.Combine(repoRoot, "src", "Modbot.Core", "Time", "SystemModbotClock.cs");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !string.Equals(f, allowed, StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => SystemClockUse.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(repoRoot, f))
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These files read the system clock instead of IModbotClock:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
