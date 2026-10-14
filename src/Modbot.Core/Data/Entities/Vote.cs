namespace Modbot.Core.Data.Entities;

/// <summary>Whether a vote still takes votes.</summary>
public static class VoteStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>What a vote is limited to, in one place for the page, the command and the post.</summary>
public static class VoteLimits
{
    /// <summary>
    /// The most options on one vote: Discord's five rows, each holding a drop-down list of 25
    /// choices. Up to 25 options are buttons; more than that are lists.
    /// </summary>
    public const int MostOptions = 125;

    /// <summary>A row of buttons or a drop-down list holds 25.</summary>
    public const int PerRow = 25;

    public const int MostQuestionLength = 200;

    /// <summary>A button's label holds 80 characters; a drop-down choice's holds 100.</summary>
    public const int MostLabelLength = 80;
}

/// <summary>
/// A question Modbot posts in a Discord channel with one button for each option. The table is
/// <c>vote</c>.
/// </summary>
/// <remarks>
/// A vote has no time limit: staff close it. Each person has one pick on it, and can change it
/// until it is closed.
/// </remarks>
public class Vote
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The question, as the message shows it.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>The channel the message is posted in. Text, never parsed.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The posted message. Text, never parsed.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// The Discord role a person must hold to vote, or null when anybody in the server may. Text,
    /// never parsed.
    /// </summary>
    public string? RoleId { get; set; }

    /// <summary>
    /// The message shows how many people picked each option while the vote is open, and is
    /// rewritten after picks. Off: the counts appear when the vote is closed.
    /// </summary>
    public bool ShowCounts { get; set; }

    /// <summary>
    /// On: nobody sees who picked what, on the page or in Discord. Off: the Modbot page lists who
    /// picked each option (Discord never does). Modbot keeps each person's pick either way, to
    /// allow one vote each and changes; this only decides whether it is shown. Set when the vote
    /// is made and never switched off afterwards.
    /// </summary>
    public bool Anonymous { get; set; } = true;

    /// <summary>One of <see cref="VoteStatuses"/>.</summary>
    public string Status { get; set; } = VoteStatuses.Open;

    /// <summary>The Modbot account that made it.</summary>
    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public List<VoteOption> Options { get; set; } = [];
}

/// <summary>One option on a vote. The table is <c>vote_option</c>.</summary>
public class VoteOption
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid VoteId { get; set; }

    /// <summary>What the button or choice says.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The option's place on the vote, from 0.</summary>
    public int Position { get; set; }
}

/// <summary>
/// One person's pick on a vote: there is at most one for each person, and changing a vote changes
/// this row. The table is <c>vote_pick</c>.
/// </summary>
public class VotePick
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid VoteId { get; set; }

    public Guid OptionId { get; set; }

    /// <summary>Who voted. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>When they last voted or changed their pick.</summary>
    public DateTimeOffset VotedAt { get; set; }
}
