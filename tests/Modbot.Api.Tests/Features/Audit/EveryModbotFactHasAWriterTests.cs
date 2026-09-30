using System.Reflection;
using System.Text.RegularExpressions;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// A fact type nothing writes is a promise the audit log cannot keep.
/// </summary>
/// <remarks>
/// <para>
/// Eight types were in the list with a label, a category and a Discord option, and no code that
/// ever wrote one. The Cloud report counts cold stops and blocked requests from two of them and so
/// always read zero. Nothing connects adding a type to writing it, so this does: every
/// <c>modbot.*</c> type must be named somewhere in <c>src</c> that writes facts.
/// </para>
/// <para>
/// The eight that have no writer today are listed below, each with what is missing. The list is
/// checked both ways: a type that gains a writer must come off it, so it only ever gets shorter.
/// </para>
/// </remarks>
public class EveryModbotFactHasAWriterTests
{
    /// <summary>Types with no writer yet, and what is missing. Take one off when it gets a writer.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoWriterYet = new Dictionary<string, string>
    {
        // The evidence facts come off this list when evidence handling writes them (TASK-005).
        [nameof(FactType.EvidenceAttached)] = "nothing writes a fact when evidence is attached to a case file",
        [nameof(FactType.EvidenceAccessed)] = "nothing writes a fact when somebody opens a piece of evidence",
        [nameof(FactType.EvidenceDestroyed)] = "nothing writes a fact when evidence is destroyed",

        // The Cloud report counts the second and third of these, so its numbers stay 0 until
        // something writes them.
        [nameof(FactType.SyncFailed)] = "no producer writes a fact when a sync fails",
        [nameof(FactType.RateLimitColdStop)] = "nothing writes a fact when the rate limiter cold stops",
        [nameof(FactType.WafBlocked)] = "nothing writes a fact when a request is blocked by Cloudflare",
        [nameof(FactType.MigrationApplied)] = "nothing writes a fact when a migration is applied",
        [nameof(FactType.PartitionCreated)] = "nothing writes a fact when a partition is created",
    };

    /// <summary>
    /// Files that list or describe fact types rather than write them: the constants themselves,
    /// their labels, the audit log's tables and the Discord route options. The demo is out too,
    /// because it invents facts a real deployment never writes.
    /// </summary>
    private static readonly string[] NotWriters =
    [
        Path.Combine("Modbot.Core", "Data", "Entities", "FactType.cs"),
        Path.Combine("Modbot.Core", "Data", "Entities", "FactLabels.cs"),
        Path.Combine("Modbot.Core", "Discord", "DiscordEventTypes.cs"),
        Path.Combine("Modbot.Api", "Features", "Audit", "FactSubjects.cs"),
        Path.Combine("Modbot.Api", "Features", "Audit", "AuditVisibility.cs"),
        Path.Combine("Modbot.Analytics", "Facts", "LinkedActions.cs"),
    ];

    /// <summary>A line that reads or compares a type is not a line that writes one.</summary>
    private static readonly Regex Compares = new(
        @"==|!=|\bcase\b|\.Contains\(|\bis\s+FactType|\bnot\s+FactType",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A type that is a whole line by itself: an element of a list, or the first argument of a call.</summary>
    private static readonly Regex OnItsOwn = new(
        @"^\s*(?:Core\.Data\.Entities\.)?FactType\.\w+,?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A line that is one entry of a table: a pair (<c>new(A, B)</c>) or a key (<c>[FactType.X] =</c>).</summary>
    private static readonly Regex AnEntry = new(
        @"^\s*(?:new\s*\(\s*FactType\.|\[\s*FactType\.)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryModbotFactTypeHasSomethingThatWritesIt()
    {
        var withoutWriter = TypesWithoutAWriter();

        var missing = withoutWriter
            .Where(name => !NoWriterYet.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These fact types are never written by anything in src. Write them, or add them to NoWriterYet "
            + $"with the reason:{Environment.NewLine}" + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void ATypeThatGainedAWriterComesOffTheList()
    {
        var withoutWriter = TypesWithoutAWriter();

        var stale = NoWriterYet.Keys
            .Where(name => !withoutWriter.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These fact types are written now. Take them off NoWriterYet:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>The names of the <c>modbot.*</c> constants no writing line in src refers to.</summary>
    private static HashSet<string> TypesWithoutAWriter()
    {
        var src = Path.Combine(FindRepoRoot(), "src");

        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && !IsDemo(path, src) && !IsNotAWriter(path, src))
            .ToList();

        // A type is being written when it stands where a fact's type goes -- Type = FactType.X,
        // an argument, a switch arm, either side of a condition -- and not when it is only listed:
        // a line that is nothing but the type counts only as the first argument of a call (the line
        // before opens it), so an element of a list or an array does not; nor does an entry of a
        // table, or a comparison.
        var lines = files
            .SelectMany(path =>
            {
                var all = File.ReadAllLines(path);

                return all
                    .Select((line, index) => (line, previous: index == 0 ? string.Empty : all[index - 1].TrimEnd()))
                    .Where(x => !Compares.IsMatch(x.line) && !AnEntry.IsMatch(x.line))
                    .Where(x => !OnItsOwn.IsMatch(x.line) || x.previous.EndsWith('('))
                    .Select(x => x.line);
            })
            .ToList();

        var names = typeof(FactType)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Where(f => ((string)f.GetRawConstantValue()!).StartsWith("modbot.", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();

        // Guards the guard: a walk that read nothing would call every type unwritten, and a
        // renamed folder would do it silently.
        Assert.True(lines.Count > 1000, "Almost no source was read, so the search cannot tell what is written.");

        return names
            .Where(name => !lines.Any(line => Regex.IsMatch(line, $@"\bFactType\.{name}\b")))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsDemo(string path, string src)
        => Path.GetRelativePath(src, path).StartsWith("Modbot.Demo" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsNotAWriter(string path, string src)
        => NotWriters.Contains(Path.GetRelativePath(src, path), StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
