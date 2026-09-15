using System.ClientModel;
using System.Text.Json;
using OpenAI.Chat;

namespace Modbot.AI.Chat;

/// <summary>What the model named the conversation, and what that one call used.</summary>
/// <param name="Title">Null when the model could not be asked or answered with nothing usable.</param>
public sealed record ChatTitleSuggestion(string? Title, ChatTokenUsage? Usage);

/// <summary>
/// The name a conversation is listed under.
/// </summary>
/// <remarks>
/// The first words of the question always work and cost nothing, so they are what a conversation
/// starts with and what it keeps when anything goes wrong. One short call after the first reply
/// replaces them with something a person can pick out of a list of thirty. It is counted and
/// limited like every other call Chat makes; the owner can type over it afterwards.
/// </remarks>
public static class ChatTitle
{
    public const int MaxLength = 80;

    /// <summary>Tokens the naming call may write. A name is a handful of words.</summary>
    private const int MaxTitleTokens = 24;

    /// <summary>How much of the question and the answer the model is shown.</summary>
    private const int Shown = 1200;

    private const string Instructions = """
        Name this conversation in at most six words, as a heading in a list of conversations.
        Use plain words and the same language as the question. No quotation marks, no full stop,
        no explanation: the name only.
        """;

    /// <summary>The first line of the first message, cut to fit the list.</summary>
    public static string FromQuestion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= MaxLength ? line : string.Concat(line.AsSpan(0, MaxLength - 1), "…");
    }

    /// <summary>
    /// Asks the model for a name. Never throws: a provider that refuses leaves the first words in
    /// place, which is a worse name and no kind of failure.
    /// </summary>
    public static async Task<ChatTitleSuggestion> SuggestAsync(
        ChatClient chat, string? provider, string question, string answer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chat);

        try
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = MaxTitleTokens };
            Usage.AiReportedCost.AskFor(options, provider);

            ChatCompletion completion = await chat.CompleteChatAsync(
                [
                    new SystemChatMessage(Instructions),
                    new UserChatMessage($"Question:\n{Cut(question)}\n\nAnswer:\n{Cut(answer)}"),
                ],
                options,
                ct);

            var text = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

            return new ChatTitleSuggestion(Tidy(text), completion.Usage);
        }
        catch (Exception e) when (e is ClientResultException or HttpRequestException or JsonException or InvalidOperationException)
        {
            return new ChatTitleSuggestion(null, null);
        }
    }

    /// <summary>A model asked for a heading often sends one wrapped in quotes or spread over lines.</summary>
    internal static string? Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim().Trim('"', '\'', '“', '”', '‘', '’', '#', '*', ' ', '.');

        if (line.Length == 0)
            return null;

        return line.Length <= MaxLength ? line : string.Concat(line.AsSpan(0, MaxLength - 1), "…");
    }

    private static string Cut(string text) =>
        text.Length <= Shown ? text : text[..Shown];
}
