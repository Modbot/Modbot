using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// A moderator's pattern that looked unusual, opened for a person to look at. The table is
/// <c>modbot_review</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.8.5: detection surfaces patterns for human review. It never accuses and never punishes.
/// A review is a question -- "this happened; is it what it looks like?" -- and closing one records
/// who answered it and what they said. Both the opening and the closing are also facts, so the
/// answer is history.
/// </para>
/// <para>
/// <strong>One review per moderator, signal and thing, at a time.</strong> The "thing"
/// (<see cref="About"/>) is the person for a same-person signal and the UTC day for a volume
/// signal. While a review is open, re-running detection refreshes its numbers rather than opening
/// a second one. Once closed, detection opens another for the same key only on evidence newer
/// than <see cref="WindowEnd"/> -- so a closed review is never reopened by the facts that opened
/// it, and a moderator who keeps going after being asked does get asked again.
/// </para>
/// </remarks>
public class Review
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public FactPlatform ModeratorPlatform { get; set; }

    /// <summary>The moderator being reviewed. Opaque; never parsed (spec 3.1.1).</summary>
    public string ModeratorId { get; set; } = string.Empty;

    /// <summary>Which check fired -- see <see cref="ReviewSignal"/>.</summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>What the review is about: a person's id, or a UTC day as <c>yyyy-MM-dd</c>.</summary>
    public string About { get; set; } = string.Empty;

    /// <summary>The earliest fact the evidence covers.</summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>
    /// The latest instant the evidence covers. For a same-person review, the last action counted;
    /// for a day review, the end of the day. Detection opens a new review for the same key only on
    /// facts after this.
    /// </summary>
    public DateTimeOffset WindowEnd { get; set; }

    /// <summary>The pattern in one plain sentence, with the numbers in it.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Every number the review was opened on, and the ids of the facts behind them, as
    /// <c>jsonb</c>. Kept so the page can show its working rather than a verdict.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string Evidence { get; set; } = "{}";

    public ReviewState State { get; set; } = ReviewState.Open;

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>When the evidence was last refreshed by a detection run.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedByUserId { get; set; }

    /// <summary>The username at the time, so the record reads without a join and survives a rename.</summary>
    public string? ClosedByUsername { get; set; }

    /// <summary>What the person who closed it said. Required to close.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// <see cref="ReviewOutcome"/> for a review opened by an AI moderation flag; null for every
    /// other signal, where closing records that a person looked and nothing more.
    /// </summary>
    public string? Outcome { get; set; }
}

/// <summary>
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </summary>
public enum ReviewState : short
{
    Open = 1,
    Closed = 2,
}

/// <summary>The checks that can open a review. Stored as text so a new one needs no migration.</summary>
public static class ReviewSignal
{
    /// <summary>
    /// One moderator keeps acting on one person, across different instances or days, where no
    /// other moderator has (spec 5.8.5, first two bullets).
    /// </summary>
    public const string SamePerson = "same-person";

    /// <summary>
    /// One moderator did far more in a day than the rest of the team did that day and than the
    /// team usually does (spec 5.8.5, fourth bullet).
    /// </summary>
    public const string FarAboveTeam = "far-above-team";

    /// <summary>
    /// An AI moderation rule flagged somebody, and the flag was sent here so the team's normal
    /// review flow handles it (AI moderation design §19). The "moderator" is the rule.
    /// </summary>
    public const string AiFlag = "ai-flag";
}

/// <summary>
/// What a person concluded when they closed a review (AI moderation design §19).
/// </summary>
/// <remarks>
/// Only a review opened by an AI moderation flag asks for one, because only there does the answer
/// change something: "wrong" dismisses the flag, "right" confirms it, and both feed the rule's
/// counts. A review of a moderator's pattern records a note and nothing else -- closing one is not
/// a verdict on the moderator.
/// </remarks>
public static class ReviewOutcome
{
    /// <summary>The rule was right to flag it.</summary>
    public const string Right = "right";

    /// <summary>The rule was wrong: a false positive.</summary>
    public const string Wrong = "wrong";

    public static bool IsOutcome(string? outcome)
        => string.Equals(outcome, Right, StringComparison.Ordinal) || string.Equals(outcome, Wrong, StringComparison.Ordinal);
}
