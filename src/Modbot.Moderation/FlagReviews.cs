using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;

namespace Modbot.Moderation;

/// <summary>
/// Turns one AI moderation flag into a review, so the team's normal review flow handles it
/// (AI moderation design §19).
/// </summary>
/// <remarks>
/// <para>
/// A flag and a review ask the same question — "this happened; is it what it looks like?" — and
/// Reviews already has the page, the badge, the permission and the record of who answered. So a
/// flag is sent there rather than given a second thing that looks like it.
/// </para>
/// <para>
/// The "moderator" being reviewed is the rule, on the Modbot platform, the same way a rule that
/// pauses itself is the subject of its own fact: nobody on VRChat did this. <c>About</c> is the
/// flag, so the open-review index already stops one flag opening two reviews.
/// </para>
/// </remarks>
public static class FlagReviews
{
    public static Review For(ModerationFlag flag, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(flag);

        return new Review
        {
            Id = Guid.CreateVersion7(now),
            ModeratorPlatform = FactPlatform.Modbot,
            ModeratorId = flag.RuleId.ToString(),
            Signal = ReviewSignal.AiFlag,
            About = flag.Id.ToString(),
            WindowStart = flag.FlaggedAt,
            WindowEnd = flag.FlaggedAt,
            Summary = Summary(flag),
            Evidence = Evidence(flag).ToJsonString(),
            State = ReviewState.Open,
            OpenedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>The flag in one sentence, with the words that matched in it.</summary>
    public static string Summary(ModerationFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);

        var who = string.IsNullOrWhiteSpace(flag.SubjectName) ? flag.SubjectId : flag.SubjectName;
        var what = flag.Picture is { Length: > 0 } picture ? picture : $"“{flag.Matched}”";
        var sentence = $"{flag.RuleName} flagged {who}: {what}.";

        return sentence.Length <= 1000 ? sentence : sentence[..1000];
    }

    private static JsonObject Evidence(ModerationFlag flag) => new()
    {
        ["flagId"] = flag.Id.ToString(),
        ["ruleKind"] = flag.RuleKind,
        ["ruleId"] = flag.RuleId.ToString(),
        ["ruleName"] = flag.RuleName,
        ["ruleVersion"] = flag.RuleVersion,
        ["term"] = flag.Term,
        ["target"] = flag.Target,
        ["subjectPlatform"] = flag.SubjectPlatform.ToString(),
        ["subjectId"] = flag.SubjectId,
        ["subjectName"] = flag.SubjectName,
        ["matched"] = flag.Matched,
        ["reason"] = flag.Reason,
        ["language"] = flag.Language,
        ["picture"] = flag.Picture,
        ["pictureUrl"] = flag.PictureUrl,
        ["channelId"] = flag.ChannelId,
        ["messageId"] = flag.MessageId,
        ["contextMessageIds"] = Ids(flag.ContextMessageIds),
        ["flaggedAt"] = flag.FlaggedAt.ToString("O"),
    };

    /// <summary>
    /// The review's evidence with the AI's opinion added (AutoMod design §6.3), so the person
    /// closing the review reads it beside the words that matched.
    /// </summary>
    public static string WithOpinion(string? evidence, string verdict, string why, string? proposedAction, DateTimeOffset at)
    {
        JsonObject o;

        try
        {
            o = (string.IsNullOrWhiteSpace(evidence) ? null : JsonNode.Parse(evidence) as JsonObject) ?? new JsonObject();
        }
        catch (JsonException)
        {
            o = new JsonObject();
        }

        o["aiOpinion"] = verdict;
        o["aiOpinionReason"] = why;
        o["aiProposedAction"] = proposedAction;
        o["aiOpinionAt"] = at.ToString("O");

        return o.ToJsonString();
    }

    private static JsonArray Ids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return new JsonArray([.. (JsonSerializer.Deserialize<List<string>>(json) ?? []).Select(id => (JsonNode?)JsonValue.Create(id))]);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
