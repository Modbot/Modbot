using System.Reflection;
using Modbot.Discord.Commands;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// The "What Modbot keeps" list lives in one place, and the docs page says the same.
/// </summary>
/// <remarks>
/// The list a member sees in Discord and the list an operator reads in the docs have to agree, or
/// one of them is telling somebody something untrue about their data. Nothing connects editing
/// one to editing the other, so this does: every heading and every line must be on the page word
/// for word.
/// </remarks>
public class WhatModbotKeepsTests
{
    [Fact]
    public void TheDocsPageListsEveryLine()
    {
        var page = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "docs", "content", "docs", "discord", "account-linking.mdx"));

        var missing = WhatModbotKeeps.Kinds
            .SelectMany(k => new[] { $"**{k.Heading}**" }.Concat(k.Lines.Select(line => "- " + line)))
            .Where(text => !page.Contains(text, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These are in WhatModbotKeeps but not on the /me docs page:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void TheListHasEveryHeadingAndLineTheDocsPageHas()
    {
        var page = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "docs", "content", "docs", "discord", "account-linking.mdx"));

        var start = page.IndexOf("### What Modbot keeps", StringComparison.Ordinal);
        var end = page.IndexOf("### Ask to delete my data", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The /me docs page has lost its What Modbot keeps section.");

        var section = page[start..end].Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var headings = section
            .Where(line => line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal))
            .Select(line => line[2..^2])
            .ToList();
        var lines = section
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToList();

        Assert.Equal(WhatModbotKeeps.Kinds.Select(k => k.Heading), headings);
        Assert.Equal(WhatModbotKeeps.Kinds.SelectMany(k => k.Lines), lines);
    }

    [Fact]
    public void TheCardFitsDiscordsLimits()
    {
        // Discord allows 6,000 characters across a card's title and fields, bullets and line breaks included.
        var total = WhatModbotKeeps.Title.Length
            + WhatModbotKeeps.Kinds.Sum(k => k.Heading.Length + k.Lines.Sum(line => line.Length + 3));
        Assert.InRange(total, 1, 6000);
    }

    [Fact]
    public void EveryHeadingFitsADiscordField()
    {
        Assert.All(WhatModbotKeeps.Kinds, kind =>
        {
            Assert.InRange(kind.Heading.Length, 1, 256);
            Assert.InRange(kind.Lines.Sum(line => line.Length + 3), 1, 1024);
        });

        // Discord allows 25 fields on a card.
        Assert.InRange(WhatModbotKeeps.Kinds.Count, 1, 25);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
