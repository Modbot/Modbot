namespace Modbot.Core.Data.Entities;

/// <summary>
/// One sample text in a rule's test set (AI moderation design §12).
/// </summary>
/// <remarks>
/// A sample belongs to one rule, term list or AI topic, and says what the operator expects: this
/// text should be flagged, or it should not. Running the set is what earns a rule the right to act.
/// </remarks>
public class ModerationTestSample
{
    public Guid Id { get; set; }

    /// <summary><see cref="ModerationRuleKind"/>.</summary>
    public string RuleKind { get; set; } = string.Empty;

    public Guid RuleId { get; set; }

    /// <summary>The sample text. Short: a message or a line of a profile, not a transcript.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>What the operator expects.</summary>
    public bool ShouldFlag { get; set; }

    /// <summary>Why this sample is here, in the operator's words. Optional.</summary>
    public string? Note { get; set; }

    /// <summary>One target name, e.g. <c>discordMessage</c>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Written by Modbot when the rule was created, not typed by the operator.</summary>
    public bool Seeded { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One run of a rule's test set, kept so runs can be compared (AI moderation design §12.3).</summary>
/// <remarks>
/// The model and the time are stored with the run, because the answer to "did the new model make
/// this rule worse" is the run before it beside the run after it.
/// </remarks>
public class ModerationTestRun
{
    public Guid Id { get; set; }

    public string RuleKind { get; set; } = string.Empty;

    public Guid RuleId { get; set; }

    public DateTimeOffset RanAt { get; set; }

    /// <summary>The model that answered, as the provider named it. Null for a term list: no model ran.</summary>
    public string? Model { get; set; }

    /// <summary>The rule's version when the run happened (AI moderation design §14).</summary>
    public int RuleVersion { get; set; }

    public int Samples { get; set; }

    /// <summary>How many samples said "should flag".</summary>
    public int ShouldFlagCount { get; set; }

    /// <summary>Of those, how many the rule caught.</summary>
    public int Caught { get; set; }

    /// <summary>Of those, how many it did not.</summary>
    public int Missed { get; set; }

    /// <summary>How many samples said "should not flag".</summary>
    public int ShouldNotFlagCount { get; set; }

    /// <summary>Of those, how many the rule flagged anyway. The gate (§12.4) wants this at zero.</summary>
    public int WronglyFlagged { get; set; }

    /// <summary>Why AI topics did not run, when they could have. Null when nothing was skipped.</summary>
    public string? AiSkipped { get; set; }

    /// <summary>One row per sample, as a JSON array: what matched, the quote and the reason.</summary>
    public string Results { get; set; } = "[]";

    public Guid? RanByUserId { get; set; }

    public string? RanByUsername { get; set; }
}

/// <summary>
/// A rule as it stood at one version (AI moderation design §14).
/// </summary>
/// <remarks>
/// A flag and an action record the version they acted on, so the Flags page can show the rule text
/// as it was then rather than as it is now. A new version is written whenever the rule's own text
/// changes: its name, its terms or instructions, what it checks and where. Switching it on and off,
/// changing what it does, the trial and a pause are not the rule's text and do not make a version.
/// </remarks>
public class ModerationRuleVersion
{
    public Guid Id { get; set; }

    public string RuleKind { get; set; } = string.Empty;

    public Guid RuleId { get; set; }

    /// <summary>Counts from 1 for each rule.</summary>
    public int Version { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public Guid? ChangedByUserId { get; set; }

    public string? ChangedByUsername { get; set; }

    /// <summary>The rule's name at this version.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The rule as a moderator reads it: the terms, or what to catch.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The whole rule at this version, as JSON.</summary>
    public string Snapshot { get; set; } = "{}";
}
