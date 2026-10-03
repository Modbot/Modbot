using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Modbot.Core.Bluesky;

/// <summary>What went wrong with a call to Bluesky, in the few kinds Modbot acts on differently.</summary>
public enum BlueskyProblem
{
    /// <summary>The sign-in was refused for the handle or app password (<c>AuthenticationRequired</c> at sign-in).</summary>
    BadSignIn,

    /// <summary>The account is taken down, suspended or deactivated (<c>AccountTakedown</c>, <c>AccountDeactivated</c>).</summary>
    AccountGone,

    /// <summary>The access or refresh token has run out (<c>ExpiredToken</c>). A refresh or a new sign-in follows.</summary>
    TokenExpired,

    /// <summary>The token was not taken (<c>InvalidToken</c>, or <c>AuthenticationRequired</c> on a call that is not the sign-in).</summary>
    TokenRefused,

    /// <summary>
    /// A rate limit (429, <c>RateLimitExceeded</c>). Never retried: the Bluesky lane stops until the
    /// time Bluesky says the limit resets (CLAUDE.md).
    /// </summary>
    RateLimited,

    /// <summary>
    /// Something is already at the record key a put said must be empty (<c>InvalidSwap</c>). For a
    /// post, that is Modbot's own earlier try: the key is read back and the post taken.
    /// </summary>
    InvalidSwap,

    /// <summary>Nothing is at that record key (<c>RecordNotFound</c>).</summary>
    RecordNotFound,

    /// <summary>The handle or DID could not be found.</summary>
    NotFound,

    /// <summary>No answer, a timeout, or a 5xx: whether the call did anything is not known.</summary>
    Unavailable,

    /// <summary>Anything else Bluesky refused, held with its own words.</summary>
    Other,
}

/// <summary>One refusal from Bluesky.</summary>
/// <param name="Status">The HTTP status, or 0 when no answer arrived.</param>
/// <param name="Error">Bluesky's error word (the XRPC body's <c>error</c>).</param>
/// <param name="Message">Bluesky's own sentence, when it gave one.</param>
/// <param name="ResetAt">For a rate limit, when Bluesky says it resets (<c>ratelimit-reset</c>), when it said.</param>
public sealed record BlueskyFailure(
    BlueskyProblem Problem,
    int Status,
    string? Error = null,
    string? Message = null,
    DateTimeOffset? ResetAt = null)
{
    /// <summary>Whether this failure stops every call to Bluesky for a while.</summary>
    public bool StopsTheLane => Problem == BlueskyProblem.RateLimited;

    /// <summary>A failure that says nothing was done and nothing is known: no answer, or Bluesky's own trouble.</summary>
    public bool Unclear => Problem == BlueskyProblem.Unavailable;
}

/// <summary>A value from Bluesky, or why there is none.</summary>
public readonly record struct BlueskyResult<T>(T? Value, BlueskyFailure? Failure)
{
    public static BlueskyResult<T> Ok(T value) => new(value, null);

    public static BlueskyResult<T> Failed(BlueskyFailure failure) => new(default, failure);
}

/// <summary>
/// Sorts Bluesky's answers into <see cref="BlueskyProblem"/>s, and says each in a plain sentence
/// (Bluesky design §3.1).
/// </summary>
/// <remarks>
/// XRPC answers a refusal with <c>{"error":"InvalidSwap","message":"…"}</c>. That body is read here
/// and nowhere else in Modbot. The words the operator reads say "the account's server", never PDS.
/// </remarks>
public static class BlueskyErrors
{
    /// <summary>How long Modbot stops after a rate limit when Bluesky does not say when it resets.</summary>
    public static readonly TimeSpan LimitStop = TimeSpan.FromMinutes(15);

    /// <summary>The shortest stop after a rate limit, whatever Bluesky says.</summary>
    public static readonly TimeSpan ShortestStop = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest stop after a rate limit, whatever Bluesky says: a reset time far off (a broken
    /// header, or a server set to misbehave) must not silence the lane for good. Bluesky's own daily
    /// limits reset within a day.
    /// </summary>
    public static readonly TimeSpan LongestStop = TimeSpan.FromHours(24);

    /// <summary>The words for a handle or app password Bluesky refused.</summary>
    public const string NotAccepted = "Bluesky did not accept the handle or app password.";

    /// <summary>The words for a handle that could not be found, or that does not name its account back.</summary>
    public const string HandleNotFound = "Could not find this handle.";

    /// <summary>The words for an account server that did not answer.</summary>
    public const string Unreachable = "Could not reach the account's server.";

    /// <summary>The words while Bluesky limits Modbot.</summary>
    public const string Limited = "Bluesky is limiting Modbot.";

    /// <summary>The words while Modbot's own sign-in guard holds a sign-in back.</summary>
    public const string TooManySignIns = "Too many sign-ins.";

    /// <summary>The words when Bluesky no longer takes a sign-in made with Bluesky (OAuth).</summary>
    public const string SignInEnded = "The Bluesky sign-in has ended.";

    /// <summary>The words for a value that is not shaped like an app password.</summary>
    public const string UseAnAppPassword = "Use an app password.";

    /// <summary>Classifies an XRPC answer that was not a success.</summary>
    /// <param name="signingIn">
    /// The call was the sign-in itself: an <c>AuthenticationRequired</c> there is the password, not a token.
    /// </param>
    public static BlueskyFailure FromXrpc(HttpStatusCode status, string? body, HttpResponseHeaders? headers, bool signingIn = false)
    {
        var (error, message) = ReadError(body);
        var code = (int)status;

        var problem = error switch
        {
            "RateLimitExceeded" => BlueskyProblem.RateLimited,
            "ExpiredToken" => BlueskyProblem.TokenExpired,
            "InvalidToken" => BlueskyProblem.TokenRefused,
            "AuthenticationRequired" or "AuthFactorTokenRequired" => signingIn ? BlueskyProblem.BadSignIn : BlueskyProblem.TokenRefused,
            "AccountTakedown" or "AccountDeactivated" or "AccountSuspended" => BlueskyProblem.AccountGone,
            "InvalidSwap" => BlueskyProblem.InvalidSwap,
            "RecordNotFound" => BlueskyProblem.RecordNotFound,
            _ => code switch
            {
                429 => BlueskyProblem.RateLimited,
                401 => signingIn ? BlueskyProblem.BadSignIn : BlueskyProblem.TokenRefused,
                >= 500 => BlueskyProblem.Unavailable,
                _ => BlueskyProblem.Other,
            },
        };

        return new BlueskyFailure(problem, code, error, message, problem == BlueskyProblem.RateLimited ? ResetOf(headers) : null);
    }

    /// <summary>A call that got no answer at all: a timeout or a dropped connection.</summary>
    public static BlueskyFailure NoAnswer() => new(BlueskyProblem.Unavailable, 0);

    /// <summary>
    /// When the Bluesky lane may be used again after <paramref name="failure"/>: the time Bluesky said
    /// the limit resets, or <see cref="LimitStop"/> from now when it did not say, never sooner than
    /// <see cref="ShortestStop"/> and never later than <see cref="LongestStop"/>.
    /// </summary>
    public static DateTimeOffset StopUntil(BlueskyFailure failure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var until = failure.ResetAt ?? now + LimitStop;

        if (until < now + ShortestStop)
            return now + ShortestStop;

        return until > now + LongestStop ? now + LongestStop : until;
    }

    /// <summary>The sentence an operator reads for a failure.</summary>
    public static string Sentence(BlueskyFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Problem switch
        {
            BlueskyProblem.BadSignIn => NotAccepted,
            BlueskyProblem.NotFound => HandleNotFound,
            BlueskyProblem.RateLimited => Limited,
            BlueskyProblem.Unavailable => Unreachable,
            _ => OwnWords(failure),
        };
    }

    /// <summary>Bluesky's own sentence, or its error word, or the status it answered with.</summary>
    public static string OwnWords(BlueskyFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (!string.IsNullOrWhiteSpace(failure.Message))
            return Cut(failure.Message.Trim());

        if (!string.IsNullOrWhiteSpace(failure.Error))
            return Cut(failure.Error.Trim());

        return $"Bluesky answered {failure.Status}.";
    }

    private static string Cut(string text) => text.Length <= 300 ? text : text[..300];

    /// <summary>
    /// The reset time from <c>ratelimit-reset</c> (seconds since 1970), or from <c>Retry-After</c>
    /// as a date. A <c>Retry-After</c> in seconds is not read here, where there is no clock: the
    /// fifteen-minute stop stands in for it, as for a limit that names no time at all.
    /// </summary>
    private static DateTimeOffset? ResetOf(HttpResponseHeaders? headers)
    {
        if (headers is null)
            return null;

        if (headers.TryGetValues("ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return headers.RetryAfter?.Date;
    }

    private static (string? Error, string? Message) ReadError(string? body)
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
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

            return (error, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
