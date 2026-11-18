namespace Modbot.Core.Data.Entities;

/// <summary>
/// One thing a member of staff told the admins: a bug, an idea or a change they would like. The
/// table is <c>feedback</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Kept on this server and nowhere else.</strong> Nothing in this table is sent to Modbot
/// Cloud or to any other site (feedback design §1). The words are what the sender confirmed on the
/// draft; when AI tidied them it was asked to leave out the names of people, worlds and groups, but
/// what was saved is what the sender pressed Send on.
/// </para>
/// <para>
/// The sender is kept as an id and the name they had then, with no link to the account, so deleting
/// the account leaves the feedback readable and the name as it was.
/// </para>
/// </remarks>
public class FeedbackItem
{
    public const int MaxTitleLength = 120;

    public const int MaxDetailsLength = 4000;

    public Guid Id { get; set; }

    /// <summary>The account that sent it. Not a foreign key: the feedback outlives the account.</summary>
    public Guid SenderUserId { get; set; }

    /// <summary>The sender's username when they sent it.</summary>
    public string SenderName { get; set; } = string.Empty;

    /// <summary>One of <see cref="FeedbackKinds"/>.</summary>
    public string Kind { get; set; } = FeedbackKinds.Idea;

    public string Title { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    /// <summary>One of <see cref="FeedbackStatuses"/>.</summary>
    public string Status { get; set; } = FeedbackStatuses.New;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the status last changed. Null while it is still new.</summary>
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>The username of whoever last changed the status.</summary>
    public string? StatusChangedBy { get; set; }
}

/// <summary>What sort of thing the feedback is.</summary>
public static class FeedbackKinds
{
    public const string Bug = "bug";

    public const string Idea = "idea";

    public const string Change = "change";

    public static readonly IReadOnlyList<string> All = [Bug, Idea, Change];
}

/// <summary>Where the feedback stands with the admins.</summary>
public static class FeedbackStatuses
{
    public const string New = "new";

    public const string Reviewed = "reviewed";

    public const string Done = "done";

    public const string Dropped = "dropped";

    public static readonly IReadOnlyList<string> All = [New, Reviewed, Done, Dropped];
}
