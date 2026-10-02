using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Modbot.Core.Files;

namespace Modbot.Core.Net;

/// <summary>What a picture link gave: the picture, or why there is none.</summary>
/// <param name="Picture">The bytes, typed by what they are (<see cref="PictureFormats.Sniff"/>).</param>
/// <param name="Problem">A plain sentence for the person, when there is no picture.</param>
public sealed record PictureLinkResult(PictureBytes? Picture, string? Problem)
{
    public static PictureLinkResult Refused(string problem) => new(null, problem);
}

/// <summary>
/// Fetches the picture behind a link somebody typed into Modbot (calendar design §15.2, added
/// 2026-10-02): an event's picture link, for the form's crop box and for Discord's event cover.
/// </summary>
/// <remarks>
/// <para>
/// A link that ends in <c>.png</c> is the easy case. Testers paste the other kinds: a page that
/// shows a picture, a signed CDN link with a query string, a host that answers with WebP. Modbot
/// fetches what is behind the link so the browser can crop it and turn it into a PNG, and so
/// Discord gets the picture rather than a page.
/// </para>
/// <para>
/// <strong>Every fetch is a request from Modbot's server to an address a person chose,</strong> the
/// same risk as a webhook address, so the guard is the webhook's and then some:
/// </para>
/// <list type="bullet">
///   <item>https only, on port 443, with no user name or password in the link;</item>
///   <item>public addresses only, checked on the address the name resolved to when the connection
///   is made (<see cref="PublicAddresses.ConnectAsync"/>), so a name that resolves to
///   <c>169.254.169.254</c> or the machine's own network reaches nothing;</item>
///   <item>no redirect is followed by the HTTP stack. Each one is read here, checked like the first
///   link, and followed at most <see cref="MaxRedirects"/> times;</item>
///   <item>at most <see cref="MaxBytes"/>, read no further, and <see cref="Timeout"/> for the
///   whole fetch, redirects and all;</item>
///   <item>the answer must be a picture by its first bytes (<see cref="PictureFormats"/>), whatever
///   it says it is. A page is read only as far as its <c>og:image</c> or <c>twitter:image</c>, which
///   is fetched under the same rules and must itself be a picture;</item>
///   <item>no proxy from the environment, which would connect somewhere the check never saw;</item>
///   <item>no cookies, and nothing of the person's is sent.</item>
/// </list>
/// <para>
/// Nothing is kept: the bytes go to the browser or to Discord and are dropped.
/// </para>
/// </remarks>
public sealed partial class PictureLinks
{
    /// <summary>The largest picture fetched: the same as a VRChat picture upload.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>How much of a page is read to find its picture.</summary>
    public const int PageBytes = 512 * 1024;

    /// <summary>Redirects followed, each checked as the first link was.</summary>
    public const int MaxRedirects = 3;

    /// <summary>The longest link taken, the same as the picture link a save takes.</summary>
    public const int MaxLinkLength = 2048;

    /// <summary>The whole fetch, redirects and a page's picture included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public const string NotHttps = "The picture link must start with https://.";
    public const string Private = "The picture link points at a private address.";
    public const string NotAPicture = "The link is not a picture.";
    public const string TooBig = "The picture is larger than 10 MB.";
    public const string TooSlow = "The link took too long to answer.";
    public const string TooManyRedirects = "The link sends Modbot on too many times.";
    public const string Unreachable = "Could not fetch the picture link.";

    private static readonly Lazy<PictureLinks> SharedLinks = new(() => new PictureLinks());

    /// <summary>The one every caller uses outside tests: one pool of connections for the process.</summary>
    public static PictureLinks Shared => SharedLinks.Value;

    private readonly HttpMessageInvoker _http;

    /// <param name="handler">
    /// For tests only. Null builds the guarded handler: <see cref="PublicAddresses.ConnectAsync"/>
    /// on every connection, no redirects, no proxy, no cookies.
    /// </param>
    public PictureLinks(HttpMessageHandler? handler = null)
    {
        _http = new HttpMessageInvoker(handler ?? GuardedHandler(), disposeHandler: handler is null);
    }

    /// <summary>The handler every real fetch goes through.</summary>
    public static SocketsHttpHandler GuardedHandler() => new()
    {
        ConnectCallback = (context, ct) =>
            PublicAddresses.ConnectAsync(context, host => new PrivateAddressException(host), ct),
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Why a link may not be fetched at all, before anything is sent, or null when it may.
    /// </summary>
    public static string? Problem(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!link.IsAbsoluteUri || link.Scheme != Uri.UriSchemeHttps || link.Port != 443)
            return NotHttps;

        if (link.UserInfo.Length > 0)
            return NotHttps;

        return PublicAddresses.IsBlockedHost(link.Host) ? Private : null;
    }

    /// <summary>The picture behind <paramref name="url"/>, or the reason there is none.</summary>
    public async Task<PictureLinkResult> FetchAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > MaxLinkLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var link))
        {
            return PictureLinkResult.Refused(NotHttps);
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(Timeout);

        try
        {
            return await FollowAsync(link, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PictureLinkResult.Refused(TooSlow);
        }
        catch (HttpRequestException e) when (IsPrivate(e))
        {
            return PictureLinkResult.Refused(Private);
        }
        catch (HttpRequestException)
        {
            return PictureLinkResult.Refused(Unreachable);
        }
        catch (RegexMatchTimeoutException)
        {
            return PictureLinkResult.Refused(NotAPicture);
        }
    }

    private async Task<PictureLinkResult> FollowAsync(Uri link, CancellationToken ct)
    {
        var next = link;
        var pageRead = false;

        for (var hop = 0; ; hop++)
        {
            if (Problem(next) is { } problem)
                return PictureLinkResult.Refused(problem);

            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Accept.ParseAdd("image/*");
            request.Headers.Accept.ParseAdd("text/html;q=0.5");
            request.Headers.UserAgent.ParseAdd("Modbot");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (hop >= MaxRedirects)
                    return PictureLinkResult.Refused(TooManyRedirects);

                if (response.Headers.Location is not { } location)
                    return PictureLinkResult.Refused(Unreachable);

                next = location.IsAbsoluteUri ? location : new Uri(next, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                return PictureLinkResult.Refused(Unreachable);

            if (response.Content.Headers.ContentLength > MaxBytes)
                return PictureLinkResult.Refused(TooBig);

            var bytes = await ReadAsync(response.Content, MaxBytes + 1, ct).ConfigureAwait(false);

            if (PictureFormats.Sniff(bytes) is { } type)
            {
                return bytes.Length > MaxBytes
                    ? PictureLinkResult.Refused(TooBig)
                    : new PictureLinkResult(new PictureBytes(bytes, type), null);
            }

            // A page: its own picture, once. A page whose picture is another page is not followed.
            if (pageRead || !LooksLikePage(response.Content.Headers.ContentType, bytes))
                return PictureLinkResult.Refused(NotAPicture);

            var html = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, PageBytes));

            if (PagePicture(html) is not { } found
                || !Uri.TryCreate(next, found, out var picture))
            {
                return PictureLinkResult.Refused(NotAPicture);
            }

            if (hop >= MaxRedirects)
                return PictureLinkResult.Refused(TooManyRedirects);

            pageRead = true;
            next = picture;
        }
    }

    private static bool IsPrivate(Exception e)
    {
        for (Exception? at = e; at is not null; at = at.InnerException)
        {
            if (at is PrivateAddressException)
                return true;
        }

        return false;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool LooksLikePage(MediaTypeHeaderValue? type, byte[] bytes)
    {
        if (type?.MediaType is { } media
            && (media.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || media.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var start = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The picture a page names for link previews: <c>og:image</c> (its secure form first), then
    /// <c>twitter:image</c>. Null when it names none.
    /// </summary>
    public static string? PagePicture(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        string[] wanted = ["og:image:secure_url", "og:image", "og:image:url", "twitter:image", "twitter:image:src"];
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match tag in MetaTag().Matches(html))
        {
            string? key = null;
            string? content = null;

            foreach (Match attribute in Attribute().Matches(tag.Value))
            {
                var name = attribute.Groups["name"].Value;
                var value = attribute.Groups["dq"].Success ? attribute.Groups["dq"].Value
                    : attribute.Groups["sq"].Success ? attribute.Groups["sq"].Value
                    : attribute.Groups["bare"].Value;

                if (name.Equals("property", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    key = value.Trim();
                }
                else if (name.Equals("content", StringComparison.OrdinalIgnoreCase))
                {
                    content = WebUtility.HtmlDecode(value).Trim();
                }
            }

            if (key is not null && content is { Length: > 0 })
                found.TryAdd(key, content);
        }

        foreach (var key in wanted)
        {
            if (found.TryGetValue(key, out var value))
                return value;
        }

        return null;
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

    [GeneratedRegex("<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetaTag();

    [GeneratedRegex(
        "(?<name>[a-zA-Z:_-]+)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s\"'>]+))",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribute();

    /// <summary>A name that resolved only to addresses Modbot does not reach.</summary>
    public sealed class PrivateAddressException(string host) : HttpRequestException($"{host} is a private address.");
}
