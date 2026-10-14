using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modbot.Core.Supporters;

/// <summary>What went wrong with a call to Patreon, in the few kinds Modbot acts on differently.</summary>
public enum PatreonProblem
{
    /// <summary>The token request was refused: the client id, the client secret or the refresh token is not Patreon's, or was reset.</summary>
    CredentialsRefused,

    /// <summary>The access token was not accepted (401). A new one is asked for once.</summary>
    Unauthorized,

    /// <summary>The access token is not allowed to read this (403): it lacks a scope, or is not the campaign owner's.</summary>
    Forbidden,

    /// <summary>A rate limit (429). Never retried; every call stops for a while (CLAUDE.md: never retry a 429).</summary>
    Limited,

    /// <summary>Nothing there under that id (404).</summary>
    NotFound,

    /// <summary>No answer, a 5xx, or an answer Modbot cannot read: the outcome of the call is not known.</summary>
    Unavailable,

    /// <summary>Anything else Patreon refused.</summary>
    Other,
}

/// <summary>One refusal from Patreon.</summary>
/// <param name="Problem">The kind of refusal.</param>
/// <param name="Status">The HTTP status, or 0 when no answer arrived.</param>
/// <param name="RetryAfter">How long Patreon says to wait (<c>retry_after_seconds</c> in its answer to a 429), when it said.</param>
public sealed record PatreonFailure(PatreonProblem Problem, int Status, TimeSpan? RetryAfter = null)
{
    /// <summary>Whether this failure stops every call to Patreon for a while.</summary>
    public bool IsALimit => Problem == PatreonProblem.Limited;
}

/// <summary>A value from Patreon, or why there is none.</summary>
public readonly record struct PatreonResult<T>(T? Value, PatreonFailure? Failure)
{
    public static PatreonResult<T> Ok(T value) => new(value, null);

    public static PatreonResult<T> Failed(PatreonFailure failure) => new(default, failure);
}

/// <summary>A new pair of tokens, and how long Patreon says the access token lasts.</summary>
public sealed record PatreonTokens(string AccessToken, string? RefreshToken, TimeSpan LastsFor)
{
    // Both are secrets; keep them out of any ToString.
    public override string ToString() => $"PatreonTokens({LastsFor.TotalSeconds:0}s)";
}

/// <summary>What Modbot asks of Patreon and how fast.</summary>
public static class PatreonLimits
{
    /// <summary>
    /// The shortest wait between two requests to Patreon: 1 a second. Patreon's rate limit page
    /// (docs.patreon.com, #rate-limiting) gives the client "up to 100 requests every 2 seconds" and each access
    /// token "up to 100 requests per minute"; one a second is well inside both.
    /// </summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);

    /// <summary>The shortest stop after a 429, when Patreon says nothing longer.</summary>
    public static readonly TimeSpan ColdStop = TimeSpan.FromMinutes(15);

    /// <summary>The longest stop after a 429, however long Patreon says.</summary>
    public static readonly TimeSpan LongestColdStop = TimeSpan.FromHours(6);

    /// <summary>How many members a page asks for.</summary>
    public const int PageSize = 500;

    /// <summary>The most pages a catch-up reads, so a cursor that never ends cannot hold it forever.</summary>
    public const int MostPages = 200;

    /// <summary>The most patrons looked up in one pass.</summary>
    public const int MostLookups = 20;

    /// <summary>How long a stop lasts after a 429: Patreon's own wait, but at least <see cref="ColdStop"/> and at most <see cref="LongestColdStop"/>.</summary>
    public static TimeSpan StopFor(PatreonFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var asked = failure.RetryAfter ?? TimeSpan.Zero;
        return asked < ColdStop ? ColdStop : asked > LongestColdStop ? LongestColdStop : asked;
    }
}

/// <summary>
/// Patreon's API, called with plain <see cref="HttpClient"/> (Patreon design, step 2). One per process: a singleton.
/// </summary>
/// <remarks>
/// <para>
/// Four calls: the token refresh, the creator's first campaign, one page of that campaign's members and one
/// member. Every call goes to Patreon's one host on the named client <see cref="HttpClientName"/>, which follows
/// no redirects, uses no proxy and only ever speaks https: no address in Modbot's settings is fetched. The only
/// thing sent to Patreon is the creator's own token or the client's keys, and a member's or a campaign's id.
/// </para>
/// <para>
/// Nothing here retries or paces. A rate limit comes back as a failure that <see cref="PatreonFailure.IsALimit"/>
/// and the caller stops (CLAUDE.md: never retry a 429); the caller waits <see cref="PatreonLimits.MinGap"/> between
/// calls. Nothing here logs a token.
/// </para>
/// </remarks>
public sealed class PatreonClient(IHttpClientFactory http)
{
    /// <summary>The name of the <see cref="HttpClient"/> every Patreon call is made on.</summary>
    public const string HttpClientName = "patreon";

    /// <summary>The API's base address.</summary>
    public const string ApiAddress = "https://www.patreon.com/api/oauth2/v2/";

    /// <summary>Where every token request goes.</summary>
    public const string TokenAddress = "https://www.patreon.com/api/oauth2/token";

    // The fields the intake reads, and nothing else: the member's status and last charge, the user's connected apps, the tier's name and price.
    private const string MemberQuery =
        "include=user,currently_entitled_tiers"
        + "&fields[member]=patron_status,last_charge_date,last_charge_status,full_name,currently_entitled_amount_cents"
        + "&fields[user]=social_connections"
        + "&fields[tier]=title,amount_cents";

    /// <summary>A new access token for a refresh token (the refresh_token grant).</summary>
    public async Task<PatreonResult<PatreonTokens>> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var content = new FormUrlEncodedContent(
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId),
            new("client_secret", clientSecret),
        ]);

        try
        {
            using var response = await http.CreateClient(HttpClientName).PostAsync(new Uri(TokenAddress), content, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var failure = Failure(response.StatusCode, text, response.Headers);

                // At the token address a refusal of the keys is a 400 or 401, and says nothing about an access token.
                return PatreonResult<PatreonTokens>.Failed(failure.Problem is PatreonProblem.Unauthorized or PatreonProblem.Other
                    && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                        ? failure with { Problem = PatreonProblem.CredentialsRefused }
                        : failure);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var access = Text(root, "access_token");
            var refresh = Text(root, "refresh_token");
            var seconds = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var value) ? value : 0;

            return string.IsNullOrEmpty(access) || seconds <= 0
                ? PatreonResult<PatreonTokens>.Failed(new PatreonFailure(PatreonProblem.Unavailable, (int)response.StatusCode))
                : PatreonResult<PatreonTokens>.Ok(new PatreonTokens(access, string.IsNullOrEmpty(refresh) ? null : refresh, TimeSpan.FromSeconds(seconds)));
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return PatreonResult<PatreonTokens>.Failed(new PatreonFailure(PatreonProblem.Unavailable, 0));
        }
    }

    /// <summary>The id of the creator's first campaign, or a successful null when the creator has none.</summary>
    public async Task<PatreonResult<string?>> FirstCampaignIdAsync(string accessToken, CancellationToken ct)
    {
        var answer = await GetAsync(accessToken, "campaigns", ct).ConfigureAwait(false);

        if (answer.Failure is { } failure)
            return PatreonResult<string?>.Failed(failure);

        var page = PatreonMembersPage.Parse(answer.Value!);

        return page is null
            ? PatreonResult<string?>.Failed(new PatreonFailure(PatreonProblem.Unavailable, 200))
            : PatreonResult<string?>.Ok(page.Data?.Select(c => c.Id?.Trim()).FirstOrDefault(id => !string.IsNullOrEmpty(id)));
    }

    /// <summary>One page of the campaign's members, from <paramref name="cursor"/> (null for the first page).</summary>
    public async Task<PatreonResult<PatreonMembersPage>> MembersAsync(string accessToken, string campaignId, string? cursor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);

        var path = "campaigns/" + Uri.EscapeDataString(campaignId) + "/members?" + MemberQuery
            + "&page[count]=" + PatreonLimits.PageSize.ToString(CultureInfo.InvariantCulture)
            + (string.IsNullOrEmpty(cursor) ? string.Empty : "&page[cursor]=" + Uri.EscapeDataString(cursor));

        var answer = await GetAsync(accessToken, path, ct).ConfigureAwait(false);

        if (answer.Failure is { } failure)
            return PatreonResult<PatreonMembersPage>.Failed(failure);

        return PatreonMembersPage.Parse(answer.Value!) is { } page
            ? PatreonResult<PatreonMembersPage>.Ok(page)
            : PatreonResult<PatreonMembersPage>.Failed(new PatreonFailure(PatreonProblem.Unavailable, 200));
    }

    /// <summary>One member with their user and tiers, as a message the intake takes.</summary>
    public async Task<PatreonResult<PatreonMessage>> MemberAsync(string accessToken, string memberId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);

        var answer = await GetAsync(accessToken, "members/" + Uri.EscapeDataString(memberId) + "?" + MemberQuery, ct).ConfigureAwait(false);

        if (answer.Failure is { } failure)
            return PatreonResult<PatreonMessage>.Failed(failure);

        return PatreonMessage.Parse(System.Text.Encoding.UTF8.GetBytes(answer.Value!)) is { } message
            ? PatreonResult<PatreonMessage>.Ok(message)
            : PatreonResult<PatreonMessage>.Failed(new PatreonFailure(PatreonProblem.Unavailable, 200));
    }

    private async Task<PatreonResult<string>> GetAsync(string accessToken, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ApiAddress), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? PatreonResult<string>.Ok(text)
                : PatreonResult<string>.Failed(Failure(response.StatusCode, text, response.Headers));
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return PatreonResult<string>.Failed(new PatreonFailure(PatreonProblem.Unavailable, 0));
        }
    }

    private static PatreonFailure Failure(HttpStatusCode status, string body, HttpResponseHeaders headers)
    {
        var code = (int)status;

        if (status == HttpStatusCode.TooManyRequests)
            return new PatreonFailure(PatreonProblem.Limited, code, RetryAfter(body, headers));

        return status switch
        {
            HttpStatusCode.Unauthorized => new PatreonFailure(PatreonProblem.Unauthorized, code),
            HttpStatusCode.Forbidden => new PatreonFailure(PatreonProblem.Forbidden, code),
            HttpStatusCode.NotFound => new PatreonFailure(PatreonProblem.NotFound, code),
            _ when code >= 500 => new PatreonFailure(PatreonProblem.Unavailable, code),
            _ => new PatreonFailure(PatreonProblem.Other, code),
        };
    }

    /// <summary>Patreon's wait: <c>retry_after_seconds</c> in the answer's body, or else a <c>Retry-After</c> header in seconds.</summary>
    private static TimeSpan? RetryAfter(string body, HttpResponseHeaders headers)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("retry_after_seconds", out var seconds) && seconds.TryGetDouble(out var value) && value > 0)
                return TimeSpan.FromSeconds(Math.Min(value, TimeSpan.FromDays(1).TotalSeconds));
        }
        catch (JsonException)
        {
            // Not JSON: the header, if any, is all there is.
        }

        return headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : null;
    }

    private static bool IsNoAnswer(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Registers the Patreon client.</summary>
public static class PatreonServices
{
    /// <summary>
    /// The named client follows no redirects and uses no proxy: Patreon's host is fixed, and the VRChat egress
    /// proxy is for VRChat only. Answers are capped at 16 MB, which holds a page of 500 members with their tiers.
    /// </summary>
    public static IServiceCollection AddPatreon(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(PatreonClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
            });

        services.TryAddSingleton<PatreonClient>();

        return services;
    }
}
