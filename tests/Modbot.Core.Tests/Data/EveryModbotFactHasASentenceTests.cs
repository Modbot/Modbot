using System.Reflection;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Data;

public class EveryModbotFactHasASentenceTests
{
    /// <summary>
    /// Every fact Modbot records about its own work reads as a sentence in the audit log.
    /// </summary>
    /// <remarks>
    /// A type with no sentence falls back to printing itself, "Zuelatak · modbot.calendar.event.create
    /// · 01a0d9f1-…", which a volunteer moderator cannot read. Half of Modbot's own types were in that
    /// state before anybody noticed, because nothing connects adding a type here to writing its
    /// sentence in the web app. This does.
    /// </remarks>
    [Fact]
    public void EveryModbotFactTypeIsInTheSentenceTable()
    {
        var sentences = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Modbot.Web", "src", "components", "factSentence.tsx"));

        var missing = typeof(FactType)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(type => type.StartsWith("modbot.", StringComparison.Ordinal))
            .Where(type => !sentences.Contains($"'{type}':", StringComparison.Ordinal))
            .Order()
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These fact types have no sentence in factSentence.tsx:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
