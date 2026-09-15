namespace Modbot.Core.Data.Entities;

/// <summary>How one AI call ended.</summary>
/// <remarks>
/// Stored as text so a new outcome does not renumber the ones already written.
/// </remarks>
public static class AiCallOutcomes
{
    /// <summary>The model answered.</summary>
    public const string Answered = "answered";

    /// <summary>The feature's timeout ran out before the provider answered.</summary>
    public const string TimedOut = "timedOut";

    /// <summary>Could not reach the provider, or it answered with something unreadable.</summary>
    public const string Error = "error";

    /// <summary>The provider answered with an error status: a bad key, a model it will not run, a rate limit.</summary>
    public const string Refused = "refused";

    /// <summary>A Modbot limit stopped the call before it was made. Nothing was sent and nothing was charged.</summary>
    public const string Limited = "limited";

    /// <summary>In the order the call log lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Answered, TimedOut, Error, Refused, Limited];

    public static string LabelOf(string outcome) => outcome switch
    {
        Answered => "Answered",
        TimedOut => "Timed out",
        Error => "Error",
        Refused => "Refused",
        Limited => "Limited",
        _ => outcome,
    };

    public static bool IsKnown(string? outcome) => outcome is not null && All.Contains(outcome, StringComparer.Ordinal);
}

/// <summary>
/// One AI call, whatever came of it: which feature asked, which model answered, what it used, how
/// long it took and what went wrong.
/// </summary>
/// <remarks>
/// <para>
/// Beside <see cref="AiUsage"/>, not instead of it. That table is the spend ledger and holds a row
/// only when the provider counted tokens; this one holds a row for every attempt, including the
/// ones that timed out or never left the building, so a moderator can see why a feature went quiet.
/// </para>
/// <para>
/// <see cref="Prompt"/> and <see cref="Answer"/> are kept only for a call that produced a flag and
/// for a call somebody started from a button. Every other row is counts only: profile text belongs
/// to the person it describes, and keeping every check of it forever would be a second copy of the
/// group's members that nobody asked for.
/// </para>
/// </remarks>
public class AiCall
{
    /// <summary>Version 7, so the table sorts by when the call was made.</summary>
    public Guid Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>The feature that asked: <c>moderation</c>, <c>insights</c>, <c>chat</c>, <c>test</c>.</summary>
    public string Feature { get; set; } = string.Empty;

    /// <summary>The model the call asked for.</summary>
    public string ModelAsked { get; set; } = string.Empty;

    /// <summary>The model that answered, as the provider named it. Null when nothing answered.</summary>
    public string? ModelAnswered { get; set; }

    public string? Provider { get; set; }

    /// <summary>The fallback model answered because the main one did not.</summary>
    public bool Fallback { get; set; }

    /// <summary><see cref="AiCallOutcomes"/>.</summary>
    public string Outcome { get; set; } = AiCallOutcomes.Answered;

    /// <summary>What went wrong, in the provider's words where it gave any.</summary>
    public string? Error { get; set; }

    public int InputTokens { get; set; }

    /// <summary>Input tokens the provider served from its cache. Included in <see cref="InputTokens"/>.</summary>
    public int CachedInputTokens { get; set; }

    public int OutputTokens { get; set; }

    /// <summary>What the provider said the call cost, when it said. Only OpenRouter does.</summary>
    public decimal? ReportedCost { get; set; }

    /// <summary>From asking to answered or given up on, the fallback attempt included.</summary>
    public int DurationMs { get; set; }

    /// <summary>The Modbot account that asked, or null when Modbot asked on its own.</summary>
    public Guid? UserId { get; set; }

    public string? Username { get; set; }

    /// <summary>What the model was sent. Null on a row that keeps counts only.</summary>
    public string? Prompt { get; set; }

    /// <summary>What the model answered, as it answered. Null on a row that keeps counts only.</summary>
    public string? Answer { get; set; }

    /// <summary>The call produced at least one flag.</summary>
    public bool Flagged { get; set; }
}
