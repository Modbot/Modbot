using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modbot.Core.Twitch;

/// <summary>A Twitch channel, as Get Users gives it.</summary>
/// <param name="Id">Twitch's user id. Opaque text, never parsed.</param>
/// <param name="Login">The channel's login name, lower case.</param>
/// <param name="DisplayName">The name Twitch shows.</param>
public sealed record TwitchChannel(string Id, string Login, string DisplayName);

/// <summary>A stream that is live, as Get Streams gives it.</summary>
/// <param name="Id">Twitch's stream id. Opaque text, never parsed.</param>
/// <param name="Type"><c>live</c>, or whatever else Twitch says it is (a rerun, say).</param>
/// <param name="Title">The stream's title.</param>
/// <param name="Category">The category (game) name.</param>
/// <param name="Viewers">The viewer count.</param>
/// <param name="StartedAt">When Twitch says it started.</param>
public sealed record TwitchLiveStream(
    string Id,
    string Type,
    string? Title,
    string? Category,
    int Viewers,
    DateTimeOffset StartedAt);

/// <summary>An app access token, and how long Twitch says it lasts.</summary>
public sealed record TwitchAppToken(string Value, TimeSpan LastsFor)
{
    // The token is a secret; keep it out of any ToString.
    public override string ToString() => $"TwitchAppToken({LastsFor.TotalSeconds:0}s)";
}

/// <summary>
/// Twitch's API, called with plain <see cref="HttpClient"/> and <see cref="JsonDocument"/> (Twitch
/// design). One per process: a singleton.
/// </summary>
/// <remarks>
/// <para>
/// Three calls, all with an app access token and the client id: the token request (client
/// credentials), Get Users for the channel's id (Check) and Get Streams for whether it is live (the
/// poll). Every call goes to Twitch's two fixed hosts on the named client <see cref="HttpClientName"/>,
/// which follows no redirects, uses no proxy and only ever speaks https: no address in Modbot's
/// settings is fetched, so the private-address guard the pictures and Bluesky need is not needed.
/// </para>
/// <para>
/// Nothing here retries. A rate limit comes back as a <see cref="TwitchFailure"/> that
/// <see cref="TwitchFailure.IsALimit"/>, and the caller stops until Twitch's
/// <c>Ratelimit-Reset</c> (CLAUDE.md: never retry a 429).
/// </para>
/// </remarks>
public sealed class TwitchClient(IHttpClientFactory http)
{
    /// <summary>The name of the <see cref="HttpClient"/> every Twitch call is made on.</summary>
    public const string HttpClientName = "twitch";

    /// <summary>The API's base address.</summary>
    public const string ApiAddress = "https://api.twitch.tv/helix/";

    /// <summary>Where every token request goes.</summary>
    public const string TokenAddress = "https://id.twitch.tv/oauth2/token";

    /// <summary>The largest login Twitch has.</summary>
    public const int MostLoginLength = 25;

    /// <summary>An app access token for the client id and secret (the client credentials grant).</summary>
    public async Task<TwitchResult<TwitchAppToken>> TokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        using var content = new FormUrlEncodedContent(
        [
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("grant_type", "client_credentials"),
        ]);

        try
        {
            using var response = await http.CreateClient(HttpClientName).PostAsync(new Uri(TokenAddress), content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return TwitchResult<TwitchAppToken>.Failed(TwitchErrors.FromToken(response.StatusCode, text, response.Headers));

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var token = Text(root, "access_token");
            var seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var value) ? value : 0;

            return string.IsNullOrEmpty(token) || seconds <= 0
                ? TwitchResult<TwitchAppToken>.Failed(new TwitchFailure(TwitchProblem.Other, (int)response.StatusCode))
                : TwitchResult<TwitchAppToken>.Ok(new TwitchAppToken(token, TimeSpan.FromSeconds(seconds)));
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return TwitchResult<TwitchAppToken>.Failed(TwitchErrors.NoAnswer());
        }
    }

    /// <summary>
    /// Get Users for a login: the channel, or a successful null when Twitch has none by that name.
    /// </summary>
    public async Task<TwitchResult<TwitchChannel?>> ChannelAsync(string accessToken, string clientId, string login, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        var answer = await GetAsync(accessToken, clientId, "users?login=" + Uri.EscapeDataString(login), ct);

        if (answer.Failure is { } failure)
            return TwitchResult<TwitchChannel?>.Failed(failure);

        var root = answer.Value;

        foreach (var row in Rows(root))
        {
            var id = Text(row, "id");
            if (string.IsNullOrEmpty(id))
                continue;

            return TwitchResult<TwitchChannel?>.Ok(new TwitchChannel(
                id, Text(row, "login") ?? login, Text(row, "display_name") ?? Text(row, "login") ?? login));
        }

        return TwitchResult<TwitchChannel?>.Ok(null);
    }

    /// <summary>
    /// Get Streams for one channel: its live stream, or a successful null when it is not live.
    /// </summary>
    public async Task<TwitchResult<TwitchLiveStream?>> StreamAsync(string accessToken, string clientId, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var answer = await GetAsync(accessToken, clientId, "streams?first=1&user_id=" + Uri.EscapeDataString(userId), ct);

        if (answer.Failure is { } failure)
            return TwitchResult<TwitchLiveStream?>.Failed(failure);

        var root = answer.Value;

        foreach (var row in Rows(root))
        {
            var id = Text(row, "id");
            var startedAt = Text(row, "started_at");

            if (string.IsNullOrEmpty(id)
                || !DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started))
            {
                continue;
            }

            var viewers = row.TryGetProperty("viewer_count", out var count) && count.TryGetInt32(out var number) ? number : 0;

            return TwitchResult<TwitchLiveStream?>.Ok(new TwitchLiveStream(
                id,
                Text(row, "type") ?? string.Empty,
                Text(row, "title"),
                Text(row, "game_name"),
                Math.Max(0, viewers),
                started));
        }

        return TwitchResult<TwitchLiveStream?>.Ok(null);
    }

    private async Task<TwitchResult<JsonElement>> GetAsync(string accessToken, string clientId, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ApiAddress), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", clientId);

        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return TwitchResult<JsonElement>.Failed(TwitchErrors.FromApi(response.StatusCode, text, response.Headers));

            using var document = JsonDocument.Parse(text);
            return TwitchResult<JsonElement>.Ok(document.RootElement.Clone());
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return TwitchResult<JsonElement>.Failed(TwitchErrors.NoAnswer());
        }
    }

    private static bool IsNoAnswer(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object)
                    yield return row;
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Registers the Twitch client and the app token keeper.</summary>
public static class TwitchServices
{
    /// <summary>
    /// The named client follows no redirects and uses no proxy: Twitch's hosts are fixed, and the
    /// VRChat egress proxy is for VRChat only. Answers are capped at 1 MB.
    /// </summary>
    public static IServiceCollection AddTwitch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(TwitchClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
            });

        services.TryAddSingleton<TwitchClient>();
        services.TryAddSingleton<TwitchSignIn>();

        return services;
    }
}
