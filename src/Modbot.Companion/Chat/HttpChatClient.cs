using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Chat;

/// <summary>
/// Dictate's way into AI Chat: asks the paired server whether chat is on for this device's owner, and
/// sends the words they dictated.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> Two requests, each to the one paired server, each carrying
/// the device token as a bearer header. A GET with no body, to learn whether AI Chat is on and the
/// owner may use it. A POST of the text the moderator dictated, and the
/// id of the conversation it belongs to when it is a follow-up. The answer comes back as a stream and
/// is read as it arrives.</para>
/// <para><strong>What it does not send.</strong> Not a sound: only text, and only text that was
/// already written out on this machine. Nothing about the moderator's machine, and nothing about any
/// other conversation. The server's side acts as the device's owner and can reach nothing but this one
/// conversation.</para>
/// <para><strong>Nothing that comes back is a command.</strong> The answer is text for the panel to
/// show. It is never run, opened or followed.</para>
/// </remarks>
public sealed class HttpChatClient : IChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HttpChatClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.ChatEndpoint());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // A revoked or refused device is told it may not: the button goes, it is not "unknown".
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return false;

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("available", out var available)
                && available.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? available.GetBoolean()
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException
                                      || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    public async Task<ChatSendResult> SendAsync(
        ServerPairing pairing,
        string text,
        Guid? conversationId,
        Action<string> onAnswer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.ChatMessagesEndpoint())
        {
            Content = JsonContent.Create(new SendBody(conversationId, text), options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        Guid? known = conversationId;

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                return new ChatSendResult(
                    ProblemFor(response.StatusCode, error),
                    known,
                    ConversationGone: response.StatusCode is HttpStatusCode.NotFound);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(body);

            var answer = string.Empty;
            string? finished = null;
            var done = false;

            await foreach (var e in ServerEvents.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                switch (e.Name)
                {
                    case "conversation":
                        if (Id(e.Data) is { } id)
                            known = id;
                        break;

                    case "text":
                        if (Text(e.Data) is { Length: > 0 } more)
                        {
                            answer += more;
                            onAnswer(answer);
                        }
                        break;

                    case "message":
                        var message = Assistant(e.Data);
                        if (message is { } said)
                        {
                            // The AI asked to look something up: what it wrote so far was a lead-in,
                            // and the reply it writes next starts over. Otherwise this is the whole
                            // reply, which is the truth if a piece of the stream was lost.
                            answer = said.AsksForTools ? string.Empty : said.Content;
                            if (said.AsksForTools || answer.Length > 0)
                                onAnswer(answer);
                        }
                        break;

                    case "done":
                        done = true;
                        finished = Outcome(e.Data);
                        break;
                }
            }

            if (!done)
                return new ChatSendResult(ChatProblems.Unreachable, known);

            return finished switch
            {
                "Answered" => new ChatSendResult(answer.Length > 0 ? null : ChatProblems.Failed, known),
                "Cancelled" => new ChatSendResult(ChatProblems.Unreachable, known),
                _ => new ChatSendResult(ChatProblems.Failed, known),
            };
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
                                      || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ChatSendResult(ChatProblems.Unreachable, known);
        }
    }

    /// <summary>The one line for a refusal, from its status and the server's own short message.</summary>
    public static string ProblemFor(HttpStatusCode status, string? serverSaid)
    {
        var said = serverSaid ?? string.Empty;

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ChatProblems.NotAllowed,
            HttpStatusCode.TooManyRequests => ChatProblems.SpendLimit,
            HttpStatusCode.Conflict when said.Contains("full", StringComparison.OrdinalIgnoreCase) => ChatProblems.Full,
            HttpStatusCode.Conflict => ChatProblems.Off,
            HttpStatusCode.BadRequest when said.Contains("at most", StringComparison.OrdinalIgnoreCase) => ChatProblems.TooLong,
            HttpStatusCode.NotFound => ChatProblems.Failed,
            HttpStatusCode.BadRequest => ChatProblems.Failed,
            _ => ChatProblems.Unreachable,
        };
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            foreach (var name in new[] { "error", "message" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or IOException or InvalidOperationException)
        {
            // An error with no readable reason is still an error: the status says enough.
        }

        return null;
    }

    private static Guid? Id(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid) ? guid : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Outcome(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String
                ? outcome.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct AssistantMessage(string Content, bool AsksForTools);

    /// <summary>What the AI wrote, when this stored message is one of its own; null for anything else.</summary>
    private static AssistantMessage? Assistant(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (!root.TryGetProperty("role", out var role) || role.GetString() != "assistant")
                return null;

            var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;

            var asks = root.TryGetProperty("toolCalls", out var calls)
                && calls.ValueKind == JsonValueKind.Array
                && calls.GetArrayLength() > 0;

            return new AssistantMessage(content, asks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SendBody(Guid? ConversationId, string Text);
}
