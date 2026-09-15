namespace Modbot.Core.Data.Entities;

/// <summary>Where a term list came from.</summary>
public static class TermListSource
{
    /// <summary>Written by the operator on this deployment.</summary>
    public const string Local = "local";

    /// <summary>Subscribed from Modbot Hub. Read-only here, apart from switching terms off.</summary>
    public const string Cloud = "cloud";
}

/// <summary>The two kinds of rule, as stored on a flag.</summary>
public static class ModerationRuleKind
{
    public const string TermList = "termList";

    public const string Topic = "topic";
}

/// <summary>A term list (AI moderation design §2).</summary>
public class ModerationTermList
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="TermListSource.Local"/> or <see cref="TermListSource.Cloud"/>.</summary>
    public string Source { get; set; } = TermListSource.Local;

    public bool Enabled { get; set; }

    /// <summary>A <c>ModerationTargets</c> value.</summary>
    public int Targets { get; set; }

    /// <summary>
    /// The terms, as a JSON array. A list is read and written whole: it is edited as one form, and
    /// matching loads every term of every enabled list anyway.
    /// </summary>
    public string Terms { get; set; } = "[]";

    /// <summary>Ids of terms switched off, as a JSON array. Survives a Hub update, because Hub ids are stable.</summary>
    public string ExcludedTerms { get; set; } = "[]";

    public bool DeleteMessage { get; set; }

    public int? TimeoutMinutes { get; set; }

    /// <summary>Who set this list to act, and when. Null while it only flags.</summary>
    public Guid? ActSetByUserId { get; set; }

    public string? ActSetByUsername { get; set; }

    public DateTimeOffset? ActSetAt { get; set; }

    // ── Cloud lists only ────────────────────────────────────────────────────────────────────

    /// <summary>The Hub's id for the list, e.g. <c>modbot_harassment_terms</c>.</summary>
    public string? HubId { get; set; }

    public string? HubVersion { get; set; }

    public DateTimeOffset? HubFetchedAt { get; set; }

    /// <summary>A newer version, fetched and waiting for the operator to apply it (foundation §4.2.7).</summary>
    public string? HubAvailableVersion { get; set; }

    /// <summary>The waiting version's terms, already converted, as a JSON array.</summary>
    public string? HubAvailableTerms { get; set; }

    /// <summary>How many terms the waiting version adds, removes and changes, as JSON.</summary>
    public string? HubAvailableChanges { get; set; }

    /// <summary>Why the last fetch failed, or null when it worked.</summary>
    public string? HubError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An AI topic: a name and what to catch, in the operator's words (AI moderation design §2).</summary>
public class ModerationTopic
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    /// <summary><c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public string Sensitivity { get; set; } = "medium";

    public bool Enabled { get; set; }

    public int Targets { get; set; }

    public bool DeleteMessage { get; set; }

    public int? TimeoutMinutes { get; set; }

    public Guid? ActSetByUserId { get; set; }

    public string? ActSetByUsername { get; set; }

    public DateTimeOffset? ActSetAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ModerationFlagState : short
{
    Open = 1,
    Dismissed = 2,
}

/// <summary>
/// One flag: a rule matched something a person wrote (AI moderation design §5).
/// </summary>
/// <remarks>
/// The row is the working copy moderators page through and dismiss; the facts written alongside it
/// are the record. Dismissal is the one change ever made to a row, and it writes its own fact.
/// </remarks>
public class ModerationFlag
{
    public Guid Id { get; set; }

    public DateTimeOffset FlaggedAt { get; set; }

    /// <summary><see cref="ModerationRuleKind"/>.</summary>
    public string RuleKind { get; set; } = string.Empty;

    public Guid RuleId { get; set; }

    /// <summary>The rule's name when it flagged. The rule may have been renamed or deleted since.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>Which term in the list. Empty for a topic, so the dismissal lookup has one shape.</summary>
    public string TermKey { get; set; } = string.Empty;

    public string Term { get; set; } = string.Empty;

    /// <summary>One target name, e.g. <c>discordMessage</c>.</summary>
    public string Target { get; set; } = string.Empty;

    public FactPlatform SubjectPlatform { get; set; }

    public string SubjectId { get; set; } = string.Empty;

    public string? SubjectName { get; set; }

    public string? ChannelId { get; set; }

    public string? MessageId { get; set; }

    public string Matched { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public bool MessageDeleted { get; set; }

    public int? TimedOutMinutes { get; set; }

    public ModerationFlagState State { get; set; } = ModerationFlagState.Open;

    public DateTimeOffset? DismissedAt { get; set; }

    public Guid? DismissedByUserId { get; set; }

    public string? DismissedByUsername { get; set; }
}
