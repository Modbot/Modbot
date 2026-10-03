using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modbot.TestSupport;

/// <summary>
/// Google's token endpoint and Calendar API, answered from a script. Records every request with its
/// method, address and form body, so a test can check exactly what was sent, and what was not.
/// </summary>
/// <remarks>
/// Any request it has no answer for is a 500 and is still recorded, so a test that expects "only
/// these calls" sees the stray one.
/// </remarks>
public sealed class FakeGoogle : HttpMessageHandler
{
    public List<Recorded> Requests { get; } = [];

    /// <summary>The token endpoint's answer. 200 gives <see cref="AccessToken"/>.</summary>
    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The token endpoint's body when <see cref="TokenStatus"/> is not 200.</summary>
    public string TokenError { get; set; } = """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""";

    public string AccessToken { get; set; } = "ya29.test-access-token";

    /// <summary><c>events.list</c>'s answer. 200 gives the three fields below.</summary>
    public HttpStatusCode CalendarStatus { get; set; } = HttpStatusCode.OK;

    public string CalendarError { get; set; } = """{"error":{"code":404,"message":"Not Found","errors":[{"reason":"notFound"}]}}""";

    public string CalendarName { get; set; } = "Group events";

    public string TimeZone { get; set; } = "Europe/London";

    public string AccessRole { get; set; } = "writer";

    /// <summary><c>acl.list</c>'s answer. 200 gives a public sharing entry with <see cref="PublicRole"/>.</summary>
    public HttpStatusCode AclStatus { get; set; } = HttpStatusCode.OK;

    public string AclError { get; set; } = """{"error":{"code":403,"message":"Forbidden","errors":[{"reason":"forbidden"}]}}""";

    /// <summary>The role of the sharing entry for everyone, or null for none.</summary>
    public string? PublicRole { get; set; } = "reader";

    /// <summary>Seconds, for <c>Retry-After</c> on a refusal. Null sends none.</summary>
    public int? RetryAfterSeconds { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;

        Requests.Add(new Recorded(request.Method, uri, Form(body), request.Headers.Authorization?.ToString(), body));

        if (request.Method == HttpMethod.Post && uri.Host == "oauth2.googleapis.com" && uri.AbsolutePath == "/token")
        {
            TokenRequests++;
            return TokenStatus == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken}}","expires_in":3599,"token_type":"Bearer"}""")
                : Json(TokenStatus, TokenError);
        }

        // Event calls; the bare list read with GET is Check's, answered below as before.
        if (uri.Host == "www.googleapis.com"
            && EventPath(uri) is { } eventCall
            && (eventCall.EventId is not null || request.Method == HttpMethod.Post))
        {
            if (Answer?.Invoke(request.Method, eventCall) is { } scripted)
                return scripted;

            if (RefuseTokenOnce)
            {
                RefuseTokenOnce = false;
                return Json(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"Invalid Credentials","errors":[{"reason":"authError"}]}}""");
            }

            if (request.Headers.Authorization?.Parameter != AccessToken)
                return Json(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"Invalid Credentials","errors":[{"reason":"authError"}]}}""");

            var response = EventAnswer(request.Method, eventCall, body);

            // The call went through on Google's side, and the answer never came back.
            if (DropAnswer?.Invoke(request.Method, eventCall) == true)
            {
                response.Dispose();
                throw new HttpRequestException("The answer was lost.");
            }

            return response;
        }

        if (request.Method == HttpMethod.Get && uri.Host == "www.googleapis.com" && uri.AbsolutePath.StartsWith("/calendar/v3/calendars/", StringComparison.Ordinal))
        {
            if (request.Headers.Authorization?.Parameter != AccessToken)
                return Json(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"Invalid Credentials","errors":[{"reason":"authError"}]}}""");

            if (uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
            {
                return CalendarStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { summary = CalendarName, timeZone = TimeZone, accessRole = AccessRole }))
                    : Json(CalendarStatus, CalendarError);
            }

            if (uri.AbsolutePath.EndsWith("/acl", StringComparison.Ordinal))
            {
                if (AclStatus != HttpStatusCode.OK)
                    return Json(AclStatus, AclError);

                var items = new List<object> { new { scope = new { type = "user" }, role = "owner" } };
                if (PublicRole is not null)
                    items.Add(new { scope = new { type = "default" }, role = PublicRole });

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items }));
            }
        }

        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    private HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        if (status != HttpStatusCode.OK && RetryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

        return response;
    }

    private static Dictionary<string, string> Form(string? body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(body) || body.StartsWith('{'))
            return form;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            form[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }

        return form;
    }

    /// <param name="Form">The form fields of a token request; empty for anything else.</param>
    /// <param name="Body">The request's body as sent, when it had one.</param>
    public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Form, string? Authorization, string? Body = null)
    {
        /// <summary>The request was to Google's Calendar API rather than the token endpoint.</summary>
        public bool IsCalendarApi => Uri.Host == "www.googleapis.com";
    }

    // ── Events (Google Calendar design §3.4, step 2) ─────────────────────────────────────

    /// <summary>How many token requests were made.</summary>
    public int TokenRequests { get; private set; }

    /// <summary>
    /// The events on each calendar, by calendar id and event id, as Google keeps them: a deleted
    /// event stays, with <c>status</c> <c>cancelled</c>.
    /// </summary>
    public Dictionary<(string Calendar, string Id), JsonObject> Events { get; } = [];

    /// <summary>Every write to one date of a series: the instance id and the body sent.</summary>
    public List<(string Calendar, string InstanceId, JsonObject Body)> InstanceWrites { get; } = [];

    /// <summary>When set, the series has no date to return from <c>events.instances</c>.</summary>
    public bool NoInstances { get; set; }

    /// <summary>Answers the next Calendar API event call with 401, once.</summary>
    public bool RefuseTokenOnce { get; set; }

    /// <summary>A scripted answer for an event call, before the store is asked. Null lets the store answer.</summary>
    public Func<HttpMethod, EventCall, HttpResponseMessage?>? Answer { get; set; }

    /// <summary>For an event call the store answers: true does it and then loses the answer.</summary>
    public Func<HttpMethod, EventCall, bool>? DropAnswer { get; set; }

    /// <summary>The event calls made, in order.</summary>
    public List<Recorded> EventCalls =>
        [.. Requests.Where(r => r.IsCalendarApi && EventPath(r.Uri) is { } call && (call.EventId is not null || r.Method == HttpMethod.Post))];

    /// <summary>One call on a calendar's events.</summary>
    /// <param name="EventId">The event or instance id; null for the calendar's whole list.</param>
    /// <param name="Instances">A <c>events/{id}/instances</c> read.</param>
    public sealed record EventCall(string Calendar, string? EventId, bool Instances, Uri Uri);

    /// <summary>A Google error body with a reason.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string reason, string message = "Refused", int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { code = (int)status, message, errors = new[] { new { reason } } } }),
                Encoding.UTF8,
                "application/json"),
        };

        if (retryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

        return response;
    }

    /// <summary>
    /// <c>calendars/{calendar}/events[/{id}[/instances]]</c>, except the bare list Check reads, which
    /// is answered as before.
    /// </summary>
    private static EventCall? EventPath(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // calendar, v3, calendars, {calendar}, events, {id}, instances
        if (segments.Length < 5 || segments[0] != "calendar" || segments[2] != "calendars" || segments[4] != "events")
            return null;

        var calendar = Uri.UnescapeDataString(segments[3]);
        var id = segments.Length > 5 ? Uri.UnescapeDataString(segments[5]) : null;
        return new EventCall(calendar, id, segments.Length > 6 && segments[6] == "instances", uri);
    }

    private HttpResponseMessage EventAnswer(HttpMethod method, EventCall call, string? body)
    {
        var key = (call.Calendar, call.EventId ?? string.Empty);

        if (call.Instances)
        {
            if (NoInstances || !Events.TryGetValue(key, out var series) || Status(series) == "cancelled")
                return Json(HttpStatusCode.OK, """{"items":[]}""");

            var query = call.Uri.Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

            var planned = DateTimeOffset.Parse(query["originalStart"], System.Globalization.CultureInfo.InvariantCulture);
            var instanceId = $"{call.EventId}_{planned.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";

            var instance = new JsonObject
            {
                ["id"] = instanceId,
                ["status"] = "confirmed",
                ["recurringEventId"] = call.EventId,
                ["originalStartTime"] = new JsonObject { ["dateTime"] = planned.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
                ["summary"] = series["summary"]?.DeepClone(),
                ["start"] = new JsonObject { ["dateTime"] = planned.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
            };

            return Json(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray(instance) }.ToJsonString());
        }

        if (method == HttpMethod.Post)
        {
            var sent = (JsonObject)JsonNode.Parse(body!)!;
            var id = sent["id"]!.GetValue<string>();

            if (Events.ContainsKey((call.Calendar, id)))
                return Error(HttpStatusCode.Conflict, "duplicate", "The requested identifier already exists.");

            Events[(call.Calendar, id)] = sent;
            return Json(HttpStatusCode.OK, Answered(sent, id));
        }

        // An instance id: one date of a series.
        if (call.EventId!.Contains('_', StringComparison.Ordinal) && method == HttpMethod.Put)
        {
            InstanceWrites.Add((call.Calendar, call.EventId, (JsonObject)JsonNode.Parse(body!)!));
            return Json(HttpStatusCode.OK, body!);
        }

        if (!Events.TryGetValue(key, out var stored))
            return Error(HttpStatusCode.NotFound, "notFound", "Not Found");

        if (method == HttpMethod.Get)
            return Json(HttpStatusCode.OK, Answered(stored, call.EventId!));

        if (Status(stored) == "cancelled")
            return Error(HttpStatusCode.Gone, "deleted", "Resource has been deleted");

        if (method == HttpMethod.Put)
        {
            Events[key] = (JsonObject)JsonNode.Parse(body!)!;
            return Json(HttpStatusCode.OK, Answered(Events[key], call.EventId!));
        }

        if (method == HttpMethod.Delete)
        {
            stored["status"] = "cancelled";
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>The event's address on Google, as Google answers it in <c>htmlLink</c>.</summary>
    public static string LinkOf(string eventId) => "https://www.google.com/calendar/event?eid=" + Uri.EscapeDataString(eventId);

    /// <summary>
    /// An event as Google answers it: what is stored, with the <c>htmlLink</c> Google adds. The
    /// stored copy stays what Modbot sent.
    /// </summary>
    private static string Answered(JsonObject googleEvent, string eventId)
    {
        var answer = (JsonObject)googleEvent.DeepClone();
        answer["htmlLink"] = LinkOf(eventId);
        return answer.ToJsonString();
    }

    private static string? Status(JsonObject googleEvent) =>
        googleEvent["status"] is JsonValue value && value.TryGetValue<string>(out var status) ? status : null;

    /// <summary>
    /// A service account key file made for a test: a fresh 2048-bit RSA key, with the given
    /// <c>token_uri</c> so a test can show it is ignored.
    /// </summary>
    public static (string Json, RSA Key) KeyFile(
        string clientEmail = "modbot@test-project.iam.gserviceaccount.com",
        string keyId = "0123456789abcdef",
        string tokenUri = "https://oauth2.googleapis.com/token",
        string type = "service_account")
    {
        var rsa = RSA.Create(2048);

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = type,
            ["project_id"] = "test-project",
            ["private_key_id"] = keyId,
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = clientEmail,
            ["client_id"] = "1234567890",
            ["auth_uri"] = "https://accounts.google.com/o/oauth2/auth",
            ["token_uri"] = tokenUri,
        });

        return (json, rsa);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients over one handler.</summary>
public sealed class OneHandlerClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
