using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modbot.Core.Net;

namespace Modbot.Core.Bluesky;

/// <summary>A Bluesky account as Check found it: its lasting id, its handle, and its own server.</summary>
/// <param name="Did">The account's DID, <c>did:plc:…</c> or <c>did:web:…</c>.</param>
/// <param name="Handle">The handle, checked both ways: it names the DID, and the DID's document names it back.</param>
/// <param name="Server">The account's own server (its PDS): https, no path.</param>
public sealed record BlueskyAccount(string Did, string Handle, Uri Server);

/// <summary>
/// Finds a Bluesky account from its handle: the handle to its DID, the DID to its document, and the
/// document to the account's own server (Bluesky design §3.1, facts 22 and 23).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every address here is chosen by whoever controls the account</strong>: the handle's own
/// website, a did:web host, and the server the document names. So every call goes through the
/// guarded client (<see cref="BlueskyClient.HttpClientName"/>, on
/// <see cref="PictureLinks.GuardedHandler"/>): public addresses only, no redirects, no proxy, no
/// cookies, and small answers.
/// </para>
/// <para>
/// The handle is checked both ways, as the handle spec asks: it resolves to the DID, and the DID's
/// document lists <c>at://handle</c> among its names. A handle that does either alone is not taken.
/// </para>
/// </remarks>
public sealed partial class BlueskyIdentity(IHttpClientFactory http)
{
    /// <summary>Bluesky's public, cached API, which looks a handle up (DNS included) for anyone.</summary>
    public const string PublicApi = "https://public.api.bsky.app/";

    /// <summary>Where did:plc documents are read.</summary>
    public const string PlcDirectory = "https://plc.directory/";

    /// <summary>The longest handle the handle spec allows.</summary>
    public const int MaxHandleLength = 253;

    /// <summary>Endings the handle spec sets aside; no real account has one.</summary>
    private static readonly string[] ReservedEndings =
        [".local", ".arpa", ".invalid", ".localhost", ".internal", ".example", ".onion", ".alt", ".test"];

    /// <summary>
    /// A handle as Modbot keeps it: trimmed, a leading <c>@</c> dropped, lower case. Null when what
    /// is left is not a handle by the handle spec's rules.
    /// </summary>
    public static string? NormaliseHandle(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;

        var handle = typed.Trim();
        if (handle.StartsWith('@'))
            handle = handle[1..];

        handle = handle.ToLowerInvariant();

        if (handle.Length > MaxHandleLength || !HandleShape().IsMatch(handle))
            return null;

        foreach (var ending in ReservedEndings)
        {
            if (handle.EndsWith(ending, StringComparison.Ordinal))
                return null;
        }

        return handle;
    }

    /// <summary>
    /// The account behind <paramref name="handle"/>, or why there is none: <see cref="BlueskyProblem.NotFound"/>
    /// when the handle does not lead to an account that names it back, <see cref="BlueskyProblem.Unavailable"/>
    /// when a place on the way did not answer.
    /// </summary>
    public async Task<BlueskyResult<BlueskyAccount>> FindAsync(string handle, CancellationToken ct)
    {
        if (NormaliseHandle(handle) is not { } clean)
            return BlueskyResult<BlueskyAccount>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0));

        var did = await DidOfAsync(clean, ct).ConfigureAwait(false);
        if (did.Value is not { } found)
            return BlueskyResult<BlueskyAccount>.Failed(did.Failure!);

        var document = await DocumentAsync(found, ct).ConfigureAwait(false);
        if (document.Value is not { } text)
            return BlueskyResult<BlueskyAccount>.Failed(document.Failure!);

        var read = ReadDocument(text, found);
        if (read is null)
            return BlueskyResult<BlueskyAccount>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0, Message: "The account's document names no server."));

        if (!read.Handles.Contains(clean, StringComparer.Ordinal))
            return BlueskyResult<BlueskyAccount>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0, Message: "The account does not name this handle back."));

        return BlueskyResult<BlueskyAccount>.Ok(new BlueskyAccount(found, clean, read.Server));
    }

    /// <summary>
    /// The account's own server for <paramref name="did"/>, read from its document: for a post sent
    /// from an account that is no longer the one in Settings, which is still read back and deleted
    /// where it went.
    /// </summary>
    public async Task<BlueskyResult<Uri>> ServerOfAsync(string did, CancellationToken ct)
    {
        var document = await DocumentAsync(did, ct).ConfigureAwait(false);
        if (document.Value is not { } text)
            return BlueskyResult<Uri>.Failed(document.Failure!);

        return ReadDocument(text, did) is { } read
            ? BlueskyResult<Uri>.Ok(read.Server)
            : BlueskyResult<Uri>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0, Message: "The account's document names no server."));
    }

    /// <summary>What Modbot takes from a DID document: the names it lists, and the account's server.</summary>
    /// <param name="Handles">Every <c>at://</c> name in <c>alsoKnownAs</c>, lower case.</param>
    public sealed record DidDocument(IReadOnlyList<string> Handles, Uri Server);

    /// <summary>
    /// Reads a DID document (atproto.com/specs/did): its <c>id</c> must be <paramref name="did"/>,
    /// and the server is the service <c>#atproto_pds</c> of type <c>AtprotoPersonalDataServer</c>,
    /// whose address must be https with no path, query or user name. Null when any of that fails.
    /// </summary>
    public static DidDocument? ReadDocument(string json, string did)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), did, StringComparison.Ordinal))
            {
                return null;
            }

            var handles = new List<string>();
            if (root.TryGetProperty("alsoKnownAs", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in names.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.String
                        && name.GetString() is { } value
                        && value.StartsWith("at://", StringComparison.Ordinal))
                    {
                        handles.Add(value["at://".Length..].ToLowerInvariant());
                    }
                }
            }

            if (!root.TryGetProperty("service", out var services) || services.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var service in services.EnumerateArray())
            {
                if (service.ValueKind != JsonValueKind.Object)
                    continue;

                var serviceId = service.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
                var type = service.TryGetProperty("type", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                var endpoint = service.TryGetProperty("serviceEndpoint", out var se) && se.ValueKind == JsonValueKind.String ? se.GetString() : null;

                if (serviceId is not ("#atproto_pds") && serviceId != did + "#atproto_pds")
                    continue;

                if (type != "AtprotoPersonalDataServer")
                    return null;

                return ServerAddress(endpoint) is { } server ? new DidDocument(handles, server) : null;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An account server address Modbot will send to: https, a host that is not a private address,
    /// and no path, query, fragment or user name. Null otherwise.
    /// </summary>
    public static Uri? ServerAddress(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath is not ("/" or "")
            || PublicAddresses.IsBlockedHost(uri.Host))
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// <summary>
    /// The DID for a handle: Bluesky's public lookup first (it reads the handle's DNS record, which
    /// .NET cannot), then the handle's own <c>/.well-known/atproto-did</c>.
    /// </summary>
    private async Task<BlueskyResult<string>> DidOfAsync(string handle, CancellationToken ct)
    {
        var lookup = await GetTextAsync(
            new Uri(new Uri(PublicApi), "xrpc/com.atproto.identity.resolveHandle?handle=" + Uri.EscapeDataString(handle)),
            ct).ConfigureAwait(false);

        if (lookup.Value is { } json && DidFromLookup(json) is { } did)
            return BlueskyResult<string>.Ok(did);

        // A 429 from the lookup stops nothing here: the handle's own address is asked instead.
        if (PublicAddresses.IsBlockedHost(handle))
            return BlueskyResult<string>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0));

        var own = await GetTextAsync(new Uri($"https://{handle}/.well-known/atproto-did"), ct).ConfigureAwait(false);

        if (own.Value is { } text && text.Trim() is { } line && IsDid(line))
            return BlueskyResult<string>.Ok(line);

        // Neither answered: the handle may be fine and the places unreachable.
        return lookup.Failure is { Unclear: true } && own.Failure is { Unclear: true }
            ? BlueskyResult<string>.Failed(BlueskyErrors.NoAnswer())
            : BlueskyResult<string>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0));
    }

    private async Task<BlueskyResult<string>> DocumentAsync(string did, CancellationToken ct)
    {
        Uri address;

        if (did.StartsWith("did:plc:", StringComparison.Ordinal) && did.Length > "did:plc:".Length && PlcId().IsMatch(did["did:plc:".Length..]))
        {
            address = new Uri(new Uri(PlcDirectory), did);
        }
        else if (did.StartsWith("did:web:", StringComparison.Ordinal))
        {
            // atproto takes did:web for a whole host only: no path parts (colons) and no port.
            var host = Uri.UnescapeDataString(did["did:web:".Length..]);
            if (host.Contains(':', StringComparison.Ordinal) || NormaliseHandle(host) != host.ToLowerInvariant() || PublicAddresses.IsBlockedHost(host))
                return BlueskyResult<string>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0));

            address = new Uri($"https://{host}/.well-known/did.json");
        }
        else
        {
            return BlueskyResult<string>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0));
        }

        var answer = await GetTextAsync(address, ct).ConfigureAwait(false);

        return answer.Failure is { Unclear: false }
            ? BlueskyResult<string>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, answer.Failure.Status))
            : answer;
    }

    private async Task<BlueskyResult<string>> GetTextAsync(Uri address, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient(BlueskyClient.HttpClientName).GetAsync(address, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return BlueskyResult<string>.Failed(BlueskyErrors.FromXrpc(response.StatusCode, text, response.Headers));

            return BlueskyResult<string>.Ok(text);
        }
        catch (HttpRequestException)
        {
            return BlueskyResult<string>.Failed(BlueskyErrors.NoAnswer());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return BlueskyResult<string>.Failed(BlueskyErrors.NoAnswer());
        }
    }

    private static string? DidFromLookup(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("did", out var did)
                && did.ValueKind == JsonValueKind.String
                && IsDid(did.GetString())
                    ? did.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A DID of the two kinds atproto uses, in its plain form.</summary>
    public static bool IsDid(string? value) =>
        value is { Length: > 8 and <= 2048 }
        && (value.StartsWith("did:plc:", StringComparison.Ordinal) || value.StartsWith("did:web:", StringComparison.Ordinal))
        && !value.Any(char.IsWhiteSpace);

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HandleShape();

    // did:plc ids are 24 characters of base32 today; only the alphabet is held to, so the id stays
    // one path part of plc.directory's address.
    [GeneratedRegex(@"^[a-z2-7]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlcId();
}
