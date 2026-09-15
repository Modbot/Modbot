namespace Modbot.Core.Moderation;

/// <summary>
/// Where a piece of text came from, which decides which rules look at it (AI moderation design §3).
/// </summary>
/// <remarks>Flags, so a rule can hold several. A check is always about exactly one.</remarks>
[Flags]
public enum ModerationTargets
{
    None = 0,
    DiscordMessage = 1 << 0,
    DisplayName = 1 << 1,
    Bio = 1 << 2,
    Status = 1 << 3,
    Pronouns = 1 << 4,
}

/// <summary>The API names of <see cref="ModerationTargets"/>, one way and back.</summary>
public static class ModerationTargetNames
{
    private static readonly (ModerationTargets Target, string Name)[] Names =
    [
        (ModerationTargets.DiscordMessage, "discordMessage"),
        (ModerationTargets.DisplayName, "displayName"),
        (ModerationTargets.Bio, "bio"),
        (ModerationTargets.Status, "status"),
        (ModerationTargets.Pronouns, "pronouns"),
    ];

    public const ModerationTargets All =
        ModerationTargets.DiscordMessage | ModerationTargets.DisplayName | ModerationTargets.Bio
        | ModerationTargets.Status | ModerationTargets.Pronouns;

    public static string NameOf(ModerationTargets single)
        => Array.Find(Names, n => n.Target == single).Name ?? single.ToString();

    public static IReadOnlyList<string> NamesOf(ModerationTargets targets)
        => Names.Where(n => targets.HasFlag(n.Target)).Select(n => n.Name).ToList();

    public static ModerationTargets? Parse(string? name)
    {
        foreach (var (target, text) in Names)
        {
            if (string.Equals(text, name, StringComparison.Ordinal))
                return target;
        }

        return null;
    }

    /// <summary>Every name parsed, or null with the offender named when one is not a target.</summary>
    public static ModerationTargets? ParseAll(IEnumerable<string>? names, out string? error)
    {
        error = null;
        var targets = ModerationTargets.None;

        foreach (var name in names ?? [])
        {
            if (Parse(name) is not { } one)
            {
                error = $"'{name}' is not a target.";
                return null;
            }

            targets |= one;
        }

        return targets;
    }
}

/// <summary>One Discord message, new or edited, as message indexing hands it over.</summary>
/// <param name="Text">The message text as it stands now. Empty text is not checked.</param>
/// <param name="Edited">True for an edit. A flag already raised on the message is not raised again either way.</param>
public sealed record DiscordMessageToCheck(
    string GuildId,
    string ChannelId,
    string MessageId,
    string AuthorId,
    string? AuthorName,
    string Text,
    bool Edited = false);

/// <summary>A VRChat profile's text, as stored.</summary>
public sealed record ProfileToCheck(
    string UserId,
    string? DisplayName,
    string? Bio,
    string? Status,
    string? Pronouns);

/// <summary>One rule that matched.</summary>
/// <param name="RuleKind"><c>termList</c> or <c>topic</c>.</param>
/// <param name="TermKey">Which term in the list; empty for a topic.</param>
/// <param name="Term">The term as written, or the topic name.</param>
/// <param name="Matched">The words in the text that matched.</param>
/// <param name="Reason">The Hub note for a term, or the model's reason for a topic.</param>
/// <param name="Suppressed">A moderator dismissed this flag for this person before, so nothing happens.</param>
public sealed record ModerationMatch(
    string RuleKind,
    Guid RuleId,
    string RuleName,
    string TermKey,
    string Term,
    ModerationTargets Target,
    string Matched,
    string? Reason,
    bool DeleteMessage,
    int? TimeoutMinutes,
    bool Suppressed = false);

/// <summary>What a check found and did.</summary>
/// <param name="AiSkipped">Why AI topics did not run, when there were topics that could have.</param>
public sealed record ModerationOutcome(
    IReadOnlyList<ModerationMatch> Matches,
    int FlagsWritten,
    bool MessageDeleted,
    int? TimedOutMinutes,
    string? AiSkipped)
{
    public static ModerationOutcome Nothing { get; } = new([], 0, false, null, null);
}

/// <summary>
/// The AI moderation engine (AI moderation design §8). Discord message indexing calls
/// <see cref="CheckDiscordMessageAsync"/> for each new or edited message; profile text goes through
/// <see cref="CheckProfileAsync"/>.
/// </summary>
/// <remarks>
/// Both do nothing while AI moderation is switched off, and neither throws over a broken rule or
/// an unreachable AI endpoint: a bad rule must not stop a message being indexed. Scoped, because it
/// uses the caller's database context.
/// </remarks>
public interface IModerationChecker
{
    Task<ModerationOutcome> CheckDiscordMessageAsync(DiscordMessageToCheck message, CancellationToken ct = default);

    Task<ModerationOutcome> CheckProfileAsync(ProfileToCheck profile, CancellationToken ct = default);
}

/// <summary>The checker in a process with no AI moderation: it checks nothing.</summary>
public sealed class NoModerationChecker : IModerationChecker
{
    public Task<ModerationOutcome> CheckDiscordMessageAsync(DiscordMessageToCheck message, CancellationToken ct = default)
        => Task.FromResult(ModerationOutcome.Nothing);

    public Task<ModerationOutcome> CheckProfileAsync(ProfileToCheck profile, CancellationToken ct = default)
        => Task.FromResult(ModerationOutcome.Nothing);
}
