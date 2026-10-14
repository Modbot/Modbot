namespace Modbot.Core.Data.Entities;

/// <summary>The two kinds of line on a new staff member's checklist. Text, so the API and the web app read the same word.</summary>
public static class StaffTrialItemKinds
{
    /// <summary>A task the trial mod does and fills in themselves, with a written answer.</summary>
    public const string Task = "task";

    /// <summary>A line the trial mod's buddy signs off.</summary>
    public const string SignOff = "sign-off";

    public static bool IsKnown(string? kind) => kind is Task or SignOff;
}

/// <summary>What the checklist is limited to, in one place for the settings save and the answers.</summary>
public static class StaffTrialLimits
{
    /// <summary>The most lines of each kind a server can set.</summary>
    public const int MostItemsPerKind = 30;

    /// <summary>The longest a line's wording can be.</summary>
    public const int ItemTextLength = 300;

    /// <summary>The longest a written answer can be.</summary>
    public const int AnswerLength = 4000;

    /// <summary>The longest the buddy's note can be.</summary>
    public const int NoteLength = 4000;
}

/// <summary>
/// One new staff member's trial: who their buddy is, when it began, and the checklist they work
/// through. The table is <c>staff_trial</c>; one account has at most one.
/// </summary>
/// <remarks>
/// <para>
/// The checklist lines are copied from Settings when the trial begins (<see cref="StaffTrialItem"/>),
/// so editing the server's list later does not change what somebody already on a trial was given or
/// has answered.
/// </para>
/// <para>
/// <see cref="StartedAt"/> is what a reminder at the end of the trial counts from. The account and
/// buddy columns are plain ids, as <see cref="ApiKey.CreatedByUserId"/> is: an account row is never
/// removed.
/// </para>
/// </remarks>
public class StaffTrial
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The account on trial.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The account of the experienced moderator who signs the buddy lines off.</summary>
    public Guid BuddyId { get; set; }

    /// <summary>When the trial began. Day 30 counts from here.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>What the buddy wrote about how the trial went. Null until they write something.</summary>
    public string? BuddyNote { get; set; }

    public DateTimeOffset? BuddyNoteAt { get; set; }

    /// <summary>When the day-30 reminder for the Council's vote was posted in Discord. Null until it is; it is posted once.</summary>
    public DateTimeOffset? CouncilRemindedAt { get; set; }

    /// <summary>When the trial was closed, so it no longer counts as running. Null while it runs. A closed trial gets no reminder.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public List<StaffTrialItem> Items { get; set; } = [];
}

/// <summary>One line of a trial's checklist. The table is <c>staff_trial_item</c>.</summary>
public class StaffTrialItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TrialId { get; set; }

    /// <summary>One of <see cref="StaffTrialItemKinds"/>.</summary>
    public string Kind { get; set; } = StaffTrialItemKinds.Task;

    /// <summary>The line as the server worded it when the trial began.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Where the line sits among the lines of its kind, first first.</summary>
    public int Position { get; set; }

    /// <summary>The trial mod's written answer, for a task. Null while there is none.</summary>
    public string? Answer { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>When the buddy signed the line off, for a sign-off line. Null while it is not.</summary>
    public DateTimeOffset? SignedOffAt { get; set; }

    /// <summary>The account that signed it off: the buddy, or somebody who manages accounts in their place.</summary>
    public Guid? SignedOffById { get; set; }
}
