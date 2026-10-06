using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// <para><strong>The pictures of the people in an instance</strong> are the one other thing asked
/// for here. Their address is a path on a paired server (the roster carries it, and only a path of
/// that server's own picture route is taken), so the GET goes to that server and to no one else, with
/// that server's device token on it, the same token every other request to it carries, and no more
/// than that. VRChat is not contacted from this PC for them. They are read into memory at the size the
/// panel draws them, never written to disk, and let go of once no screen shows them.</para>
/// <para>Fetched on the UI thread's behalf and handed back to it, because the window redraws on a
/// timer and asks for the same icon many times a minute; a miss starts one fetch, and every ask
/// until it lands gets nothing.</para>
/// <para><strong>It only keeps what something is showing.</strong> A decoded picture costs as much
/// memory as its size in pixels however long ago it was drawn, and the Credits page alone asks for
/// a banner and a picture per person. Keeping every picture the client had ever drawn for the rest
/// of the session was the client quietly growing as somebody looked around it. So the window says
/// which addresses anything it is showing can still ask for
/// (<see cref="KeepOnly(IReadOnlySet{string})"/>) and the rest are let go of. A picture that is
/// asked for again is fetched again, which is the price, and it is only ever paid by a page
/// somebody has come back to.</para>
/// </remarks>
internal sealed class GroupPictures
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly Action _changed;
    private readonly ConcurrentDictionary<string, Bitmap?> _pictures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Pictures let go of at the last <see cref="KeepOnly(IReadOnlySet{string})"/> and not yet
    /// disposed. Touched on the UI thread only, which is the one thread that lets go of them.
    /// </summary>
    private readonly List<Bitmap> _lettingGo = [];

    private int _arrived;

    /// <summary>
    /// How many pictures have landed so far.
    /// </summary>
    /// <remarks>
    /// The window leaves the page alone when the snapshot has not changed, and a picture arriving
    /// changes what the page would draw without changing the snapshot. This is how the window
    /// notices.
    /// </remarks>
    public int Arrived => Volatile.Read(ref _arrived);

    /// <param name="changed">Called on the UI thread when a picture has arrived, so the window redraws.</param>
    public GroupPictures(HttpClient http, Action changed)
    {
        _http = http;
        _changed = changed;
    }

    /// <summary>
    /// The device token of the paired server whose own picture route this address is, or null when it
    /// is not one. Set by the host, which knows the pairings. A picture whose address this names is
    /// fetched with that token and read at <see cref="PersonPictureWidth"/>; any other is fetched with
    /// nothing attached.
    /// </summary>
    public Func<Uri, string?>? DeviceTokenFor { get; set; }

    /// <summary>
    /// How wide, in pixels, a person's picture is read: the panel draws one 48 units across, which on a
    /// large screen is at most about three times that, so a larger file is not kept at its full size.
    /// </summary>
    public const int PersonPictureWidth = 160;

    /// <summary>The picture for an address, or null while it is loading or when it will not load.</summary>
    public Bitmap? For(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var address))
            return null;

        // Only a paired server's own picture route is ever asked for with a token, and only that
        // server's: a pairing to a server on this PC is plain http, which is allowed for nothing else.
        var token = DeviceTokenFor?.Invoke(address);

        if (token is null && address.Scheme != Uri.UriSchemeHttps)
            return null;

        if (_pictures.TryGetValue(url, out var known))
            return known;

        if (_inFlight.TryAdd(url, true))
            _ = FetchAsync(url, address, token);

        return null;
    }

    /// <summary>
    /// Lets go of every picture whose address <paramref name="wanted"/> does not name.
    /// </summary>
    /// <remarks>
    /// <para>Called from the window, on the UI thread, with every address anything on the screen
    /// can still ask for — the paired servers' icons, always, and the pictures of whichever page
    /// has just been drawn. A picture that is on the screen is therefore never one of the ones let
    /// go of, which is what keeps the screen right: nothing blinks out and nothing is fetched
    /// again while it is being shown.</para>
    /// <para>A picture is disposed one call later than it is let go of, not at once. Avalonia
    /// hands the last frame it drew to its own renderer, and a picture the screen has only just
    /// stopped showing may still be in that frame; by the next page there is no frame left that
    /// could hold it.</para>
    /// </remarks>
    public void KeepOnly(IReadOnlySet<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        foreach (var gone in _lettingGo)
            gone.Dispose();

        _lettingGo.Clear();

        foreach (var address in _pictures.Keys)
        {
            if (wanted.Contains(address))
                continue;

            if (_pictures.TryRemove(address, out var picture) && picture is not null)
                _lettingGo.Add(picture);
        }
    }

    private async Task FetchAsync(string key, Uri address, string? deviceToken)
    {
        Bitmap? picture = null;
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);

            byte[] bytes;
            if (deviceToken is null)
            {
                bytes = await _http.GetByteArrayAsync(address, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            }

            using var stream = new MemoryStream(bytes);
            picture = deviceToken is null ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, PersonPictureWidth);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or ArgumentException)
        {
            Log.Debug(ex, "The group picture at {Address} could not be loaded", address);
        }

        _pictures[key] = picture;
        _inFlight.TryRemove(key, out _);

        if (picture is not null)
        {
            Interlocked.Increment(ref _arrived);
            Dispatcher.UIThread.Post(_changed);
        }
    }
}
