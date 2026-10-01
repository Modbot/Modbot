using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Modbot.Api.Conventions;

/// <summary>
/// The one shape every error from the HTTP API has: <c>application/problem+json</c> (RFC 9457)
/// with a stable <c>code</c> a program can branch on (API conventions design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Made in one place, not at each endpoint.</strong> Several hundred endpoints answered
/// <c>{ error }</c>, a few answered ASP.NET's problem shape, and about a hundred answered with no
/// body at all. Rewriting each of them would have touched every feature for no change in what any
/// of them decides, so the endpoint filter (<see cref="ProblemFilter"/>) and the middleware
/// (<see cref="ProblemMiddleware"/>) reshape what they already answer, and an endpoint that knows a
/// more precise code than its status gives says so with <see cref="Of"/>.
/// </para>
/// <para>
/// <strong>The old field stays.</strong> <c>error</c> carries the same sentence as <c>detail</c>
/// for one API version, because the web app and every script written so far read it. Any other
/// field an endpoint put beside it (<c>missingGroupPermission</c>, <c>caseId</c>) is kept as it
/// was: problem+json allows extra members, and those are what the web app reads to say more than
/// a sentence.
/// </para>
/// </remarks>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    /// <summary>Where each code is explained. The code is the anchor.</summary>
    public const string TypeBase = "https://docs.modbot.co/api/errors/#";

    // ── Codes ──────────────────────────────────────────────────────────────────────────────
    //
    // Plain words, lowercase, joined by hyphens. A code is never renamed and never reused for a
    // different meaning: programs compare them. New ones may be added at any time, so a client
    // treats an unknown code by its HTTP status.

    public const string InvalidRequest = "invalid-request";
    public const string NotSignedIn = "not-signed-in";
    public const string NeedsPermission = "needs-permission";
    public const string VRChatNotLinked = "vrchat-not-linked";
    public const string NotFound = "not-found";
    public const string WrongMethod = "wrong-method";
    public const string Conflict = "conflict";
    public const string NoGroup = "no-group";
    public const string Gone = "gone";
    public const string TooLarge = "too-large";
    public const string WrongContentType = "wrong-content-type";
    public const string NotPossible = "not-possible";
    public const string TooManyRequests = "too-many-requests";
    public const string VRChatRateLimited = "vrchat-rate-limited";
    public const string ServerError = "server-error";
    public const string NotAvailable = "not-available";
    public const string VRChatRefused = "vrchat-refused";
    public const string DiscordRefused = "discord-refused";
    public const string ServiceFailed = "service-failed";
    public const string NotSetUp = "not-set-up";
    public const string Unavailable = "unavailable";
    public const string TimedOut = "timed-out";
    public const string Refused = "refused";

    /// <summary>Every code, with its title. The order is the order the errors page lists them in.</summary>
    public static IReadOnlyList<(string Code, int Status, string Title)> All { get; } =
    [
        (InvalidRequest, 400, "Request not valid"),
        (NotSignedIn, 401, "Not signed in"),
        (NeedsPermission, 403, "Needs a permission"),
        (VRChatNotLinked, 403, "VRChat account not linked"),
        (Refused, 403, "Refused"),
        (NotFound, 404, "Not found"),
        (WrongMethod, 405, "Wrong method"),
        (Conflict, 409, "Conflict"),
        (NoGroup, 409, "No VRChat group set up"),
        (Gone, 410, "Gone"),
        (TooLarge, 413, "Too large"),
        (WrongContentType, 415, "Wrong content type"),
        (NotPossible, 422, "Not possible"),
        (TooManyRequests, 429, "Too many requests"),
        (VRChatRateLimited, 429, "VRChat rate limit"),
        (ServerError, 500, "Server error"),
        (NotAvailable, 501, "Not available"),
        (VRChatRefused, 502, "VRChat refused"),
        (DiscordRefused, 502, "Discord refused"),
        (ServiceFailed, 502, "Another service failed"),
        (NotSetUp, 503, "Not set up"),
        (Unavailable, 503, "Unavailable"),
        (TimedOut, 504, "Timed out"),
    ];

    private static readonly Dictionary<string, string> Titles =
        All.ToDictionary(c => c.Code, c => c.Title, StringComparer.Ordinal);

    /// <summary>The code an error has when its endpoint named none: the plainest one for its status.</summary>
    public static string CodeFor(int status) => status switch
    {
        400 => InvalidRequest,
        401 => NotSignedIn,
        403 => NeedsPermission,
        404 => NotFound,
        405 => WrongMethod,
        409 => Conflict,
        410 => Gone,
        413 => TooLarge,
        415 => WrongContentType,
        422 => NotPossible,
        429 => TooManyRequests,
        501 => NotAvailable,
        502 => ServiceFailed,
        503 => Unavailable,
        504 => TimedOut,
        >= 500 => ServerError,
        _ => Refused,
    };

    /// <summary>The short title for a code; a code this list lacks gets its status's.</summary>
    public static string TitleOf(string code, int status)
        => Titles.TryGetValue(code, out var title) ? title : Titles[CodeFor(status)];

    /// <summary>An error with a sentence and, when the status's own code is too coarse, a code of its own.</summary>
    /// <param name="status">The HTTP status, 400 or above.</param>
    /// <param name="sentence">What went wrong, for a person. Becomes <c>detail</c> and <c>error</c>.</param>
    /// <param name="code">One of the codes above; null means the status's own.</param>
    /// <param name="extra">More fields, kept as they are (an anonymous object or a <see cref="JsonObject"/>).</param>
    public static IResult Of(int status, string? sentence, string? code = null, object? extra = null)
        => new ProblemResult(status, sentence, code, extra);

    /// <summary>
    /// Builds the body. <paramref name="original"/> is what the endpoint answered, as JSON; its own
    /// fields are never overwritten, so a <c>detail</c> or <c>title</c> it already had stays.
    /// </summary>
    public static JsonObject Body(int status, string? sentence, string? code, JsonObject? original = null)
    {
        var body = original ?? [];

        // A code an endpoint set in its own body wins over the one it was called with.
        var chosen = body["code"] is JsonValue c && c.TryGetValue<string>(out var own) && own.Length > 0
            ? own
            : code ?? CodeFor(status);

        sentence ??= body["error"] is JsonValue e && e.TryGetValue<string>(out var said) ? said : null;
        sentence ??= body["detail"] is JsonValue d && d.TryGetValue<string>(out var detail) ? detail : null;

        var shaped = new JsonObject
        {
            ["type"] = TypeBase + chosen,
            ["title"] = TitleOf(chosen, status),
            ["status"] = status,
        };

        if (sentence is not null)
            shaped["detail"] = sentence;

        shaped["code"] = chosen;

        // Kept for one API version beside `detail` (see the class remarks).
        if (sentence is not null)
            shaped["error"] = sentence;

        foreach (var (name, value) in body.ToList())
        {
            body.Remove(name);

            // The endpoint's own detail, title and type are its words; keep them.
            if (name is "detail" or "title" or "type" || !shaped.ContainsKey(name))
                shaped[name] = value;
        }

        return shaped;
    }

    /// <summary>
    /// Writes an error straight to the response, for middleware and handlers that answer without
    /// an <see cref="IResult"/>.
    /// </summary>
    public static Task WriteAsync(HttpContext http, int status, string? sentence, string? code = null, JsonObject? extra = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.StatusCode = status;

        if (HttpMethods.IsHead(http.Request.Method))
            return Task.CompletedTask;

        return http.Response.WriteAsJsonAsync(
            Body(status, sentence, code, extra), (JsonSerializerOptions?)null, ContentType, http.RequestAborted);
    }

    /// <summary>
    /// Whether errors on this path take this shape: the API under <c>/api</c>, except the
    /// companion's own protocol, which has its own codes (<c>CompanionApiErrors</c>).
    /// </summary>
    public static bool Covers(PathString path)
    {
        if (!path.StartsWithSegments("/api", out var rest))
            return false;

        // /api/v1/companion/...
        var value = rest.Value;
        return !(value is { Length: > 2 } && value[1] == 'v' && char.IsAsciiDigit(value[2]));
    }

    /// <summary>
    /// The VRChat proxy, whose answers are VRChat's and are passed on untouched, empty or not. Its
    /// own refusals (the proxy off, no permission) are results like any other endpoint's and are
    /// shaped; only an answer with no body is left alone, because there it cannot tell them apart.
    /// </summary>
    public static bool IsPassedOn(PathString path)
        => path.StartsWithSegments("/api/proxy");

    /// <summary>The endpoint's JSON settings, so a reshaped body is spelled like an unshaped one.</summary>
    internal static JsonSerializerOptions JsonOptionsOf(HttpContext http)
        => http.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? JsonSerializerOptions.Web;

    /// <summary>An error made by <see cref="Of"/>.</summary>
    internal sealed class ProblemResult(int status, string? sentence, string? code, object? extra)
        : IResult, IStatusCodeHttpResult, IContentTypeHttpResult
    {
        public int? StatusCode => status;

        string? IContentTypeHttpResult.ContentType => Problems.ContentType;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var original = extra switch
            {
                null => null,
                JsonObject o => o,
                _ => JsonSerializer.SerializeToNode(extra, extra.GetType(), JsonOptionsOf(httpContext)) as JsonObject,
            };

            return WriteAsync(httpContext, status, sentence, code, original);
        }
    }
}
