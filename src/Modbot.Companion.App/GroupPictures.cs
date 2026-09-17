using System.Collections.Concurrent;
using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The group icons the sidebar and the server cards show, fetched once and kept in memory.
/// </summary>
/// <remarks>
/// <para><strong>What this reads, and what is sent.</strong> One HTTP GET per icon address, to
/// the address the paired server gave at pairing -- VRChat's picture host, in practice -- and
/// nothing goes with it: no token, no cookie, no identity. The bytes come back and become a
/// picture; nothing is written to your disk, and the address is not reported anywhere. A picture
/// that will not load leaves the name standing on its own.</para>
/// <para>Fetched on the UI thread's behalf and handed back to it, because the window redraws on a
/// timer and asks for the same icon many times a minute; a miss starts one fetch, and every ask
/// until it lands gets nothing.</para>
/// </remarks>
internal sealed class GroupPictures
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly Action _changed;
    private readonly ConcurrentDictionary<string, Bitmap?> _pictures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _inFlight = new(StringComparer.Ordinal);

    /// <param name="changed">Called on the UI thread when a picture has arrived, so the window redraws.</param>
    public GroupPictures(HttpClient http, Action changed)
    {
        _http = http;
        _changed = changed;
    }

    /// <summary>The picture for an address, or null while it is loading or when it will not load.</summary>
    public Bitmap? For(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps)
            return null;

        if (_pictures.TryGetValue(url, out var known))
            return known;

        if (_inFlight.TryAdd(url, true))
            _ = FetchAsync(url, address);

        return null;
    }

    private async Task FetchAsync(string key, Uri address)
    {
        Bitmap? picture = null;
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var bytes = await _http.GetByteArrayAsync(address, timeout.Token).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            picture = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or ArgumentException)
        {
            Log.Debug(ex, "The group picture at {Address} could not be loaded", address);
        }

        _pictures[key] = picture;
        _inFlight.TryRemove(key, out _);

        if (picture is not null)
            Dispatcher.UIThread.Post(_changed);
    }
}
