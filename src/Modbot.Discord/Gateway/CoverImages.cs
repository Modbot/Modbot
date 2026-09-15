using System.Net.Http;
using System.Net.Http.Headers;
using Modbot.Core.Net;

namespace Modbot.Discord.Gateway;

/// <summary>
/// Fetches a calendar event's picture link for a Discord event cover (calendar design §3.2).
/// </summary>
/// <remarks>
/// <para>
/// Discord wants the picture's bytes, not its address, so Modbot has to fetch what a moderator
/// typed. That is the same risk as a webhook address, and it gets the same guard: https only, and
/// only to public addresses, checked on the address the name resolved to when connecting
/// (<see cref="PublicAddresses"/>).
/// </para>
/// <para>
/// A cover that cannot be fetched is left off rather than failing the event: the event matters, the
/// picture does not.
/// </para>
/// </remarks>
internal static class CoverImages
{
    /// <summary>Discord's own limit for an event cover is 10 MB; less is plenty for a picture.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectCallback = (context, ct) =>
            PublicAddresses.ConnectAsync(context, host => new HttpRequestException($"{host} is a private address."), ct),
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static async Task<Cover?> FetchAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps
            || PublicAddresses.IsBlockedHost(address.Host))
        {
            return null;
        }

        try
        {
            using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType is not MediaTypeHeaderValue { MediaType: { } type }
                || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;

            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (copy.Length + read > MaxBytes)
                {
                    await copy.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                copy.Write(buffer, 0, read);
            }

            copy.Position = 0;
            return new Cover(copy);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The fetched picture, kept open until Discord has been sent it.</summary>
    public sealed class Cover(MemoryStream bytes) : IDisposable
    {
        public global::Discord.Image Image { get; } = new(bytes);

        public void Dispose() => bytes.Dispose();
    }
}
