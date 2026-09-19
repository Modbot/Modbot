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

    /// <summary>
    /// The preview client, published after the release above and numbered higher, so a test that
    /// passes only because the preview happened to be older would not pass.
    /// </summary>
    public const string PreviewVersion = "2026.9.11-preview.1";

    private const string ReleasesUrl = $"https://api.github.com/repos/{Repository}/releases";

    private const string WinFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/11";
    private const string LinuxFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/12";

    private const string WinPreviewFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/21";
    private const string LinuxPreviewFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/22";

    /// <summary>
    /// A releases.win.json attached to the preview release, which the workflow would never put
    /// there. It is served, and readable, so that a test proving Cloud ignores it would fail if
    /// Cloud stopped ignoring it.
    /// </summary>
    private const string StrayWinFeedAssetUrl = $"https://api.github.com/repos/{Repository}/releases/assets/23";

    /// <summary>Where the client actually downloads a package from, and what Cloud must write into the feed.</summary>
    public const string PackageDownloadUrl =
        $"https://github.com/{Repository}/releases/download/companion-v{ClientVersion}/Modbot-{ClientVersion}-full.nupkg";

    /// <summary>The same, for the preview.</summary>
    public const string PreviewPackageDownloadUrl =
        $"https://github.com/{Repository}/releases/download/companion-v{PreviewVersion}/Modbot-{PreviewVersion}-win-preview-full.nupkg";

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

    private static string WindowsPreviewFeed { get; } =
        $$"""
        {"Assets":[{"PackageId":"Modbot","Version":"{{PreviewVersion}}","Type":"Full",
        "FileName":"Modbot-{{PreviewVersion}}-win-preview-full.nupkg","SHA1":"abcdef0123456789abcdef0123456789abcdef01",
        "SHA256":"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789","Size":92234567}]}
        """;

    private static string LinuxPreviewFeed { get; } =
        $$"""
        {"Assets":[{"PackageId":"Modbot","Version":"{{PreviewVersion}}","Type":"Full",
        "FileName":"Modbot-{{PreviewVersion}}-linux-preview-full.nupkg","SHA1":"456789abcdef0123456789abcdef0123456789ab",
        "SHA256":"456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123","Size":82234567}]}
        """;

    /// <summary>A version nobody should ever be served, on a channel a pre-release may not answer for.</summary>
    private static string StrayWindowsFeed { get; } =
        """
        {"Assets":[{"PackageId":"Modbot","Version":"9999.9.9","Type":"Full",
        "FileName":"Modbot-9999.9.9-full.nupkg","SHA1":"ffffffffffffffffffffffffffffffffffffffff",
        "SHA256":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","Size":1}]}
        """;

    private static string ReleasesJson { get; } =
        $$"""
        [
          {
            "tag_name": "companion-v{{PreviewVersion}}",
            "html_url": "https://github.com/{{Repository}}/releases/tag/companion-v{{PreviewVersion}}",
            "published_at": "2026-09-18T12:00:00+00:00",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "releases.win-preview.json", "url": "{{WinPreviewFeedAssetUrl}}",
                "browser_download_url": "https://github.com/{{Repository}}/releases/download/companion-v{{PreviewVersion}}/releases.win-preview.json" },
              { "name": "releases.linux-preview.json", "url": "{{LinuxPreviewFeedAssetUrl}}",
                "browser_download_url": "https://github.com/{{Repository}}/releases/download/companion-v{{PreviewVersion}}/releases.linux-preview.json" },
              { "name": "releases.win.json", "url": "{{StrayWinFeedAssetUrl}}",
                "browser_download_url": "https://github.com/{{Repository}}/releases/download/companion-v{{PreviewVersion}}/releases.win.json" },
              { "name": "Modbot-{{PreviewVersion}}-win-preview-full.nupkg", "url": "https://api.github.com/repos/{{Repository}}/releases/assets/24",
                "browser_download_url": "{{PreviewPackageDownloadUrl}}" }
            ]
          },
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

        // The list call is the exact releases address; a file read is one of its assets, whose
        // address happens to start with the same prefix. Matching by prefix here would count an
        // asset read as another list call and serve it the list's own body instead of its own.
        if (string.Equals(url, ReleasesUrl, StringComparison.Ordinal))
            Interlocked.Increment(ref _releasesAsked);

        if (url.StartsWith("https://hub.docker.com/", StringComparison.Ordinal))
            Interlocked.Increment(ref _tagsAsked);

        if (Refusing)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var body = url switch
        {
            _ when string.Equals(url, ReleasesUrl, StringComparison.Ordinal) => ReleasesJson,
            WinFeedAssetUrl => WindowsFeed,
            LinuxFeedAssetUrl => LinuxFeed,
            WinPreviewFeedAssetUrl => WindowsPreviewFeed,
            LinuxPreviewFeedAssetUrl => LinuxPreviewFeed,
            StrayWinFeedAssetUrl => StrayWindowsFeed,
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
