using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Modbot.Core.Google;

/// <summary>What went wrong with a call to Google, in the few kinds Modbot acts on differently.</summary>
public enum GoogleProblem
{
    /// <summary>The token request was refused for the key: deleted, disabled, or not Google's.</summary>
    KeyRefused,

    /// <summary>The token request was refused because its times were off: this server's clock is wrong.</summary>
    ClockOff,

    /// <summary>The access token was not accepted (401). A new one is asked for once.</summary>
    Unauthorized,

    /// <summary>The calendar or event does not exist, or Modbot may not see it (404).</summary>
    NotFound,

    /// <summary>The event was deleted on Google (410).</summary>
    Gone,

    /// <summary>A 403 that is not a limit: Modbot may not do this on this calendar.</summary>
    Forbidden,

    /// <summary>The id is already taken on this calendar (409 <c>duplicate</c>).</summary>
    Duplicate,

    /// <summary>The event changed on Google since it was read (412).</summary>
    ConditionNotMet,

    /// <summary>
    /// A rate limit: 429, or 403 with <c>rateLimitExceeded</c>, <c>userRateLimitExceeded</c> or
    /// <c>usageLimits</c>. Never retried; the lane stops (Google Calendar design §3.7).
    /// </summary>
    Limited,

    /// <summary>403 <c>quotaExceeded</c>: Google's calendar use limits. Stopped for hours, not minutes.</summary>
    QuotaExceeded,

    /// <summary>No answer, or a 5xx: the outcome of the call is not known.</summary>
    Unavailable,

    /// <summary>Anything else Google refused, held with Google's own words.</summary>
    Other,
}

/// <summary>One refusal from Google.</summary>
/// <param name="Status">The HTTP status, or 0 when no answer arrived.</param>
/// <param name="Reason">Google's reason word (<c>error.errors[0].reason</c>, or the token endpoint's <c>error</c>).</param>
/// <param name="Message">Google's own sentence, when it gave one.</param>
/// <param name="RetryAfter">The wait Google asked for in <c>Retry-After</c>, when it sent one.</param>
public sealed record GoogleFailure(
    GoogleProblem Problem,
    int Status,
    string? Reason = null,
    string? Message = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>Whether this failure stops every call to Google for a while (§3.7).</summary>
    public bool StopsTheLane => Problem is GoogleProblem.Limited or GoogleProblem.QuotaExceeded;
}

/// <summary>A value from Google, or why there is none.</summary>
public readonly record struct GoogleResult<T>(T? Value, GoogleFailure? Failure)
{
    public static GoogleResult<T> Ok(T value) => new(value, null);

    public static GoogleResult<T> Failed(GoogleFailure failure) => new(default, failure);
}

/// <summary>
/// Sorts Google's answers into <see cref="GoogleProblem"/>s, and says each in a plain sentence
/// (Google Calendar design §3.1, §3.7).
/// </summary>
/// <remarks>
/// The Calendar API answers <c>{"error":{"code":403,"message":"…","errors":[{"reason":"…"}]}}</c>;
/// the token endpoint answers <c>{"error":"invalid_grant","error_description":"…"}</c>. Both are
/// read here, and nothing else in Modbot looks at Google's error bodies.
/// </remarks>
public static class GoogleErrors
{
    /// <summary>How long Modbot stops after a rate limit, unless Google asks for longer.</summary>
    public static readonly TimeSpan LimitStop = TimeSpan.FromMinutes(15);

    /// <summary>How long Modbot stops after Google's calendar use limits (<c>quotaExceeded</c>).</summary>
    public static readonly TimeSpan QuotaStop = TimeSpan.FromHours(6);

    /// <summary>Classifies a Calendar API answer that was not a success.</summary>
    public static GoogleFailure FromCalendar(HttpStatusCode status, string? body, RetryConditionHeaderValue? retryAfter = null)
    {
        var (reason, message) = ReadCalendarError(body);
        var code = (int)status;
        var wait = RetryAfterOf(retryAfter);

        var problem = code switch
        {
            401 => GoogleProblem.Unauthorized,
            404 => GoogleProblem.NotFound,
            409 => GoogleProblem.Duplicate,
            410 => GoogleProblem.Gone,
            412 => GoogleProblem.ConditionNotMet,
            429 => GoogleProblem.Limited,
            403 => reason switch
            {
                "quotaExceeded" => GoogleProblem.QuotaExceeded,
                "rateLimitExceeded" or "userRateLimitExceeded" or "usageLimits" => GoogleProblem.Limited,
                _ => GoogleProblem.Forbidden,
            },
            >= 500 => GoogleProblem.Unavailable,
            _ => GoogleProblem.Other,
        };

        return new GoogleFailure(problem, code, reason, message, wait);
    }

    /// <summary>Classifies a token endpoint answer that was not a success.</summary>
    public static GoogleFailure FromToken(HttpStatusCode status, string? body, RetryConditionHeaderValue? retryAfter = null)
    {
        var (error, description) = ReadTokenError(body);
        var code = (int)status;
        var wait = RetryAfterOf(retryAfter);

        if (code == 429)
            return new GoogleFailure(GoogleProblem.Limited, code, error, description, wait);

        if (code >= 500)
            return new GoogleFailure(GoogleProblem.Unavailable, code, error, description, wait);

        // Google's words for a token whose iat or exp is out of range: "Token must be a short-lived
        // token (60 minutes) and in a reasonable timeframe". The key is fine; the clock is not.
        if (description is not null
            && (description.Contains("short-lived token", StringComparison.OrdinalIgnoreCase)
                || description.Contains("reasonable timeframe", StringComparison.OrdinalIgnoreCase)))
        {
            return new GoogleFailure(GoogleProblem.ClockOff, code, error, description, wait);
        }

        // invalid_grant: a deleted or disabled key, or a bad signature. invalid_client and
        // unauthorized_client: the service account itself is gone or not allowed.
        if (error is "invalid_grant" or "invalid_client" or "unauthorized_client" || code is 400 or 401 or 403)
            return new GoogleFailure(GoogleProblem.KeyRefused, code, error, description, wait);

        return new GoogleFailure(GoogleProblem.Other, code, error, description, wait);
    }

    /// <summary>A call that got no answer at all: a timeout or a dropped connection.</summary>
    public static GoogleFailure NoAnswer() => new(GoogleProblem.Unavailable, 0);

    /// <summary>
    /// How long to stop after a failure that stops the lane: 15 minutes for a rate limit and six
    /// hours for the use limits, or Google's <c>Retry-After</c> when that is later.
    /// </summary>
    public static TimeSpan StopFor(GoogleFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var floor = failure.Problem == GoogleProblem.QuotaExceeded ? QuotaStop : LimitStop;
        return failure.RetryAfter is { } asked && asked > floor ? asked : floor;
    }

    /// <summary>The sentence an operator reads for a failure (Google Calendar design §3.1).</summary>
    public static string Sentence(GoogleFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Problem switch
        {
            GoogleProblem.KeyRefused => "Google did not accept the key.",
            GoogleProblem.ClockOff => "The server's clock is off.",
            GoogleProblem.NotFound => CannotSee,
            GoogleProblem.Forbidden => "Modbot can't change events on this calendar.",
            GoogleProblem.Limited or GoogleProblem.QuotaExceeded => "Google is limiting Modbot.",
            GoogleProblem.Unavailable => "Google did not answer.",
            _ => string.IsNullOrWhiteSpace(failure.Message)
                ? $"Google answered {failure.Status}."
                : Cut(failure.Message.Trim()),
        };
    }

    /// <summary>Check's words when Modbot cannot see the calendar at all.</summary>
    public const string CannotSee = "Modbot can't see this calendar.";

    /// <summary>Check's words when Modbot may read the calendar but not change its events.</summary>
    public const string ReadOnly = "Modbot can only read this calendar.";

    private static string Cut(string text) => text.Length <= 300 ? text : text[..300];

    private static TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header) =>
        header?.Delta is { } delta && delta > TimeSpan.Zero ? delta : null;

    private static (string? Reason, string? Message) ReadCalendarError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return (null, null);

            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string? reason = null;

            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errors.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("reason", out var r)
                        && r.ValueKind == JsonValueKind.String)
                    {
                        reason = r.GetString();
                        break;
                    }
                }
            }

            return (reason, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static (string? Error, string? Description) ReadTokenError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

            return (error, description);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
