using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Overlay;

/// <summary>How marking a person's avatar ended.</summary>
public enum MarkAvatarOutcome
{
    /// <summary>The avatar is on the server's list: just added, or already there.</summary>
    Done,

    /// <summary>The server said no, and said why.</summary>
    Refused,

    /// <summary>The token was rejected. Terminal for this pairing.</summary>
    Unauthorised,

    /// <summary>The server could not be reached. Nothing was marked.</summary>
    Unreachable,
}

/// <param name="Message">The server's reason, for <see cref="MarkAvatarOutcome.Refused"/>.</param>
public sealed record AvatarMarked(MarkAvatarOutcome Outcome, string? Message = null);

/// <summary>Mark avatar on a person's card.</summary>
public interface IProblemAvatarClient
{
    Task<AvatarMarked> MarkAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken);
}

/// <summary>
/// Sends Mark avatar when the moderator presses it on a person's card.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> One POST per press, to the paired server, with the device
/// token: the VRChat id of the person whose card is open. The server looks up the avatar it last saw
/// them wear and adds that name to its problem avatar list, as the moderator whose companion this is,
/// and only if they may manage settings. No avatar name travels from here, so nothing a log line or a
/// card says can put a different name on the list.</para>
/// <para>Nothing is sent on its own, ahead of a press, or to any other server.</para>
/// </remarks>
public sealed class HttpProblemAvatarClient : IProblemAvatarClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HttpProblemAvatarClient(HttpClient http) => _http = http;

    public async Task<AvatarMarked> MarkAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        // A made-up person from the Debug page is never sent to a server (TestPeople).
        if (TestPeople.IsTest(subjectId))
            return new AvatarMarked(MarkAvatarOutcome.Refused, "Not marked: test person.");

        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.ProblemAvatarsEndpoint())
        {
            Content = JsonContent.Create(new MarkBody(subjectId), options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new AvatarMarked(MarkAvatarOutcome.Done);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
                return new AvatarMarked(MarkAvatarOutcome.Unauthorised);

            // A refusal says why; anything else is the server not being there.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                var reason = await ReadReasonAsync(response, cancellationToken).ConfigureAwait(false);
                return new AvatarMarked(MarkAvatarOutcome.Refused, reason ?? "Not marked.");
            }

            return new AvatarMarked(MarkAvatarOutcome.Unreachable);
        }
        catch (HttpRequestException)
        {
            return new AvatarMarked(MarkAvatarOutcome.Unreachable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AvatarMarked(MarkAvatarOutcome.Unreachable);
        }
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, cancellationToken).ConfigureAwait(false);
            return error?.Message is { Length: > 0 and <= 200 } message ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record MarkBody([property: JsonPropertyName("vrchatUserId")] string VRChatUserId);

    private sealed record ErrorBody(string? Code, string? Message);
}
