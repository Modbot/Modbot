using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Mcp;

/// <summary>What a client's published metadata document said, once checked.</summary>
public sealed record McpClientDocument(string ClientId, string Name, IReadOnlyList<string> RedirectUris, string? ClientUri);

/// <summary>
/// Clients that identify themselves by a URL rather than by registering: the MCP authorization
/// specification's client ID metadata documents, which Claude.ai uses by default.
/// </summary>
/// <remarks>
/// <para>The <c>client_id</c> is an HTTPS address, and the document at that address says who the
/// client is and where it may be sent back to. Modbot fetches it the first time the address is
/// seen, keeps what it said as an <see cref="McpClient"/> row with the address on it, and
/// fetches again once an hour so a changed redirect address is picked up. A fetch that fails
/// falls back to what was kept, and a client never seen before whose document cannot be read is
/// unknown.</para>
/// <para>What is sent: one GET to the client's address, with nothing attached. Only HTTPS
/// addresses are fetched, and only up to 64 KB is read.</para>
/// </remarks>
public static class McpClientDocuments
{
    public const string HttpClientName = "Modbot.Mcp.ClientDocuments";

    public static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    public const int MaxDocumentBytes = 64 * 1024;

    /// <summary>The client behind a <c>client_id</c>: a registered one by its id, or a published one by its address.</summary>
    public static async Task<McpClient?> ResolveAsync(
        string? clientId,
        ModbotContext db,
        IHttpClientFactory http,
        IModbotClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(clientId))
            return null;

        if (Guid.TryParse(clientId, out var id))
            return await db.McpClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

        if (!IsDocumentAddress(clientId, out var address))
            return null;

        var key = address.AbsoluteUri;
        var known = await db.McpClients.FirstOrDefaultAsync(c => c.MetadataUrl == key, ct);
        var now = clock.UtcNow;

        if (known is not null && known.MetadataFetchedAt is { } fetched && now - fetched < Fresh)
            return known;

        var document = await FetchAsync(address, http, ct);
        if (document is null)
            return known;

        if (known is null)
        {
            known = new McpClient { MetadataUrl = key, CreatedAt = now };
            db.McpClients.Add(known);
        }

        known.Name = document.Name;
        known.RedirectUris = JsonSerializer.Serialize(document.RedirectUris);
        known.ClientUri = document.ClientUri;
        known.SecretHash = null;
        known.MetadataFetchedAt = now;
        await db.SaveChangesAsync(ct);

        return known;
    }

    /// <summary>
    /// An HTTPS address on the usual port with a host name and no fragment or user part, as the
    /// specification asks. A bare IP address is not a client: a published identity has a name.
    /// </summary>
    public static bool IsDocumentAddress(string clientId, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttps || parsed.HostNameType != UriHostNameType.Dns
            || parsed.Host.Length == 0 || !parsed.IsDefaultPort || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0)
            return false;

        address = parsed;
        return true;
    }

    /// <summary>
    /// The handler the fetch goes through: no redirects, and each connection made only to an
    /// address of the name that is public at the moment of connecting
    /// (<see cref="PublicAddresses"/>), so neither a redirect nor a name that resolves
    /// differently the second time can send the fetch inside this server's network.
    /// </summary>
    public static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await PublicAddresses.ResolvePublicAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0)
                throw new HttpRequestException($"'{context.DnsEndPoint.Host}' is not a public address.");

            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static async Task<McpClientDocument?> FetchAsync(Uri address, IHttpClientFactory http, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is > MaxDocumentBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxDocumentBytes)
                return null;

            return Parse(address.AbsoluteUri, buffer.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a document, or answers null when it does not describe the client at that address:
    /// its <c>client_id</c> must be the address itself, it must name at least one redirect
    /// address that passes the same checks as a registration, and it may not ask for a secret.
    /// </summary>
    public static McpClientDocument? Parse(string address, ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("client_id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || !string.Equals(idElement.GetString(), address, StringComparison.Ordinal))
                return null;

            if (!root.TryGetProperty("redirect_uris", out var urisElement) || urisElement.ValueKind != JsonValueKind.Array)
                return null;

            var uris = new List<string>();
            foreach (var uri in urisElement.EnumerateArray())
            {
                if (uri.ValueKind != JsonValueKind.String || uri.GetString() is not { Length: > 0 } text)
                    return null;

                if (McpOAuthEndpoints.RedirectUriProblem(text) is not null)
                    return null;

                uris.Add(text);
            }

            if (uris.Count == 0)
                return null;

            if (root.TryGetProperty("token_endpoint_auth_method", out var method)
                && method.ValueKind == JsonValueKind.String
                && method.GetString() != "none")
                return null;

            var name = root.TryGetProperty("client_name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()!.Trim()
                : string.Empty;
            if (name.Length == 0)
                name = new Uri(address).Host;
            if (name.Length > McpOAuthEndpoints.MaxClientNameLength)
                name = name[..McpOAuthEndpoints.MaxClientNameLength];

            var clientUri = root.TryGetProperty("client_uri", out var siteElement) && siteElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(siteElement.GetString(), UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps
                ? site.AbsoluteUri
                : null;

            return new McpClientDocument(address, name, uris, clientUri);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
