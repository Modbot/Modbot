using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Logging;
using Modbot.Core.Net;
using Modbot.Moderation;
using Serilog;

namespace Modbot.AI.Moderation;

/// <summary>One picture as it goes to the model (AI moderation design §17).</summary>
/// <param name="Key">A short name used only inside one request, so the model can say which matched.</param>
/// <param name="Bytes">The picture itself, when the provider will not take a link. Null when the link goes.</param>
/// <param name="MediaType">The type the server said the bytes are, e.g. <c>image/png</c>.</param>
public sealed record PictureToCheck(string Key, string Label, string Url, BinaryData? Bytes, string? MediaType);

/// <summary>
/// Which pictures a check sends, and how (AI moderation design §17).
/// </summary>
/// <remarks>
/// <para>
/// Pictures cost far more than text — a single picture can be worth more tokens than the whole
/// message around it — so a rule sends at most <see cref="MostPictures"/> of them, each at most
/// <see cref="MostBytes"/>, and the call goes through the usage and spend limits like any other.
/// </para>
/// <para>
/// A link is sent where the provider takes one, because that is one fewer copy of a member's
/// picture on Modbot's server. Where it will not, the bytes are fetched here, and that fetch is the
/// dangerous one: an attachment link is text somebody else wrote, so it goes out through a client
/// that refuses every private and local address, on the address the name actually resolves to
/// (foundation, <see cref="PublicAddresses"/>).
/// </para>
/// </remarks>
public sealed class ModerationPictures
{
    /// <summary>The named client used to fetch picture bytes. Refuses private addresses.</summary>
    public const string HttpClientName = "Modbot.AI.Pictures";

    /// <summary>How many pictures one check may send.</summary>
    public const int MostPictures = 4;

    /// <summary>How big one picture may be before it is skipped.</summary>
    public const int MostBytes = 4 * 1024 * 1024;

    /// <summary>The picture types Modbot sends. Anything else is skipped.</summary>
    public static IReadOnlyList<string> Types => PictureAttachments.Types;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _http;
    private readonly ILogger _log;

    public ModerationPictures(IHttpClientFactory http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);
    }

    /// <summary>
    /// Whether this provider takes a picture as a link. Anthropic's compatibility layer and a
    /// local server take the bytes; the hosted OpenAI-shaped endpoints take either.
    /// </summary>
    public static bool SendsLinks(string? provider)
        => provider is not null
           && (provider.Equals(AiProviders.OpenRouter.Id, StringComparison.OrdinalIgnoreCase)
               || provider.Equals(AiProviders.OpenAI.Id, StringComparison.OrdinalIgnoreCase)
               || provider.Equals(AiProviders.XAi.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every picture worth sending, capped and checked, ready for the model.
    /// </summary>
    /// <param name="sendLinks">
    /// The provider takes links, so nothing is downloaded. False fetches the bytes here.
    /// </param>
    public async Task<IReadOnlyList<PictureToCheck>> ReadyAsync(
        IEnumerable<PictureSource> sources, bool sendLinks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var ready = new List<PictureToCheck>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            if (ready.Count >= MostPictures)
                break;

            if (!Allowed(source.Url) || !seen.Add(source.Url))
                continue;

            var key = $"p{ready.Count + 1}";

            if (sendLinks)
            {
                ready.Add(new PictureToCheck(key, source.Label, source.Url, null, null));
                continue;
            }

            if (await FetchAsync(source.Url, ct).ConfigureAwait(false) is { } fetched)
                ready.Add(new PictureToCheck(key, source.Label, source.Url, fetched.Bytes, fetched.MediaType));
        }

        return ready;
    }

    /// <summary>
    /// Whether Modbot will look at this link at all: https, not too long, and not a name or
    /// literal in a private or local range.
    /// </summary>
    public static bool Allowed(string? url)
        => url is { Length: > 0 and <= 2000 }
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && !PublicAddresses.IsBlockedHost(uri.Host);

    private async Task<(BinaryData Bytes, string MediaType)?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient(HttpClientName);
            client.Timeout = FetchTimeout;

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var type = Before(response.Content.Headers.ContentType?.MediaType ?? string.Empty, ';').Trim();
            if (!Types.Contains(type, StringComparer.OrdinalIgnoreCase))
                return null;

            if (response.Content.Headers.ContentLength is > MostBytes)
                return null;

            // Read at most one byte more than the cap, so a server that lies about the length or
            // gives none is still stopped at the cap rather than filling memory.
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                buffer.Write(chunk, 0, read);
                if (buffer.Length > MostBytes)
                    return null;
            }

            return buffer.Length == 0 ? null : (BinaryData.FromBytes(buffer.ToArray()), type.ToLowerInvariant());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            _log.Debug("Could not read a picture for a moderation check: {Error}", e.Message);
            return null;
        }
    }

    private static string Before(string text, char at)
    {
        var index = text.IndexOf(at);
        return index < 0 ? text : text[..index];
    }
}

/// <summary>Registers the picture client, which refuses private and local addresses.</summary>
public static class ModerationPictureClient
{
    public static IServiceCollection AddModerationPictureClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(ModerationPictures.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A picture link is text somebody else wrote. Every redirect would be another
                // address to check, so none is followed.
                AllowAutoRedirect = false,
                ConnectCallback = (context, ct) => PublicAddresses.ConnectAsync(
                    context,
                    host => new HttpRequestException($"{host} is a private address."),
                    ct),
            });

        return services;
    }
}
