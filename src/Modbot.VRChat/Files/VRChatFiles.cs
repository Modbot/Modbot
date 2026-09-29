namespace Modbot.VRChat.Files;

/// <summary>One picture or video fetched from VRChat, as it came off the wire.</summary>
/// <param name="Bytes">The file itself.</param>
/// <param name="ContentType">What VRChat said the bytes are, e.g. <c>image/png</c>.</param>
public sealed record VRChatFile(byte[] Bytes, string ContentType);

/// <summary>What came of asking VRChat for a file.</summary>
/// <remarks>
/// Its own list rather than the gate's usual <c>VRChatResult</c>, because the two answers that
/// matter most here are not statuses at all: VRChat can say 200 and send a PDF, and it can say
/// 200 and send a gigabyte. A status code has nothing to say about either, and the endpoint has
/// to tell them apart to answer properly.
/// </remarks>
public enum VRChatFileOutcome
{
    /// <summary>The file is here.</summary>
    Fetched,

    /// <summary>The address is not one VRChat serves files from. Nothing was sent.</summary>
    NotVRChatAddress,

    /// <summary>No VRChat session to fetch it on -- not set up yet, waiting to sign in, or a demo.</summary>
    NoSession,

    /// <summary>VRChat says there is no such file.</summary>
    NotFound,

    /// <summary>VRChat sent something that is not a picture or a video.</summary>
    NotShowable,

    /// <summary>The file runs past <see cref="VRChatFiles.MaxBytes"/>.</summary>
    TooBig,

    /// <summary>VRChat could not be reached, or answered with an error.</summary>
    Failed,
}

/// <summary>The answer to one file fetch.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="File">The file, on <see cref="VRChatFileOutcome.Fetched"/> and never otherwise.</param>
/// <param name="Problem">A sentence for a log or an error body, when something went wrong.</param>
public sealed record VRChatFileResult(VRChatFileOutcome Outcome, VRChatFile? File = null, string? Problem = null)
{
    public static VRChatFileResult Ok(VRChatFile file) => new(VRChatFileOutcome.Fetched, file);

    public static VRChatFileResult Problems(VRChatFileOutcome outcome, string problem) =>
        new(outcome, null, problem);
}

/// <summary>
/// The rules for fetching a picture or a video from VRChat: which addresses count, how big a
/// file may be, and what kinds Modbot will hand on.
/// </summary>
/// <remarks>
/// <para>
/// VRChat's file addresses need the session cookie and answer with a redirect to a content
/// delivery host, so a browser asking for one directly gets nothing. Modbot fetches it instead
/// (VRChat files design). The rules live here rather than in the endpoint because the gate
/// enforces two of them while it is reading the response and the endpoint enforces the third,
/// and a second copy of the address list is a second thing to keep right.
/// </para>
/// <para>
/// <strong>These calls are not paced and do not belong to a bucket.</strong> VRChat's file and
/// image addresses are not rate limited -- the maintainer confirmed it on 2026-09-17 -- and
/// giving them a bucket by analogy with the API endpoints would make a member list of forty
/// faces queue behind itself for no reason. They still go through the gate, because the session
/// cookie lives there and nothing outside it may hold one.
/// </para>
/// </remarks>
public static class VRChatFiles
{
    /// <summary>The domain VRChat serves files from. Its subdomains are the delivery hosts.</summary>
    public const string Domain = "vrchat.cloud";

    /// <summary>
    /// The most of a file Modbot reads. A profile picture is a few hundred kilobytes; this is
    /// the size at which somebody is using the address for something else.
    /// </summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>How many redirects are followed before a fetch gives up.</summary>
    public const int MaxRedirects = 5;

    /// <summary>How long one fetch may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether this is an address VRChat serves files from.
    /// </summary>
    /// <remarks>
    /// <c>vrchat.cloud</c> itself and anything under it, over <c>https</c> only. That covers
    /// <c>api.vrchat.cloud</c>, where a stored file address points, and <c>assets.</c>,
    /// <c>files.</c> and the per-deployment delivery hosts it redirects to, whose names change
    /// without notice. Matched on the label boundary, so <c>notvrchat.cloud</c> and
    /// <c>vrchat.cloud.example.com</c> are not VRChat.
    /// </remarks>
    public static bool IsVRChatAddress(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
            return false;

        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = url.Host;

        return host.Equals(Domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a fetched address is one, when it arrives as text.</summary>
    public static bool IsVRChatAddress(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && IsVRChatAddress(parsed);

    /// <summary>
    /// VRChat's API host: the one a stored picture address points at, and the only one a file
    /// fetch sends the session cookie to.
    /// </summary>
    public const string ApiHost = "api.vrchat.cloud";

    /// <summary>The paths on <see cref="ApiHost"/> that serve a picture or a video.</summary>
    private static readonly string[] PicturePaths = ["/api/1/file/", "/api/1/image/"];

    /// <summary>
    /// Escapes that could turn a picture path into another path once VRChat decodes it: a dot,
    /// a slash, a backslash.
    /// </summary>
    private static readonly string[] PathEscapes = ["%2e", "%2f", "%5c"];

    /// <summary>
    /// Whether this is an address a picture or a video may be fetched from: one of VRChat's
    /// (<see cref="IsVRChatAddress(Uri)"/>), and on <see cref="ApiHost"/> only its file and
    /// image paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The API host is where a fetch carries the service account's session cookie, and it serves
    /// every other API call too. Without the path rule, the route would send any API call a
    /// caller wrote -- <c>/api/1/auth/user</c>, say -- as the service account, on a call that no
    /// bucket paces because file fetches have none (VRChat files design §4.3). So on that host
    /// the path must start <c>/api/1/file/</c> or <c>/api/1/image/</c>, and nothing else is sent.
    /// </para>
    /// <para>
    /// The check reads the path after <see cref="Uri"/> has resolved <c>..</c> and backslashes,
    /// so <c>/api/1/file/../auth/user</c> is checked as <c>/api/1/auth/user</c>. An escaped dot,
    /// slash or backslash in the path is refused outright, because VRChat's server may decode it
    /// after this check has passed. The other VRChat hosts are delivery hosts: they never get
    /// the cookie, and their paths are whatever VRChat makes them, so they are not limited.
    /// Only the path is checked; ids in it are not (foundation §3.1.1).
    /// </para>
    /// </remarks>
    public static bool IsPictureAddress(Uri? url)
    {
        if (!IsVRChatAddress(url))
            return false;

        if (!url!.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
            return true;

        var path = url.AbsolutePath;

        foreach (var escape in PathEscapes)
        {
            if (path.Contains(escape, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var prefix in PicturePaths)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Whether an address is one a picture may be fetched from, when it arrives as text.</summary>
    public static bool IsPictureAddress(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && IsPictureAddress(parsed);

    /// <summary>
    /// Types that look like pictures and are not. SVG is a document with scripts in it, and one
    /// served from Modbot's own address -- which is what a proxy makes it -- runs those scripts
    /// against a moderator's session the moment somebody opens the address in a tab. The
    /// evidence store refuses it for exactly this reason, and so does this.
    /// </summary>
    private static readonly IReadOnlySet<string> NeverShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/svg+xml",
        "image/svg",
    };

    /// <summary>
    /// Whether Modbot will hand these bytes on: pictures and video, nothing else.
    /// </summary>
    /// <remarks>
    /// The point is a face in a member list, and a browser that is handed HTML from somewhere
    /// else runs it. VRChat's own answer decides the type; nothing a caller says does.
    /// </remarks>
    public static bool IsShowable(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var type = BareType(contentType);

        if (NeverShown.Contains(type))
            return false;

        return type.StartsWith("image/", StringComparison.Ordinal)
            || type.StartsWith("video/", StringComparison.Ordinal);
    }

    /// <summary>The type without its parameters, lowercased, as the cache stores it.</summary>
    public static string BareType(string contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var bare = semicolon >= 0 ? contentType[..semicolon] : contentType;

        return bare.Trim().ToLowerInvariant();
    }
}
