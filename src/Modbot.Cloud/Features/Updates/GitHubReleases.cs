using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Cloud.Features.Updates;

/// <param name="Name">The file's name in the release, such as <c>releases.win.json</c>.</param>
/// <param name="ReadUrl">
/// The API address the file's bytes are read from. Works for a private repository when a token is
/// set, which the plain download address does not.
/// </param>
/// <param name="DownloadUrl">The address anybody downloads it from. Handed to clients, never read here.</param>
public sealed record ReleaseFile(string Name, string ReadUrl, string DownloadUrl);

/// <param name="Tag">The git tag, such as <c>companion-v2026.9.1</c>.</param>
/// <param name="PublishedAt">When GitHub published it.</param>
/// <param name="NotesUrl">The release page, which is where the notes are.</param>
/// <param name="Files">Everything attached to the release.</param>
public sealed record Release(string Tag, DateTimeOffset? PublishedAt, string? NotesUrl, IReadOnlyList<ReleaseFile> Files);

/// <summary>
/// Reads the project's releases from GitHub.
/// </summary>
/// <remarks>
/// <para>
/// One call lists the releases; a second reads one attached file when the companion feed needs it.
/// Nothing here is called per request — <see cref="LatestReleases"/> holds the answer and refreshes
/// it on a timer — so the number of Modbots in the world does not reach GitHub at all.
/// </para>
/// <para>
/// <strong>Rate limit.</strong> GitHub allows 60 unauthenticated requests an hour per address and
/// 5,000 with a token. A refresh costs one list call plus one read per companion channel, so three;
/// at <see cref="LatestReleases.RefreshEvery"/> that is a few dozen a day. A refusal keeps the
/// previous answer rather than being retried immediately, which is the shape that turns a rate
/// limit into a ban.
/// </para>
/// <para>
/// A failure is null, never an exception. Cloud serves what it last knew.
/// </para>
/// </remarks>
public sealed class GitHubReleases(HttpClient client, string? token, string repository)
{
    public const string HttpClientName = "github-releases";

    /// <summary>The repository read when <c>GITHUB_RELEASES_REPOSITORY</c> is not set.</summary>
    public const string DefaultRepository = "binn/Modbot";

    /// <summary>GitHub refuses a request with no user agent.</summary>
    private const string UserAgent = "Modbot-Cloud";

    /// <summary>
    /// A release index is a few kilobytes. A cap keeps a wrong file — or a repository somebody
    /// else controls — from being read into Cloud's memory.
    /// </summary>
    public const int MostFileBytes = 512 * 1024;

    public string Repository { get; } = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim();

    /// <summary>
    /// Every published release, newest first as GitHub returns them. Null when GitHub could not be
    /// read; an empty list means it answered and there are none.
    /// </summary>
    /// <remarks>
    /// Drafts and pre-releases are left out. A pre-release is somebody's trial balloon, and telling
    /// every deployment in the world to update to one is not what publishing it meant.
    /// </remarks>
    public async Task<IReadOnlyList<Release>?> ListAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Repository}/releases?per_page=100";

        try
        {
            using var request = Request(HttpMethod.Get, url, "application/vnd.github+json");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var releases = await response.Content
                .ReadFromJsonAsync<List<GitHubRelease>>(ct)
                .ConfigureAwait(false);

            if (releases is null)
                return null;

            return
            [
                .. releases
                    .Where(r => r is { Draft: false, Prerelease: false } && !string.IsNullOrWhiteSpace(r.TagName))
                    .Select(r => new Release(
                        r.TagName!,
                        r.PublishedAt,
                        r.HtmlUrl,
                        [
                            .. (r.Assets ?? [])
                                .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                                            && !string.IsNullOrWhiteSpace(a.Url)
                                            && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
                                .Select(a => new ReleaseFile(a.Name!, a.Url!, a.BrowserDownloadUrl!)),
                        ])),
            ];
        }
        catch (Exception e) when (Transient(e, ct))
        {
            return null;
        }
    }

    /// <summary>Reads one attached file as text, or null when it could not be read.</summary>
    public async Task<string?> ReadFileAsync(ReleaseFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            using var request = Request(HttpMethod.Get, file.ReadUrl, "application/octet-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is > MostFileBytes)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var buffer = new char[MostFileBytes];
            var read = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);

            // Exactly full means there is more, and a release index that large is not one of ours.
            return read >= MostFileBytes ? null : new string(buffer, 0, read);
        }
        catch (Exception e) when (Transient(e, ct))
        {
            return null;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string accept)
    {
        var request = new HttpRequestMessage(method, url);

        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    private static bool Transient(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException or JsonException or IOException
        && !ct.IsCancellationRequested;

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        bool Draft,
        bool Prerelease,
        List<GitHubReleaseAsset>? Assets);

    private sealed record GitHubReleaseAsset(
        string? Name,
        string? Url,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
