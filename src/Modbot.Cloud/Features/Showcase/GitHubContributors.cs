using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Cloud.Features.Showcase;

/// <param name="Login">Their GitHub username.</param>
/// <param name="Url">Their GitHub profile.</param>
/// <param name="AvatarUrl">Their picture.</param>
/// <param name="Contributions">Commits GitHub counted.</param>
public sealed record ContributorView(string Login, string Url, string AvatarUrl, int Contributions);

/// <summary>
/// The repository's contributors, as GitHub lists them.
/// </summary>
/// <remarks>
/// <para>
/// Read from GitHub rather than kept in a table, because GitHub already knows and a second list
/// typed by hand only goes out of date. Cached for <see cref="CacheFor"/> so that however many
/// Modbots ask, GitHub is asked a few times a day.
/// </para>
/// <para>
/// <strong>An empty list is a normal answer.</strong> The repository is private today, so an
/// unauthenticated read gets a 404; with no <c>GITHUB_TOKEN</c> set, this returns nothing and the
/// Credits page simply has no contributors on it. When the repository is public, a token is not
/// needed and the same code works.
/// </para>
/// <para>
/// <strong>Rate limit.</strong> GitHub allows 60 unauthenticated requests an hour per address and
/// 5,000 with a token. One request every six hours is nowhere near either, and a refusal is cached
/// as an empty answer for <see cref="CacheFailureFor"/> rather than retried on every page load —
/// which is the shape that turns a rate limit into a ban.
/// </para>
/// <para>
/// Bots are left out. "Who worked on this" does not mean dependabot.
/// </para>
/// </remarks>
public sealed class GitHubContributors(HttpClient client, string? token, string repository, TimeProvider time)
{
    public const string HttpClientName = "github";

    /// <summary>The repository read when <c>GITHUB_REPOSITORY</c> is not set.</summary>
    public const string DefaultRepository = "Modbot/Modbot";

    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>How long a refusal is remembered. Short enough to recover, long enough not to hammer.</summary>
    public static readonly TimeSpan CacheFailureFor = TimeSpan.FromMinutes(30);

    /// <summary>GitHub refuses a request with no user agent.</summary>
    private const string UserAgent = "Modbot-Cloud";

    private readonly SemaphoreSlim _one = new(1, 1);

    private IReadOnlyList<ContributorView> _cached = [];
    private DateTimeOffset _staleAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<ContributorView>> ReadAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();

        if (now < _staleAt)
            return _cached;

        await _one.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Somebody else may have refreshed it while this call waited.
            now = time.GetUtcNow();
            if (now < _staleAt)
                return _cached;

            var (people, ok) = await FetchAsync(ct).ConfigureAwait(false);

            _cached = ok ? people : [];
            _staleAt = now + (ok ? CacheFor : CacheFailureFor);

            return _cached;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<(IReadOnlyList<ContributorView> People, bool Ok)> FetchAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repository}/contributors?per_page=100&anon=0";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            // 404 on a private repository with no token, 403 when the rate limit is spent. Both are
            // "no contributors today" rather than an error anybody can act on.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                || !response.IsSuccessStatusCode)
            {
                return ([], false);
            }

            var people = await response.Content
                .ReadFromJsonAsync<List<GitHubContributor>>(ct)
                .ConfigureAwait(false);

            if (people is null)
                return ([], false);

            return (
            [
                .. people
                    .Where(p => !string.IsNullOrWhiteSpace(p.Login)
                                && !string.Equals(p.Type, "Bot", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Contributions)
                    .ThenBy(p => p.Login, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new ContributorView(
                        p.Login!,
                        p.HtmlUrl ?? $"https://github.com/{p.Login}",
                        p.AvatarUrl ?? "",
                        p.Contributions)),
            ], true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                  && !ct.IsCancellationRequested)
        {
            return ([], false);
        }
    }

    private sealed record GitHubContributor(
        string? Login,
        string? Type,
        int Contributions,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("avatar_url")] string? AvatarUrl);
}
