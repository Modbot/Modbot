using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modbot.Core.Google;

/// <summary>What Check reads about a calendar from <c>events.list</c> (Google Calendar design §3.1).</summary>
/// <param name="Name">The calendar's name (<c>summary</c>).</param>
/// <param name="TimeZone">The calendar's IANA time zone.</param>
/// <param name="AccessRole">
/// Modbot's role on it: <c>none</c>, <c>freeBusyReader</c>, <c>reader</c>, <c>writer</c> or <c>owner</c>.
/// </param>
public sealed record GoogleCalendarInfo(string? Name, string? TimeZone, string? AccessRole)
{
    /// <summary>Whether Modbot may make and change events: <c>writer</c> or <c>owner</c>.</summary>
    public bool CanChangeEvents => AccessRole is "writer" or "owner";

    /// <summary>Whether Modbot may read events at all.</summary>
    public bool CanRead => AccessRole is "reader" or "writer" or "owner";
}

/// <summary>Whether a calendar is public, from who it is shared with.</summary>
public static class GooglePublic
{
    /// <summary>Public, with every event's details.</summary>
    public const string All = "all";

    /// <summary>Public, but showing only when the calendar is busy.</summary>
    public const string FreeBusy = "freeBusy";

    /// <summary>Not shared with the public.</summary>
    public const string No = "no";
}

/// <summary>
/// Google Calendar's API, called with plain <see cref="HttpClient"/> and System.Text.Json (Google
/// Calendar design §3.11). One per process: a singleton.
/// </summary>
/// <remarks>
/// <para>
/// The two reads Check needs, and the event calls the sending loop makes (step 2). Every call goes to <see cref="ApiAddress"/> on the named
/// client <see cref="HttpClientName"/>, which follows no redirects and uses no proxy.
/// </para>
/// <para>
/// The calendar id is text somebody typed, and it goes into the path, so it is escaped with
/// <see cref="Uri.EscapeDataString"/>: a <c>/</c> in it becomes <c>%2F</c> and cannot climb out of
/// the calendar's own path. An id of only dots is refused outright.
/// </para>
/// <para>
/// Nothing here retries. A rate limit comes back as a <see cref="GoogleFailure"/> that
/// <see cref="GoogleFailure.StopsTheLane"/>, and the caller stops (CLAUDE.md: never retry a 429).
/// </para>
/// </remarks>
public sealed class GoogleCalendarClient(IHttpClientFactory http)
{
    /// <summary>The name of the <see cref="HttpClient"/> every Google call is made on.</summary>
    public const string HttpClientName = "google";

    /// <summary>The Calendar API's base address.</summary>
    public const string ApiAddress = "https://www.googleapis.com/calendar/v3/";

    /// <summary>The most pages of sharing entries read. Google allows 750 shares; a page holds 250.</summary>
    private const int MostSharingPages = 4;

    /// <summary>
    /// The calendar's name, time zone and Modbot's role on it: <c>events.list</c> asked for those
    /// three fields and at most one event, which needs no scope beyond reading events.
    /// </summary>
    public async Task<GoogleResult<GoogleCalendarInfo>> CalendarAsync(string accessToken, string calendarId, CancellationToken ct)
    {
        var path = CalendarPath(calendarId) + "/events?maxResults=1&fields=summary%2CtimeZone%2CaccessRole";
        var answer = await GetAsync(accessToken, path, GoogleJson.Default.EventsListAnswer, ct);

        return answer.Value is { } list
            ? GoogleResult<GoogleCalendarInfo>.Ok(new GoogleCalendarInfo(list.Summary, list.TimeZone, list.AccessRole))
            : GoogleResult<GoogleCalendarInfo>.Failed(answer.Failure!);
    }

    /// <summary>
    /// Whether the calendar is shared with the public (<see cref="GooglePublic"/>): a sharing entry
    /// for everyone (<c>scope.type</c> <c>default</c>) as <c>reader</c> is public with details, as
    /// <c>freeBusyReader</c> is free or busy only, and none is not public.
    /// </summary>
    public async Task<GoogleResult<string>> PublicAsync(string accessToken, string calendarId, CancellationToken ct)
    {
        string? pageToken = null;

        for (var page = 0; page < MostSharingPages; page++)
        {
            var path = CalendarPath(calendarId) + "/acl?maxResults=250&fields=items(scope(type)%2Crole)%2CnextPageToken"
                + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));

            var answer = await GetAsync(accessToken, path, GoogleJson.Default.AclListAnswer, ct);

            if (answer.Value is not { } list)
                return GoogleResult<string>.Failed(answer.Failure!);

            foreach (var rule in list.Items ?? [])
            {
                if (rule.Scope?.Type != "default")
                    continue;

                return GoogleResult<string>.Ok(rule.Role switch
                {
                    "reader" or "writer" or "owner" => GooglePublic.All,
                    "freeBusyReader" => GooglePublic.FreeBusy,
                    _ => GooglePublic.No,
                });
            }

            if (string.IsNullOrEmpty(list.NextPageToken))
                break;

            pageToken = list.NextPageToken;
        }

        return GoogleResult<string>.Ok(GooglePublic.No);
    }

    // ── Events (Google Calendar design §3.4, step 2) ─────────────────────────────────────
    //
    // Bodies and answers are plain JSON objects: a PUT replaces the whole event (Google's update
    // has no patch meaning), and one date of a series is changed by sending back the instance as
    // Google gave it, with only its times, words or status changed. Nothing here retries.

    /// <summary><c>events.get</c>: one event by the id Modbot gave it.</summary>
    public Task<GoogleResult<JsonObject>> GetEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, accessToken, EventPath(calendarId, eventId), null, ct);

    /// <summary><c>events.insert</c>, with the id in the body. Nobody is told: there are no attendees.</summary>
    public Task<GoogleResult<JsonObject>> InsertEventAsync(string accessToken, string calendarId, JsonObject body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, accessToken, CalendarPath(calendarId) + "/events?sendUpdates=none", body, ct);

    /// <summary><c>events.update</c>: the whole event, or one date of a series by its instance id.</summary>
    public Task<GoogleResult<JsonObject>> UpdateEventAsync(
        string accessToken, string calendarId, string eventId, JsonObject body, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, accessToken, EventPath(calendarId, eventId) + "?sendUpdates=none", body, ct);

    /// <summary><c>events.delete</c>. A 404 or 410 is the caller's to read as already gone.</summary>
    public Task<GoogleResult<JsonObject>> DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, accessToken, EventPath(calendarId, eventId) + "?sendUpdates=none", null, ct);

    /// <summary>
    /// <c>events.instances</c> for the one date of a series that was planned at
    /// <paramref name="originalStart"/>. The value is that date as Google holds it, or null when the
    /// series has no such date (cancelled, or never in it).
    /// </summary>
    public async Task<GoogleResult<JsonObject?>> InstanceAsync(
        string accessToken, string calendarId, string eventId, DateTimeOffset originalStart, CancellationToken ct)
    {
        var path = EventPath(calendarId, eventId) + "/instances?maxResults=1&originalStart="
            + Uri.EscapeDataString(originalStart.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

        var answer = await SendAsync(HttpMethod.Get, accessToken, path, null, ct);

        if (answer.Value is not { } list)
            return GoogleResult<JsonObject?>.Failed(answer.Failure!);

        var first = list["items"] is JsonArray items && items.Count > 0 ? items[0] as JsonObject : null;
        return GoogleResult<JsonObject?>.Ok(first);
    }

    /// <summary><c>calendars/{calendar}/events/{event}</c>, both escaped so each stays one path segment.</summary>
    public static string EventPath(string calendarId, string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        if (eventId.Trim('.').Length == 0)
            throw new ArgumentException("Not an event id.", nameof(eventId));

        return CalendarPath(calendarId) + "/events/" + Uri.EscapeDataString(eventId);
    }

    private async Task<GoogleResult<JsonObject>> SendAsync(
        HttpMethod method, string accessToken, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(ApiAddress), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return GoogleResult<JsonObject>.Failed(GoogleErrors.FromCalendar(response.StatusCode, text, response.Headers.RetryAfter));

            // A delete answers 204 with nothing in it.
            if (string.IsNullOrWhiteSpace(text))
                return GoogleResult<JsonObject>.Ok(new JsonObject());

            return JsonNode.Parse(text) is JsonObject value
                ? GoogleResult<JsonObject>.Ok(value)
                : GoogleResult<JsonObject>.Failed(new GoogleFailure(GoogleProblem.Other, (int)response.StatusCode));
        }
        catch (HttpRequestException)
        {
            return GoogleResult<JsonObject>.Failed(GoogleErrors.NoAnswer());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return GoogleResult<JsonObject>.Failed(GoogleErrors.NoAnswer());
        }
        catch (JsonException)
        {
            return GoogleResult<JsonObject>.Failed(GoogleErrors.NoAnswer());
        }
    }

    /// <summary>
    /// <c>calendars/{id}</c>, with the id escaped so it stays one path segment.
    /// </summary>
    public static string CalendarPath(string calendarId)
    {
        if (!GoogleCalendarIds.IsUsable(calendarId))
            throw new ArgumentException("Not a calendar id.", nameof(calendarId));

        return "calendars/" + Uri.EscapeDataString(calendarId);
    }

    private async Task<GoogleResult<T>> GetAsync<T>(
        string accessToken, string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ApiAddress), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                return GoogleResult<T>.Failed(GoogleErrors.FromCalendar(response.StatusCode, text, response.Headers.RetryAfter));
            }

            var value = await response.Content.ReadFromJsonAsync(type, ct);

            return value is null
                ? GoogleResult<T>.Failed(new GoogleFailure(GoogleProblem.Other, (int)response.StatusCode))
                : GoogleResult<T>.Ok(value);
        }
        catch (HttpRequestException)
        {
            return GoogleResult<T>.Failed(GoogleErrors.NoAnswer());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return GoogleResult<T>.Failed(GoogleErrors.NoAnswer());
        }
        catch (JsonException)
        {
            return GoogleResult<T>.Failed(GoogleErrors.NoAnswer());
        }
    }
}

/// <summary>Registers the Google sign-in and the Calendar client.</summary>
public static class GoogleServices
{
    /// <summary>
    /// The named client follows no redirects and uses no proxy: Google's hosts are fixed, and the
    /// VRChat egress proxy is for VRChat only. Answers are capped at 1 MB.
    /// </summary>
    public static IServiceCollection AddGoogleCalendar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(GoogleCalendarClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            });

        services.TryAddSingleton<GoogleSignIn>();
        services.TryAddSingleton<GoogleCalendarClient>();

        return services;
    }
}

internal sealed record EventsListAnswer(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("accessRole")] string? AccessRole);

internal sealed record AclScope([property: JsonPropertyName("type")] string? Type);

internal sealed record AclRule(
    [property: JsonPropertyName("scope")] AclScope? Scope,
    [property: JsonPropertyName("role")] string? Role);

internal sealed record AclListAnswer(
    [property: JsonPropertyName("items")] List<AclRule>? Items,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

[JsonSerializable(typeof(JwtHead))]
[JsonSerializable(typeof(JwtBody))]
[JsonSerializable(typeof(TokenAnswer))]
[JsonSerializable(typeof(EventsListAnswer))]
[JsonSerializable(typeof(AclListAnswer))]
internal sealed partial class GoogleJson : JsonSerializerContext;
