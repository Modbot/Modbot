using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Net;
using Modbot.Core.Time;

namespace Modbot.Core.Bluesky;

/// <summary>A signed-in session's tokens. Secret: never logged, never returned by the API.</summary>
/// <param name="AccessJwt">Sent with each call. Lasts about two hours.</param>
/// <param name="RefreshJwt">Swapped for new tokens. The old one stops working when it is used.</param>
/// <param name="Did">The account the session is for.</param>
/// <param name="Handle">The account's handle, as the server said it.</param>
/// <param name="OAuth">
/// For a sign-in with Bluesky (OAuth): the sign-in server, the DPoP key and when the access token
/// ends. Null for a session made with the app password.
/// </param>
public sealed record BlueskyTokens(string AccessJwt, string RefreshJwt, string Did, string? Handle, BlueskyOAuthGrant? OAuth = null)
{
    // The tokens are secrets; keep them out of any ToString.
    public override string ToString() => $"BlueskyTokens({Did})";
}

/// <summary>Where a record is: its <c>at://</c> address and its content id.</summary>
public sealed record BlueskyRecordRef(string Uri, string Cid);

/// <summary>What Check reads of the account's profile.</summary>
/// <param name="DisplayName">The display name, or null for none.</param>
/// <param name="Automated">The profile carries the <c>bot</c> self-label: the account says it is automated.</param>
public sealed record BlueskyProfile(string? DisplayName, bool Automated);

/// <summary>
/// The calls Modbot makes to a Bluesky account's own server, with plain <see cref="HttpClient"/> and
/// System.Text.Json (Bluesky design §3.8): no Bluesky package. One per process: a singleton.
/// </summary>
/// <remarks>
/// <para>
/// Every call goes to the server the account's DID document named (<see cref="BlueskyIdentity"/>), on
/// the named client <see cref="HttpClientName"/>, which is the guarded handler
/// (<see cref="PictureLinks.GuardedHandler"/>): the server is chosen by whoever controls the account.
/// </para>
/// <para>
/// A sign-in with Bluesky (OAuth) sends its token as <c>DPoP</c> with a proof made with the sign-in's
/// own key (<see cref="BlueskyAccess.DpopKey"/>). A server that asks for a nonce gets the same call
/// once more with it; that is the protocol, not a retry.
/// </para>
/// <para>
/// Nothing here retries. A rate limit comes back as a <see cref="BlueskyFailure"/> that
/// <see cref="BlueskyFailure.StopsTheLane"/>, and the caller stops until it resets.
/// </para>
/// </remarks>
public sealed class BlueskyClient(IHttpClientFactory http, IModbotClock clock)
{
    private readonly BlueskyNonces _nonces = new();

    /// <summary>The name of the <see cref="HttpClient"/> every Bluesky call is made on.</summary>
    public const string HttpClientName = "bluesky";

    /// <summary>A post's collection.</summary>
    public const string PostCollection = "app.bsky.feed.post";

    /// <summary>The profile's collection; the profile record's key is always <c>self</c>.</summary>
    public const string ProfileCollection = "app.bsky.actor.profile";

    /// <summary><c>createSession</c>: signs in with the app password. The only call that counts against Bluesky's sign-in limits.</summary>
    public Task<BlueskyResult<BlueskyTokens>> CreateSessionAsync(Uri server, string identifier, string appPassword, CancellationToken ct) =>
        TokensAsync(server, "com.atproto.server.createSession", null,
            new JsonObject { ["identifier"] = identifier, ["password"] = appPassword }, signingIn: true, ct);

    /// <summary><c>refreshSession</c>: swaps the refresh token for new tokens. The old refresh token stops working.</summary>
    public Task<BlueskyResult<BlueskyTokens>> RefreshSessionAsync(Uri server, string refreshJwt, CancellationToken ct) =>
        TokensAsync(server, "com.atproto.server.refreshSession", refreshJwt, null, signingIn: false, ct);

    /// <summary><c>getSession</c>: whether the access token still works, and for which account.</summary>
    public async Task<BlueskyResult<string>> GetSessionAsync(BlueskyAccess access, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var answer = await SendAsync(HttpMethod.Get, access.Server, "com.atproto.server.getSession", access, null, signingIn: false, ct).ConfigureAwait(false);

        return answer.Value is { } body && body["did"] is JsonValue value && value.TryGetValue<string>(out var did) && did.Length > 0
            ? BlueskyResult<string>.Ok(did)
            : BlueskyResult<string>.Failed(answer.Failure ?? new BlueskyFailure(BlueskyProblem.Other, 200));
    }

    /// <summary>
    /// The account's profile record (<c>app.bsky.actor.profile/self</c>), read from its own server with
    /// no sign-in: its display name, and whether it carries the <c>bot</c> self-label. An account with
    /// no profile record has neither.
    /// </summary>
    public async Task<BlueskyResult<BlueskyProfile>> ProfileAsync(Uri server, string did, CancellationToken ct)
    {
        var record = await GetRecordAsync(server, did, ProfileCollection, "self", ct).ConfigureAwait(false);

        if (record.Failure is { Problem: BlueskyProblem.RecordNotFound })
            return BlueskyResult<BlueskyProfile>.Ok(new BlueskyProfile(null, false));

        if (record.Failure is { } failure)
            return BlueskyResult<BlueskyProfile>.Failed(failure);

        return BlueskyResult<BlueskyProfile>.Ok(ProfileOf(record.Value.Value));
    }

    /// <summary>Reads a profile record's display name and self-labels.</summary>
    public static BlueskyProfile ProfileOf(JsonObject? value)
    {
        var name = value?["displayName"] is JsonValue shown && shown.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

        var automated = value?["labels"]?["values"] is JsonArray labels
            && labels.Any(l => l?["val"] is JsonValue val && val.TryGetValue<string>(out var word) && word == "bot");

        return new BlueskyProfile(name, automated);
    }

    /// <summary>
    /// <c>uploadBlob</c>: the card picture. The answer is the blob reference the post's card carries.
    /// The same bytes give the same reference, so sending it again is safe.
    /// </summary>
    public async Task<BlueskyResult<JsonObject>> UploadBlobAsync(BlueskyAccess access, byte[] bytes, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(bytes);

        HttpContent Content()
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }

        var answer = await SendAsync(HttpMethod.Post, access.Server, "com.atproto.repo.uploadBlob", access, Content, signingIn: false, ct).ConfigureAwait(false);

        return answer.Value?["blob"] is JsonObject blob
            ? BlueskyResult<JsonObject>.Ok((JsonObject)blob.DeepClone())
            : BlueskyResult<JsonObject>.Failed(answer.Failure ?? new BlueskyFailure(BlueskyProblem.Other, 200));
    }

    /// <summary>
    /// <c>putRecord</c> at a key Modbot picked, with <c>swapRecord: null</c>: made only if nothing is
    /// at that key (Bluesky design §3.4, fact 20). Something there answers <see cref="BlueskyProblem.InvalidSwap"/>;
    /// the very same record answers as done.
    /// </summary>
    public async Task<BlueskyResult<BlueskyRecordRef>> PutRecordAsync(
        BlueskyAccess access, string repo, string collection, string rkey, JsonObject record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var body = new JsonObject
        {
            ["repo"] = repo,
            ["collection"] = collection,
            ["rkey"] = rkey,
            ["record"] = record.DeepClone(),
            // Null, sent as null: "only if nothing is at this key". Left out, it would replace.
            ["swapRecord"] = null,
        };

        var answer = await SendAsync(HttpMethod.Post, access.Server, "com.atproto.repo.putRecord", access, () => Json(body), signingIn: false, ct).ConfigureAwait(false);
        return RefOf(answer);
    }

    /// <summary>
    /// <c>getRecord</c>, with no sign-in: the record at a key, or <see cref="BlueskyProblem.RecordNotFound"/>.
    /// </summary>
    public async Task<BlueskyResult<(BlueskyRecordRef Ref, JsonObject? Value)>> GetRecordAsync(
        Uri server, string repo, string collection, string rkey, CancellationToken ct)
    {
        var path = "com.atproto.repo.getRecord?repo=" + Uri.EscapeDataString(repo)
            + "&collection=" + Uri.EscapeDataString(collection)
            + "&rkey=" + Uri.EscapeDataString(rkey);

        var answer = await SendAsync(HttpMethod.Get, server, path, null, null, signingIn: false, ct).ConfigureAwait(false);

        if (answer.Value is not { } body)
            return BlueskyResult<(BlueskyRecordRef, JsonObject?)>.Failed(answer.Failure!);

        var uri = body["uri"] is JsonValue u && u.TryGetValue<string>(out var uriText) ? uriText : null;
        var cid = body["cid"] is JsonValue c && c.TryGetValue<string>(out var cidText) ? cidText : null;

        return uri is null
            ? BlueskyResult<(BlueskyRecordRef, JsonObject?)>.Failed(new BlueskyFailure(BlueskyProblem.Other, 200))
            : BlueskyResult<(BlueskyRecordRef, JsonObject?)>.Ok((new BlueskyRecordRef(uri, cid ?? string.Empty), body["value"] as JsonObject));
    }

    /// <summary><c>deleteRecord</c>. A record already gone is the caller's to count as deleted.</summary>
    public async Task<BlueskyResult<bool>> DeleteRecordAsync(BlueskyAccess access, string repo, string collection, string rkey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var body = new JsonObject { ["repo"] = repo, ["collection"] = collection, ["rkey"] = rkey };
        var answer = await SendAsync(HttpMethod.Post, access.Server, "com.atproto.repo.deleteRecord", access, () => Json(body), signingIn: false, ct).ConfigureAwait(false);

        return answer.Value is not null ? BlueskyResult<bool>.Ok(true) : BlueskyResult<bool>.Failed(answer.Failure!);
    }

    private async Task<BlueskyResult<BlueskyTokens>> TokensAsync(
        Uri server, string method, string? bearer, JsonObject? body, bool signingIn, CancellationToken ct)
    {
        var answer = await SendAsync(
            HttpMethod.Post,
            server,
            method,
            bearer is null ? null : new BlueskyAccess(string.Empty, server, bearer),
            body is null ? null : () => Json(body),
            signingIn,
            ct).ConfigureAwait(false);

        if (answer.Value is not { } value)
            return BlueskyResult<BlueskyTokens>.Failed(answer.Failure!);

        var access = value["accessJwt"] is JsonValue a && a.TryGetValue<string>(out var accessText) ? accessText : null;
        var refresh = value["refreshJwt"] is JsonValue r && r.TryGetValue<string>(out var refreshText) ? refreshText : null;
        var did = value["did"] is JsonValue d && d.TryGetValue<string>(out var didText) ? didText : null;
        var handle = value["handle"] is JsonValue h && h.TryGetValue<string>(out var handleText) ? handleText : null;

        return string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(did)
            ? BlueskyResult<BlueskyTokens>.Failed(new BlueskyFailure(BlueskyProblem.Other, 200))
            : BlueskyResult<BlueskyTokens>.Ok(new BlueskyTokens(access, refresh, did, handle));
    }

    private static BlueskyResult<BlueskyRecordRef> RefOf(BlueskyResult<JsonObject> answer)
    {
        if (answer.Value is not { } body)
            return BlueskyResult<BlueskyRecordRef>.Failed(answer.Failure!);

        var uri = body["uri"] is JsonValue u && u.TryGetValue<string>(out var uriText) ? uriText : null;
        var cid = body["cid"] is JsonValue c && c.TryGetValue<string>(out var cidText) ? cidText : null;

        return uri is null
            ? BlueskyResult<BlueskyRecordRef>.Failed(new BlueskyFailure(BlueskyProblem.Unavailable, 200, Message: "Bluesky's answer had no address."))
            : BlueskyResult<BlueskyRecordRef>.Ok(new BlueskyRecordRef(uri, cid ?? string.Empty));
    }

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>
    /// One XRPC call. With <paramref name="access"/>, its token goes as <c>Bearer</c>, or for a sign-in
    /// with Bluesky as <c>DPoP</c> with a proof. A server asking for a DPoP nonce gets the call once
    /// more with the nonce it gave, which is why the body is made by <paramref name="content"/>.
    /// </summary>
    private async Task<BlueskyResult<JsonObject>> SendAsync(
        HttpMethod method, Uri server, string path, BlueskyAccess? access, Func<HttpContent>? content, bool signingIn, CancellationToken ct)
    {
        var address = new Uri(server, "xrpc/" + path);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, address);
            string? nonce = null;

            if (access is { DpopKey: { } dpopKey })
            {
                nonce = _nonces.For(address);
                request.Headers.Authorization = new AuthenticationHeaderValue(BlueskyDpop.Header, access.AccessJwt);
                request.Headers.Add(BlueskyDpop.Header, BlueskyDpop.Proof(dpopKey, method, address, nonce, access.AccessJwt, clock.UtcNow));
            }
            else if (access is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessJwt);
            }

            request.Content = content?.Invoke();

            try
            {
                using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (access?.DpopKey is not null)
                {
                    var given = _nonces.Keep(address, response);

                    if (!response.IsSuccessStatusCode && attempt == 0 && given is not null && given != nonce
                        && BlueskyDpop.AsksForNonce(response, text))
                    {
                        continue;
                    }
                }

                if (!response.IsSuccessStatusCode)
                    return BlueskyResult<JsonObject>.Failed(BlueskyErrors.FromXrpc(response.StatusCode, text, response.Headers, signingIn));

                if (string.IsNullOrWhiteSpace(text))
                    return BlueskyResult<JsonObject>.Ok(new JsonObject());

                return JsonNode.Parse(text) is JsonObject value
                    ? BlueskyResult<JsonObject>.Ok(value)
                    : BlueskyResult<JsonObject>.Failed(new BlueskyFailure(BlueskyProblem.Other, (int)response.StatusCode));
            }
            catch (HttpRequestException)
            {
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
            }
            catch (JsonException)
            {
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
            }
        }
    }
}

/// <summary>Registers the Bluesky client, the account lookup and the session owner.</summary>
public static class BlueskyServices
{
    /// <summary>
    /// The named client is the guarded handler (<see cref="PictureLinks.GuardedHandler"/>): public
    /// addresses only, no redirects, no proxy, no cookies, because the account's owner chooses where
    /// it points. Answers are capped at 1 MB.
    /// </summary>
    public static IServiceCollection AddBluesky(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(BlueskyClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(PictureLinks.GuardedHandler);

        services.TryAddSingleton<BlueskyClient>();
        services.TryAddSingleton<BlueskyIdentity>();
        services.TryAddSingleton<BlueskyOAuth>();
        services.TryAddSingleton<BlueskySession>();

        return services;
    }
}
