using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Modbot.Core.Twitch;

/// <summary>What went wrong with a call to Twitch, in the few kinds Modbot acts on differently.</summary>
public enum TwitchProblem
{
    /// <summary>The token request was refused: the client id or secret is not Twitch's, or was reset.</summary>
    CredentialsRefused,

    /// <summary>The access token was not accepted (401). A new one is asked for once.</summary>
    Unauthorized,

    /// <summary>
    /// A rate limit (429). Never retried; every call stops until Twitch's <c>Ratelimit-Reset</c>
    /// (CLAUDE.md: never retry a 429).
    /// </summary>
    Limited,

    /// <summary>No answer, or a 5xx: the outcome of the call is not known.</summary>
    Unavailable,

    /// <summary>Anything else Twitch refused (a login with characters Twitch will not take, say), held with Twitch's own words.</summary>
    Other,
}

/// <summary>One refusal from Twitch.</summary>
/// <param name="Status">The HTTP status, or 0 when no answer arrived.</param>
/// <param name="Message">Twitch's own sentence (<c>message</c>), when it gave one.</param>
/// <param name="ResetAt">
/// When Twitch says the bucket is full again: its <c>Ratelimit-Reset</c>, a Unix time in seconds.
/// Null when it sent none or one that does not read as a time.
/// </param>
public sealed record TwitchFailure(
    TwitchProblem Problem,
    int Status,
    string? Message = null,
    DateTimeOffset? ResetAt = null)
{
    /// <summary>Whether this failure stops every call to Twitch for a while.</summary>
    public bool IsALimit => Problem == TwitchProblem.Limited;
}

/// <summary>A value from Twitch, or why there is none.</summary>
public readonly record struct TwitchResult<T>(T? Value, TwitchFailure? Failure)
{
    public static TwitchResult<T> Ok(T value) => new(value, null);

    public static TwitchResult<T> Failed(TwitchFailure failure) => new(default, failure);
}

/// <summary>
/// Sorts Twitch's answers into <see cref="TwitchProblem"/>s, works out how long a rate limit stops
/// Modbot, and says each in a plain sentence (Twitch design).
/// </summary>
/// <remarks>
/// Twitch answers <c>{"error":"Unauthorized","status":401,"message":"Invalid OAuth token"}</c>, and the
/// token endpoint <c>{"status":400,"message":"invalid client"}</c>. Both are read here, and nothing
/// else in Modbot looks at Twitch's error bodies.
/// </remarks>
public static class TwitchErrors
{
    /// <summary>How long Modbot stops after a rate limit when Twitch names no time.</summary>
    public static readonly TimeSpan LimitStop = TimeSpan.FromMinutes(15);

    /// <summary>The shortest stop after a rate limit, whatever Twitch's reset says.</summary>
    public static readonly TimeSpan LeastStop = TimeSpan.FromMinutes(1);

    /// <summary>The longest stop after a rate limit: a reset further off than this is not believed.</summary>
    public static readonly TimeSpan MostStop = TimeSpan.FromHours(24);

    public const string CredentialsNotAccepted = "Twitch did not accept the client id and secret.";
    public const string TokenNotAccepted = "Twitch did not accept the sign-in.";
    public const string Limiting = "Twitch is limiting Modbot.";
    public const string DidNotAnswer = "Twitch did not answer.";
    public const string ChannelNotFound = "Twitch has no channel with that name.";

    /// <summary>Classifies a Helix API answer that was not a success.</summary>
    public static TwitchFailure FromApi(HttpStatusCode status, string? body, HttpResponseHeaders? headers = null)
    {
        var code = (int)status;
        var message = ReadMessage(body);
        var reset = ResetOf(headers);

        var problem = code switch
        {
            401 => TwitchProblem.Unauthorized,
            429 => TwitchProblem.Limited,
            >= 500 => TwitchProblem.Unavailable,
            _ => TwitchProblem.Other,
        };

        return new TwitchFailure(problem, code, message, reset);
    }

    /// <summary>Classifies a token endpoint answer that was not a success.</summary>
    public static TwitchFailure FromToken(HttpStatusCode status, string? body, HttpResponseHeaders? headers = null)
    {
        var code = (int)status;
        var message = ReadMessage(body);
        var reset = ResetOf(headers);

        if (code == 429)
            return new TwitchFailure(TwitchProblem.Limited, code, message, reset);

        if (code >= 500)
            return new TwitchFailure(TwitchProblem.Unavailable, code, message, reset);

        // 400 "invalid client", 403 "invalid client secret": the id or the secret is wrong.
        if (code is 400 or 401 or 403)
            return new TwitchFailure(TwitchProblem.CredentialsRefused, code, message, reset);

        return new TwitchFailure(TwitchProblem.Other, code, message, reset);
    }

    /// <summary>A call that got no answer at all: a timeout or a dropped connection.</summary>
    public static TwitchFailure NoAnswer() => new(TwitchProblem.Unavailable, 0);

    /// <summary>
    /// When a rate limit stops lifting: Twitch's <c>Ratelimit-Reset</c> when it names a time, held to
    /// between a minute and a day from <paramref name="now"/>; otherwise 15 minutes from it.
    /// </summary>
    public static DateTimeOffset StopUntil(TwitchFailure failure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.ResetAt is not { } reset)
            return now + LimitStop;

        var wait = reset - now;

        if (wait < LeastStop)
            wait = LeastStop;
        else if (wait > MostStop)
            wait = MostStop;

        return now + wait;
    }

    /// <summary>The sentence an operator reads for a failure.</summary>
    public static string Sentence(TwitchFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Problem switch
        {
            TwitchProblem.CredentialsRefused => CredentialsNotAccepted,
            TwitchProblem.Unauthorized => TokenNotAccepted,
            TwitchProblem.Limited => Limiting,
            TwitchProblem.Unavailable => DidNotAnswer,
            _ => OwnWords(failure),
        };
    }

    /// <summary>Twitch's own sentence, or the status it answered with when it gave none.</summary>
    public static string OwnWords(TwitchFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (string.IsNullOrWhiteSpace(failure.Message))
            return $"Twitch answered {failure.Status}.";

        var text = failure.Message.Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static DateTimeOffset? ResetOf(HttpResponseHeaders? headers)
    {
        if (headers is null || !headers.TryGetValues("Ratelimit-Reset", out var values))
            return null;

        foreach (var value in values)
        {
            if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                && seconds is > 0 and < 253_402_300_799)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    private static string? ReadMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
