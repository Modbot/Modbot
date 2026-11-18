using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Feedback;

/// <summary>
/// Feedback's way to the paired server: asks whether this device's owner may send feedback, has the
/// words they spoke tidied, and saves the feedback they confirmed.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> Three requests, each to the one paired server, each carrying
/// the device token as a bearer header. A GET with no body, to learn whether the owner may send feedback.
/// A POST of the text the person spoke, to be tidied. A POST of the kind, title and details the person
/// confirmed on the draft, to be saved.</para>
/// <para><strong>What it does not send.</strong> Not a sound: only text, and only text that was already
/// written out on this machine. Nothing about the person's machine. Never Modbot Cloud and never GitHub:
/// the feedback is kept on the server the companion is paired with and nowhere else.</para>
/// <para><strong>Nothing that comes back is a command.</strong> A draft is text for the person to read
/// and change. It is never run, opened or followed.</para>
/// </remarks>
public sealed class HttpFeedbackClient : IFeedbackClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string CouldNotSend = "Could not send. Try again.";
    public const string NotAllowed = "You may not send feedback.";
    public const string TooLong = "That is too long.";

    private readonly HttpClient _http;

    public HttpFeedbackClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, pairing.FeedbackEndpoint(), pairing);

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
        catch (Exception e) when (IsTransient(e, cancellationToken))
        {
            return null;
        }
    }

    public async Task<FeedbackDraft?> TidyAsync(ServerPairing pairing, string text, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, pairing.FeedbackTidyEndpoint(), pairing);
        request.Content = JsonContent.Create(new TidyBody(text), options: Json);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var details = Text(root, "details");
            if (string.IsNullOrWhiteSpace(details))
                return null;

            return new FeedbackDraft(
                FeedbackKinds.Known(Text(root, "kind")),
                Text(root, "title"),
                details,
                root.TryGetProperty("tidied", out var tidied) && tidied.ValueKind is JsonValueKind.True);
        }
        catch (Exception e) when (IsTransient(e, cancellationToken))
        {
            return null;
        }
    }

    public async Task<FeedbackSendResult> SendAsync(
        ServerPairing pairing,
        string kind,
        string title,
        string details,
        CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, pairing.FeedbackEndpoint(), pairing);
        request.Content = JsonContent.Create(new SendBody(kind, title, details), options: Json);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return new FeedbackSendResult(
                response.StatusCode switch
                {
                    _ when response.IsSuccessStatusCode => null,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => NotAllowed,
                    HttpStatusCode.BadRequest => TooLong,
                    _ => CouldNotSend,
                });
        }
        catch (Exception e) when (IsTransient(e, cancellationToken))
        {
            return new FeedbackSendResult(CouldNotSend);
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri address, ServerPairing pairing)
    {
        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);
        return request;
    }

    private static bool IsTransient(Exception e, CancellationToken cancellationToken)
        => e is HttpRequestException or JsonException or InvalidOperationException
            || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record TidyBody(string Text);

    private sealed record SendBody(string Kind, string Title, string Details);
}
