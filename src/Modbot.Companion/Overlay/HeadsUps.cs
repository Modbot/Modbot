using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.Ingest;
using Modbot.Shared.HeadsUps;

namespace Modbot.Companion.Overlay;

/// <summary>
/// One heads-up standing in the instance the moderator is in, as the server sent it with the
/// roster (heads-ups, 2026-10-03).
/// </summary>
/// <param name="Kind"><c>pin</c>, <c>keep_an_eye</c>, <c>message</c> or <c>ask_for_help</c>. One this build does not know is not shown.</param>
/// <param name="SubjectName">User-controlled text; hostile input on a display surface.</param>
/// <param name="Text">Another moderator's words. Drawn as text, never read as anything else.</param>
/// <param name="PlacedBy">Who placed it.</param>
/// <param name="Mine">Placed from this device, so it raises no card here.</param>
public sealed record HeadsUp(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("subjectName")] string? SubjectName,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("place")] string? Place,
    [property: JsonPropertyName("placedBy")] string PlacedBy,
    [property: JsonPropertyName("placedAt")] DateTimeOffset PlacedAt,
    [property: JsonPropertyName("mine")] bool Mine = false)
{
    /// <summary>The kind, or null for one this build does not know.</summary>
    [JsonIgnore]
    public HeadsUpKind? KindOrNull => HeadsUpRules.Parse(Kind);

    /// <summary>The line that says what it is about: the person, the place, or nothing for the instance.</summary>
    [JsonIgnore]
    public string? About => KindOrNull is HeadsUpKind.AskForHelp ? Place : SubjectName ?? SubjectId;
}

/// <summary>
/// A heads-up being written on the panel, before Place is pressed. Nothing of it leaves the PC
/// until then.
/// </summary>
/// <param name="SubjectId">The roster row it was opened from.</param>
/// <param name="SubjectName">That row's name, as the roster shows it.</param>
/// <param name="Kind">Which kind is picked.</param>
/// <param name="Place">For Ask for help, the place picked, or null.</param>
/// <param name="Text">What has been typed so far.</param>
/// <param name="Problem">Why the last Place was refused, in words, or null.</param>
/// <param name="Sending">Place was pressed and the answer has not come back.</param>
/// <param name="OnInstance">For a Message: about the instance rather than the row's person.</param>
public sealed record HeadsUpDraft(
    string SubjectId,
    string? SubjectName,
    HeadsUpKind Kind = HeadsUpKind.Message,
    string? Place = null,
    string Text = "",
    string? Problem = null,
    bool Sending = false,
    bool OnInstance = false)
{
    /// <summary>Whether the person goes with it: only for the kinds that are about a person.</summary>
    public bool AboutPerson => HeadsUpRules.TakesPerson(Kind) && !(Kind is HeadsUpKind.Message && OnInstance);

    /// <summary>Why it cannot be placed yet, or null when Place can be pressed.</summary>
    public string? Missing => HeadsUpRules.Problem(Kind, SubjectId, HeadsUpRules.Clean(Text), Place);
}

/// <summary>How placing or clearing a heads-up ended.</summary>
public enum HeadsUpSendOutcome
{
    Done,

    /// <summary>The server said no, and said why.</summary>
    Refused,

    /// <summary>The token was rejected. Terminal for this pairing.</summary>
    Unauthorised,

    /// <summary>The server could not be reached. Nothing was placed.</summary>
    Unreachable,
}

/// <param name="Message">The server's reason, for <see cref="HeadsUpSendOutcome.Refused"/>.</param>
public sealed record HeadsUpSent(HeadsUpSendOutcome Outcome, string? Message = null);

/// <summary>Placing and clearing heads-ups: the only two writes the overlay makes.</summary>
public interface IHeadsUpClient
{
    Task<HeadsUpSent> PlaceAsync(ServerPairing pairing, string instanceId, string? worldId, HeadsUpDraft draft, CancellationToken cancellationToken);

    Task<HeadsUpSent> ClearAsync(ServerPairing pairing, string id, CancellationToken cancellationToken);
}

/// <summary>
/// Sends a heads-up when the moderator presses Place, and a clear when they press Clear.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> One POST per press, to the paired server whose instance
/// the moderator is standing in, with the device token: the instance's number and world, the kind,
/// the person the row was for when the kind is about a person (their VRChat id and the name the
/// roster showed), the place for Ask for help, and the words typed. A Clear sends only which
/// heads-up. Nothing is sent on its own, ahead of a press, or to any other server.</para>
/// <para><strong>Who sees it.</strong> The server shows it to the staff whose companions are in
/// the same instance, with that instance's roster, and to nobody else: not the website, not the
/// audit log, not Discord.</para>
/// </remarks>
public sealed class HttpHeadsUpClient : IHeadsUpClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HttpHeadsUpClient(HttpClient http) => _http = http;

    public Task<HeadsUpSent> PlaceAsync(
        ServerPairing pairing,
        string instanceId,
        string? worldId,
        HeadsUpDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(draft);

        var body = new PlaceBody(
            instanceId,
            worldId,
            HeadsUpRules.Word(draft.Kind),
            draft.AboutPerson ? draft.SubjectId : null,
            draft.AboutPerson ? draft.SubjectName : null,
            HeadsUpRules.Clean(draft.Text),
            draft.Kind is HeadsUpKind.AskForHelp ? draft.Place : null);

        return SendAsync(pairing, pairing.HeadsUpsEndpoint(), JsonContent.Create(body, options: Json), cancellationToken);
    }

    public Task<HeadsUpSent> ClearAsync(ServerPairing pairing, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        return SendAsync(pairing, pairing.ClearHeadsUpEndpoint(id), null, cancellationToken);
    }

    private async Task<HeadsUpSent> SendAsync(ServerPairing pairing, Uri endpoint, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new HeadsUpSent(HeadsUpSendOutcome.Done);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
                return new HeadsUpSent(HeadsUpSendOutcome.Unauthorised);

            // A refusal says why; anything else is the server not being there.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                var reason = await ReadReasonAsync(response, cancellationToken).ConfigureAwait(false);
                return new HeadsUpSent(HeadsUpSendOutcome.Refused, reason ?? "Not placed.");
            }

            return new HeadsUpSent(HeadsUpSendOutcome.Unreachable);
        }
        catch (HttpRequestException)
        {
            return new HeadsUpSent(HeadsUpSendOutcome.Unreachable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HeadsUpSent(HeadsUpSendOutcome.Unreachable);
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

    private sealed record PlaceBody(
        string InstanceId,
        string? WorldId,
        string Kind,
        string? SubjectId,
        string? SubjectName,
        string? Text,
        string? Place);

    private sealed record ErrorBody(string? Code, string? Message);
}
