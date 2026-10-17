using System.Net;
using Modbot.Core.Files;

namespace Modbot.Core.Net;

/// <summary>What a video link gave: the video, or why there is none.</summary>
/// <param name="Bytes">The video, as it came.</param>
/// <param name="Type">The type its bytes have (<see cref="VideoFormats.Sniff"/>).</param>
/// <param name="Problem">A plain sentence for the person, when there is no video.</param>
public sealed record VideoLinkResult(byte[]? Bytes, string? Type, string? Problem)
{
    public static VideoLinkResult Refused(string problem) => new(null, null, problem);
}

/// <summary>
/// Fetches the video behind a link an event's Bluesky post was given, at the moment the post goes out.
/// </summary>
/// <remarks>
/// <para>
/// A request from Modbot's server to an address a person chose, so the guard is the picture links'
/// (<see cref="PictureLinks"/>): https on port 443 with no user name or password, public addresses only
/// checked when the connection is made, no proxy and no cookies, and no redirect followed by the HTTP stack:
/// each one is read here, checked like the first link, and followed at most
/// <see cref="PictureLinks.MaxRedirects"/> times.
/// </para>
/// <para>
/// What differs from a picture: at most <see cref="VideoFormats.MaxBytes"/> (read no further), a longer
/// <see cref="Timeout"/> for the whole fetch, and a video by its first bytes (<see cref="VideoFormats"/>) whatever
/// the host says it is. A page is never read for a video of its own. Nothing is kept.
/// </para>
/// </remarks>
public sealed class VideoLinks
{
    /// <summary>The whole fetch, redirects included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public const string NotHttps = "The video link must start with https://.";
    public const string Private = "The video link points at a private address.";
    public const string NotAVideo = "The link is not a video.";
    public const string TooBig = "The video is larger than 50 MB.";
    public const string TooSlow = "The video link took too long to answer.";
    public const string TooManyRedirects = "The video link sends Modbot on too many times.";
    public const string Unreachable = "Could not fetch the video link.";

    private static readonly Lazy<VideoLinks> SharedLinks = new(() => new VideoLinks());

    /// <summary>The one every caller uses outside tests: one pool of connections for the process.</summary>
    public static VideoLinks Shared => SharedLinks.Value;

    private readonly HttpMessageInvoker _http;

    /// <param name="handler">
    /// For tests only. Null builds the guarded handler (<see cref="PictureLinks.GuardedHandler"/>).
    /// </param>
    public VideoLinks(HttpMessageHandler? handler = null)
    {
        _http = new HttpMessageInvoker(handler ?? PictureLinks.GuardedHandler(), disposeHandler: handler is null);
    }

    /// <summary>
    /// Why a link may not be taken for a video at all, before anything is sent, or null when it may: it
    /// must be a link that <see cref="PictureLinks.Problem"/> lets through.
    /// </summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > PictureLinks.MaxLinkLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var link))
        {
            return NotHttps;
        }

        return PictureLinks.Problem(link) is { } problem
            ? problem == PictureLinks.Private ? Private : NotHttps
            : null;
    }

    /// <summary>The video behind <paramref name="url"/>, or the reason there is none.</summary>
    public async Task<VideoLinkResult> FetchAsync(string? url, CancellationToken ct)
    {
        if (Problem(url) is { } refused)
            return VideoLinkResult.Refused(refused);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(Timeout);

        try
        {
            return await FollowAsync(new Uri(url!.Trim()), timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return VideoLinkResult.Refused(TooSlow);
        }
        catch (HttpRequestException e) when (IsPrivate(e))
        {
            return VideoLinkResult.Refused(Private);
        }
        catch (HttpRequestException)
        {
            return VideoLinkResult.Refused(Unreachable);
        }
    }

    private async Task<VideoLinkResult> FollowAsync(Uri link, CancellationToken ct)
    {
        var next = link;

        for (var hop = 0; ; hop++)
        {
            if (PictureLinks.Problem(next) is { } problem)
                return VideoLinkResult.Refused(problem == PictureLinks.Private ? Private : NotHttps);

            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Accept.ParseAdd("video/*");
            request.Headers.UserAgent.ParseAdd("Modbot");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (hop >= PictureLinks.MaxRedirects)
                    return VideoLinkResult.Refused(TooManyRedirects);

                if (response.Headers.Location is not { } location)
                    return VideoLinkResult.Refused(Unreachable);

                next = location.IsAbsoluteUri ? location : new Uri(next, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                return VideoLinkResult.Refused(Unreachable);

            if (response.Content.Headers.ContentLength > VideoFormats.MaxBytes)
                return VideoLinkResult.Refused(TooBig);

            var bytes = await ReadAsync(response.Content, VideoFormats.MaxBytes + 1, ct).ConfigureAwait(false);

            if (bytes.Length > VideoFormats.MaxBytes)
                return VideoLinkResult.Refused(TooBig);

            return VideoFormats.Sniff(bytes) is { } type
                ? new VideoLinkResult(bytes, type, null)
                : VideoLinkResult.Refused(NotAVideo);
        }
    }

    private static bool IsPrivate(Exception e)
    {
        for (Exception? at = e; at is not null; at = at.InnerException)
        {
            if (at is PictureLinks.PrivateAddressException)
                return true;
        }

        return false;
    }

    private static async Task<byte[]> ReadAsync(HttpContent content, int most, CancellationToken ct)
    {
        await using var body = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            var room = most - (int)copy.Length;
            copy.Write(buffer, 0, Math.Min(read, room));

            if (copy.Length >= most)
                break;
        }

        return copy.ToArray();
    }
}
