using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Flags;

/// <summary>
/// The two things a person can decide about a flag: it was wrong, or it was right
/// (AI moderation design §5 and §19).
/// </summary>
/// <remarks>
/// Here rather than in the endpoint because a flag is decided from two places — the Dismiss button
/// on the Flags page, and closing the flag's review — and both have to leave the same row and the
/// same fact behind. The caller owns the transaction and saves; this only changes the row and
/// writes the fact.
/// </remarks>
public static class FlagDecisions
{
    /// <summary>The rule was wrong. That rule and term never flags this person again.</summary>
    public static Task DismissAsync(
        IFactWriter facts, ModerationFlag flag, Guid userId, string username, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(flag);

        flag.State = ModerationFlagState.Dismissed;
        flag.DismissedAt = now;
        flag.DismissedByUserId = userId;
        flag.DismissedByUsername = Clip(username);

        return facts.WriteAsync(Fact(FactType.AiModerationFlagDismissed, flag, userId, username, now), ct);
    }

    /// <summary>The rule was right. The flag is closed, and the rule's card counts it.</summary>
    public static Task ConfirmAsync(
        IFactWriter facts, ModerationFlag flag, Guid userId, string username, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(flag);

        flag.State = ModerationFlagState.Confirmed;
        flag.ConfirmedAt = now;
        flag.ConfirmedByUserId = userId;
        flag.ConfirmedByUsername = Clip(username);

        return facts.WriteAsync(Fact(FactType.AiModerationFlagConfirmed, flag, userId, username, now), ct);
    }

    private static FactRecord Fact(
        string type, ModerationFlag flag, Guid userId, string username, DateTimeOffset now) => new()
    {
        Type = type,
        OccurredAt = now,
        SubjectPlatform = flag.SubjectPlatform,
        SubjectId = flag.SubjectId,
        ActorPlatform = FactPlatform.Modbot,
        ActorId = userId.ToString(),
        Source = FactSource.Modbot,
        Data = new JsonObject
        {
            ["flagId"] = flag.Id.ToString(),
            ["ruleKind"] = flag.RuleKind,
            ["ruleId"] = flag.RuleId.ToString(),
            ["ruleName"] = flag.RuleName,
            ["termKey"] = flag.TermKey,
            ["term"] = flag.Term,
            ["matched"] = flag.Matched,
            ["language"] = flag.Language,
            ["reviewId"] = flag.ReviewId?.ToString(),
            ["username"] = username,
        },
    };

    private static string Clip(string username) => username.Length <= 64 ? username : username[..64];
}
