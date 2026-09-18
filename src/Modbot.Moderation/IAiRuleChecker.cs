using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;

namespace Modbot.Moderation;

/// <summary>A picture that belongs to a piece of text, before anything is fetched (AutoMod design §5).</summary>
/// <param name="Label">What the flag calls it: "Attachment cat.png", "Discord avatar", "VRChat profile picture".</param>
public sealed record PictureSource(string Label, string Url);

/// <summary>Who asked for the check, when anybody did.</summary>
/// <param name="KeepText">Whether the call log keeps the prompt and the answer whatever comes of it.</param>
public sealed record AiAsker(Guid? UserId = null, string? Username = null, bool KeepText = false)
{
    public static AiAsker Nobody { get; } = new();
}

/// <summary>One piece of text the AI is asked about, and the topics it is checked against.</summary>
/// <param name="Key">A short name used only inside one check, so hits can be matched back.</param>
/// <param name="Context">The messages before this one, for understanding it only (AI moderation design §16).</param>
/// <param name="Pictures">The pictures that belong to this text. Empty when the rule or the pictures tool is off.</param>
public sealed record AiRuleText(
    string Key,
    ModerationTargets Target,
    string Text,
    IReadOnlyList<ModerationTopic> Topics,
    IReadOnlyList<ContextMessage> Context,
    IReadOnlyList<PictureSource> Pictures);

/// <summary>One topic the AI said a piece of text matched.</summary>
/// <param name="CallId">The call that answered, in the call log.</param>
/// <param name="Picture">The picture that matched, when one did. Null when the words did.</param>
public sealed record AiRuleHit(
    string TextKey,
    Guid TopicId,
    string Why,
    string Quote,
    Guid CallId,
    string? Picture = null,
    string? PictureUrl = null);

/// <summary>What the AI answered for a whole check.</summary>
/// <param name="Skipped">Why AI topics did not run, or did not finish: AI off, a limit reached, an error.</param>
/// <param name="Model">The model that answered, when one did.</param>
/// <param name="CallTexts">What each call was sent and what it answered, kept only for calls that flag.</param>
public sealed record AiRuleAnswer(
    IReadOnlyList<AiRuleHit> Hits,
    string? Skipped,
    string? Model,
    IReadOnlyDictionary<Guid, (string Prompt, string Answer)> CallTexts)
{
    public static AiRuleAnswer Off(string why) => new([], why, null, new Dictionary<Guid, (string, string)>());
}

/// <summary>
/// The AI half of a check (AutoMod design §4): which topics some text matches, and whether a picture
/// does. <c>Modbot.AI</c> implements it; without it, every check runs term lists only.
/// </summary>
/// <remarks>
/// The engine only calls this when AI is on and the AutoMod tool that classifies against topics is
/// switched on, and it hands over pictures only when the picture tool is (AutoMod design §6). An
/// implementation never has to check those switches itself, but a fake in a test can prove they
/// were honoured by counting its calls.
/// </remarks>
public interface IAiRuleChecker
{
    Task<AiRuleAnswer> CheckAsync(IReadOnlyList<AiRuleText> texts, AiAsker asker, CancellationToken ct);
}

/// <summary>The checker in a process without <c>Modbot.AI</c>: AI topics never run.</summary>
public sealed class NoAiRuleChecker : IAiRuleChecker
{
    public const string Reason = "AI is off.";

    public Task<AiRuleAnswer> CheckAsync(IReadOnlyList<AiRuleText> texts, AiAsker asker, CancellationToken ct)
        => Task.FromResult(AiRuleAnswer.Off(Reason));
}
