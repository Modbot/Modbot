using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Modbot.Core;

namespace Modbot.Cloud.Features.Updates;

/// <param name="Name">Which Modbot this is about: <c>server</c>, <c>companion</c>.</param>
/// <param name="Version">The release version, <c>YYYY.M.PATCH</c>.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="NotesUrl">The release page, where the notes are.</param>
/// <param name="Image">The Docker image to pull, for the things that ship as one. Null otherwise.</param>
/// <param name="Tag">The tag to pull with <see cref="Image"/>.</param>
/// <param name="ImagePushedAt">When that tag was pushed, when Docker Hub said.</param>
public sealed record ReleaseView(
    string Name,
    string Version,
    DateTimeOffset? PublishedAt,
    string? NotesUrl,
    string? Image,
    string? Tag,
    DateTimeOffset? ImagePushedAt);

/// <param name="Releases">The newest release of each thing Modbot ships.</param>
/// <param name="CompanionFeeds">The client's own release feed, by channel. Served as it stands.</param>
/// <param name="At">When this answer was fetched.</param>
public sealed record UpdatesAnswer(
    IReadOnlyList<ReleaseView> Releases,
    IReadOnlyDictionary<string, string> CompanionFeeds,
    DateTimeOffset At);

/// <summary>
/// The newest release of each thing Modbot ships, held in memory and refreshed on a timer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Served from memory, always.</strong> Every Modbot deployment and every companion in the
/// world asks this, and GitHub's rate limit is the constraint — not Cloud's. So a request never
/// causes a fetch: <see cref="UpdateRefreshService"/> refreshes every
/// <see cref="RefreshEvery"/> and a request reads whatever is there. The one exception is a cold
/// start, where the first reader kicks off the first fetch and waits for it, because the
/// alternative is a fresh Cloud answering nothing until the timer comes round.
/// </para>
/// <para>
/// <strong>A failed fetch keeps the last answer.</strong> GitHub being down, rate-limited or slow
/// must not turn into every Modbot in the world being told nothing; the previous answer is hours
/// old at worst and still true. Only a cold cache with a failed fetch is an error, and the endpoint
/// says so with a 503 rather than an empty list that reads as "you are up to date".
/// </para>
/// <para>
/// <strong>Nothing about the caller is used.</strong> No install id, no deployment id, no version,
/// nothing recorded. The answer does not depend on who asked, which is exactly why one cached copy
/// serves everybody.
/// </para>
/// </remarks>
public sealed class LatestReleases(
    GitHubReleases github,
    DockerHubTags docker,
    TimeProvider time,
    ILogger? log = null)
{
    /// <summary>
    /// How often the answer is fetched again. A release nobody hears about for a quarter of an hour
    /// has cost nobody anything, and four fetches an hour is invisible against GitHub's limit.
    /// </summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long after a failed fetch the next one is allowed on a cold cache. Without this, a
    /// GitHub outage at the moment Cloud starts would turn every arriving request into another
    /// request to GitHub.
    /// </summary>
    public static readonly TimeSpan RetryColdAfter = TimeSpan.FromMinutes(1);

    /// <summary>The name used for the server in the answer. The tag calls it <c>host</c>.</summary>
    public const string ServerName = "server";

    public const string CompanionName = "companion";

    /// <summary>Tag prefixes, as <c>&lt;prefix&gt;-v&lt;version&gt;</c>, and what each one is called here.</summary>
    private static readonly Dictionary<string, string> NamesByTagPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["host"] = ServerName,
        ["companion"] = CompanionName,
    };

    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly SemaphoreSlim _one = new(1, 1);

    private UpdatesAnswer? _answer;
    private DateTimeOffset _triedAt = DateTimeOffset.MinValue;

    /// <summary>What Cloud last learned, or null when it has never learned anything.</summary>
    public UpdatesAnswer? Answer => Volatile.Read(ref _answer);

    /// <summary>
    /// The answer to serve. Fetches only when there is nothing at all to serve yet.
    /// </summary>
    public async Task<UpdatesAnswer?> ReadAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _answer) is { } warm)
            return warm;

        await _one.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Somebody else may have filled it while this call waited.
            if (Volatile.Read(ref _answer) is { } filled)
                return filled;

            if (time.GetUtcNow() - _triedAt < RetryColdAfter)
                return null;

            await FetchAsync(ct).ConfigureAwait(false);
            return Volatile.Read(ref _answer);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Fetches again. Called by the timer. Returns false when the fetch failed, in which case
    /// whatever was already known is still what is served.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await FetchAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<bool> FetchAsync(CancellationToken ct)
    {
        _triedAt = time.GetUtcNow();

        var releases = await github.ListAsync(ct).ConfigureAwait(false);

        if (releases is null)
        {
            _log.LogDebug("Could not read the releases from GitHub; serving what Cloud already knew");
            return false;
        }

        var newest = Newest(releases);

        // Where each file of every release can be downloaded from. The client's feed names files
        // without a path, and the one it wants may be attached to a release older than the newest.
        var downloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in releases)
        {
            foreach (var file in release.Files)
                downloads.TryAdd(file.Name, file.DownloadUrl);
        }

        var feeds = newest.TryGetValue(CompanionName, out var companion)
            ? await CompanionFeedsAsync(companion, downloads, ct).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Docker Hub is asked separately and may fail on its own. When it does, the server's answer
        // still names the image and the version to pull; only the pushed date is missing.
        var tags = await docker.ListAsync(ct).ConfigureAwait(false);

        var views = new List<ReleaseView>();

        foreach (var (name, release) in newest.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var version = VersionOf(release.Tag)!;
            var isServer = string.Equals(name, ServerName, StringComparison.Ordinal);

            views.Add(new ReleaseView(
                name,
                version,
                release.PublishedAt,
                release.NotesUrl,
                isServer ? docker.Image : null,
                isServer ? version : null,
                isServer
                    ? tags?.FirstOrDefault(t => string.Equals(t.Name, version, StringComparison.Ordinal))?.PushedAt
                    : null));
        }

        Volatile.Write(ref _answer, new UpdatesAnswer(views, feeds, time.GetUtcNow()));

        _log.LogInformation(
            "Read {Count} release(s) from {Repository}: {Names}",
            views.Count,
            github.Repository,
            string.Join(", ", views.Select(v => $"{v.Name} {v.Version}")));

        return true;
    }

    /// <summary>The newest release of each thing, by the version in its tag.</summary>
    private static Dictionary<string, Release> Newest(IReadOnlyList<Release> releases)
    {
        var newest = new Dictionary<string, Release>(StringComparer.Ordinal);

        foreach (var release in releases)
        {
            if (NameOf(release.Tag) is not { } name || VersionOf(release.Tag) is not { } version)
                continue;

            if (ReleaseVersion.Parse(version) is not { } parsed)
                continue;

            if (!newest.TryGetValue(name, out var have)
                || ReleaseVersion.Compare(parsed, ReleaseVersion.Parse(VersionOf(have.Tag))!.Value) > 0)
            {
                newest[name] = release;
            }
        }

        return newest;
    }

    /// <summary>
    /// The client's own release feed for each channel, taken from the newest client release and
    /// served with every file name turned into the address it downloads from.
    /// </summary>
    /// <remarks>
    /// Velopack's web source reads <c>releases.{channel}.json</c> beside the address it was given,
    /// and follows an absolute address in a file name rather than looking beside that address. So
    /// Cloud serves the small index and the packages themselves come straight from where the
    /// release workflow put them — no download passes through Cloud.
    /// </remarks>
    private async Task<Dictionary<string, string>> CompanionFeedsAsync(
        Release release,
        IReadOnlyDictionary<string, string> downloads,
        CancellationToken ct)
    {
        var feeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in release.Files)
        {
            if (ChannelOf(file.Name) is not { } channel)
                continue;

            var text = await github.ReadFileAsync(file, ct).ConfigureAwait(false);
            if (text is null)
            {
                _log.LogDebug("Could not read {File} from the client release {Tag}", file.Name, release.Tag);
                continue;
            }

            if (WithDownloadAddresses(text, downloads) is { } feed)
                feeds[channel] = feed;
        }

        return feeds;
    }

    /// <summary>
    /// The same feed with every file name replaced by the address it downloads from. Null when the
    /// file is not a feed this understands, in which case the channel is simply not served.
    /// </summary>
    internal static string? WithDownloadAddresses(string json, IReadOnlyDictionary<string, string> downloads)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject feed || feed["Assets"] is not JsonArray assets)
                return null;

            foreach (var asset in assets.OfType<JsonObject>())
            {
                if (asset["FileName"]?.GetValue<string>() is not { Length: > 0 } name)
                    continue;

                if (name.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (downloads.TryGetValue(name, out var address))
                    asset["FileName"] = address;
            }

            return feed.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary><c>releases.win.json</c> is the <c>win</c> channel. Anything else is not a feed.</summary>
    internal static string? ChannelOf(string fileName)
    {
        const string start = "releases.";
        const string end = ".json";

        if (!fileName.StartsWith(start, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(end, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var channel = fileName[start.Length..^end.Length];

        return channel.Length is > 0 and <= 32 && channel.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? channel.ToLowerInvariant()
            : null;
    }

    /// <summary>What <c>companion-v2026.9.1</c> is about, or null when the tag is not a release tag.</summary>
    internal static string? NameOf(string tag)
    {
        var at = tag.IndexOf("-v", StringComparison.Ordinal);

        return at > 0 && NamesByTagPrefix.TryGetValue(tag[..at], out var known)
            ? known
            : at > 0 ? tag[..at].ToLowerInvariant() : null;
    }

    /// <summary>The version in <c>companion-v2026.9.1</c>.</summary>
    internal static string? VersionOf(string tag)
    {
        var at = tag.IndexOf("-v", StringComparison.Ordinal);
        return at > 0 && at + 2 < tag.Length ? tag[(at + 2)..] : null;
    }
}
