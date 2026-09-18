using System.Net;
using System.Text;

namespace Modbot.Cloud.Tests;

/// <summary>
/// GitHub and Docker Hub, stood in for: canned answers, a count of what was asked, and a switch
/// that makes both refuse.
/// </summary>
public sealed class FakeReleaseHost : HttpMessageHandler
{
    public const string Repository = "example/modbot";

    public const string Image = "example/modbot-host";

    /// <summary>The newest client release, which is also where its feed files are attached.</summary>
    public const string ClientVersion = "2026.9.10";

    /// <summary>The newest server release.</summary>
    public const string ServerVersion = "2026.9.3";

    private const string ReleasesUrl = $"https://api.github.com/repos/{Repository}/releases";

    private const string WinFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/11";
    private const string LinuxFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/12";

    /// <summary>Where the client actually downloads a package from, and what Cloud must write into the feed.</summary>
    public const string PackageDownloadUrl =
        $"https://github.com/{Repository}/releases/download/companion-v{ClientVersion}/Modbot-{ClientVersion}-full.nupkg";

    private int _releasesAsked;
    private int _tagsAsked;

    /// <summary>True makes every call fail, the way an outage or a spent rate limit does.</summary>
    public bool Refusing { get; set; }

    public int ReleasesAsked => Volatile.Read(ref _releasesAsked);

    public int TagsAsked => Volatile.Read(ref _tagsAsked);

    /// <summary>The client's own feed, as the release workflow published it: names, not addresses.</summary>
    public static string WindowsFeed { get; } =
        $$"""
        {"Assets":[{"PackageId":"Modbot","Version":"{{ClientVersion}}","Type":"Full",
        "FileName":"Modbot-{{ClientVersion}}-full.nupkg","SHA1":"0123456789abcdef0123456789abcdef01234567",
        "SHA256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","Size":91234567,
        "NotesMarkdown":"Fixed things.","NotesHTML":"<p>Fixed things.</p>"}]}
        """;

    private static string LinuxFeed { get; } =
        $$"""
        {"Assets":[{"PackageId":"Modbot","Version":"{{ClientVersion}}","Type":"Full",
        "FileName":"Modbot-{{ClientVersion}}-linux-full.nupkg","SHA1":"89abcdef0123456789abcdef0123456789abcdef",
        "SHA256":"89abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567","Size":81234567}]}
        """;

    private static string ReleasesJson { get; } =
        $$"""
        [
          {
            "tag_name": "companion-v{{ClientVersion}}",
            "html_url": "https://github.com/{{Repository}}/releases/tag/companion-v{{ClientVersion}}",
            "published_at": "2026-09-17T10:00:00+00:00",
            "draft": false,
            "prerelease": false,
            "assets": [
              { "name": "releases.win.json", "url": "{{WinFeedAssetUrl}}",
                "browser_download_url": "https://github.com/{{Repository}}/releases/download/companion-v{{ClientVersion}}/releases.win.json" },
              { "name": "releases.linux.json", "url": "{{LinuxFeedAssetUrl}}",
                "browser_download_url": "https://github.com/{{Repository}}/releases/download/companion-v{{ClientVersion}}/releases.linux.json" },
              { "name": "Modbot-{{ClientVersion}}-full.nupkg", "url": "https://api.github.com/repos/{{Repository}}/releases/assets/13",
                "browser_download_url": "{{PackageDownloadUrl}}" }
            ]
          },
          {
            "tag_name": "host-v{{ServerVersion}}",
            "html_url": "https://github.com/{{Repository}}/releases/tag/host-v{{ServerVersion}}",
            "published_at": "2026-09-16T10:00:00+00:00",
            "draft": false,
            "prerelease": false,
            "assets": []
          },
          {
            "tag_name": "companion-v2026.9.9",
            "html_url": "https://github.com/{{Repository}}/releases/tag/companion-v2026.9.9",
            "published_at": "2026-09-10T10:00:00+00:00",
            "draft": false,
            "prerelease": false,
            "assets": []
          },
          {
            "tag_name": "host-v2026.9.4",
            "html_url": "https://github.com/{{Repository}}/releases/tag/host-v2026.9.4",
            "published_at": "2026-09-18T10:00:00+00:00",
            "draft": true,
            "prerelease": false,
            "assets": []
          },
          {
            "tag_name": "companion-v2026.10.0",
            "html_url": "https://github.com/{{Repository}}/releases/tag/companion-v2026.10.0",
            "published_at": "2026-09-18T10:00:00+00:00",
            "draft": false,
            "prerelease": true,
            "assets": []
          }
        ]
        """;

    private static string TagsJson { get; } =
        $$"""
        {"results":[
          {"name":"{{ServerVersion}}","last_updated":"2026-09-16T10:30:00+00:00"},
          {"name":"latest","last_updated":"2026-09-16T10:30:00+00:00"}
        ]}
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri?.GetLeftPart(UriPartial.Path) ?? "";

        if (url.StartsWith(ReleasesUrl, StringComparison.Ordinal))
            Interlocked.Increment(ref _releasesAsked);

        if (url.StartsWith("https://hub.docker.com/", StringComparison.Ordinal))
            Interlocked.Increment(ref _tagsAsked);

        if (Refusing)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var body = url switch
        {
            _ when url.StartsWith(ReleasesUrl, StringComparison.Ordinal) => ReleasesJson,
            WinFeedAssetUrl => WindowsFeed,
            LinuxFeedAssetUrl => LinuxFeed,
            _ when url.StartsWith("https://hub.docker.com/", StringComparison.Ordinal) => TagsJson,
            _ => null,
        };

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
