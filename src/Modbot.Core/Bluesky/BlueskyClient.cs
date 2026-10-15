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

/// <summary>Somebody else's post that answers, names or quotes the account.</summary>
/// <param name="Reason"><c>reply</c>, <c>mention</c> or <c>quote</c>.</param>
/// <param name="Uri">The author's post, as an <c>at://</c> address.</param>
/// <param name="AuthorDid">Who wrote it.</param>
/// <param name="AuthorHandle">Their handle; <c>handle.invalid</c> when Bluesky could not confirm it.</param>
/// <param name="AuthorName">Their display name, or null for none.</param>
/// <param name="Text">What the post says.</param>
/// <param name="Answered">For a reply, the address of the post it answers, when Bluesky said.</param>
/// <param name="IndexedAt">When Bluesky took the post in; the order of the list and where reading stopped.</param>
public sealed record BlueskyMention(
    string Reason, string Uri, string AuthorDid, string AuthorHandle, string? AuthorName, string Text, string? Answered, DateTimeOffset IndexedAt);

/// <summary>One page of <see cref="BlueskyClient.MentionsAsync"/>, newest first.</summary>
/// <param name="Cursor">Where the next page starts, or null when there is none.</param>
public sealed record BlueskyMentionsPage(IReadOnlyList<BlueskyMention> Mentions, string? Cursor);

/// <summary>The numbers Bluesky shows under one post.</summary>
/// <param name="Uri">The post's <c>at://</c> address.</param>
public sealed record BlueskyPostCounts(string Uri, int Likes, int Reposts, int Replies);

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
    /// <c>uploadBlob</c>: the picture. The answer is the blob reference the post's picture carries.
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
    public Task<BlueskyResult<BlueskyRecordRef>> PutRecordAsync(
        BlueskyAccess access, string repo, string collection, string rkey, JsonObject record, CancellationToken ct) =>
        ReplaceRecordAsync(access, repo, collection, rkey, record, swapCid: null, ct);

    /// <summary>
    /// <c>putRecord</c> that replaces the record read with content id <paramref name="swapCid"/>: made
    /// only if that is still what is at the key, so a change made meanwhile is not written over
    /// (<see cref="BlueskyProblem.InvalidSwap"/>). A null <paramref name="swapCid"/> sends
    /// <c>swapRecord: null</c>, which makes the record only if nothing is there. The profile pin uses it.
    /// </summary>
    public async Task<BlueskyResult<BlueskyRecordRef>> ReplaceRecordAsync(
        BlueskyAccess access, string repo, string collection, string rkey, JsonObject record, string? swapCid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var body = new JsonObject
        {
            ["repo"] = repo,
            ["collection"] = collection,
            ["rkey"] = rkey,
            ["record"] = record.DeepClone(),
            // Null, sent as null: "only if nothing is at this key". Left out, it would replace.
            ["swapRecord"] = swapCid,
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

    /// <summary>
    /// Bluesky's public API, which answers the post numbers with no sign-in. Not the account's own
    /// server: <see cref="BlueskyIdentity"/> is for that.
    /// </summary>
    public static readonly Uri PublicApi = new("https://public.api.bsky.app/");

    /// <summary>
    /// The most posts one <see cref="PostCountsAsync"/> call asks for: <c>app.bsky.feed.getPosts</c>
    /// takes 25 addresses at most (<c>uris</c> has <c>maxLength: 25</c> in the lexicon,
    /// github.com/bluesky-social/atproto, lexicons/app/bsky/feed/getPosts.json).
    /// </summary>
    public const int MostCountedAtOnce = 25;

    /// <summary>
    /// <c>app.bsky.feed.getPosts</c> on the public API, with no sign-in: the numbers under each post
    /// asked for. A post that is gone is left out of the answer. The numbers are
    /// <c>likeCount</c>, <c>repostCount</c> and <c>replyCount</c> of <c>app.bsky.feed.defs#postView</c>
    /// (github.com/bluesky-social/atproto, lexicons/app/bsky/feed/defs.json).
    /// </summary>
    public async Task<BlueskyResult<IReadOnlyList<BlueskyPostCounts>>> PostCountsAsync(IReadOnlyList<string> uris, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uris);

        if (uris.Count == 0)
            return BlueskyResult<IReadOnlyList<BlueskyPostCounts>>.Ok([]);

        if (uris.Count > MostCountedAtOnce)
            throw new ArgumentException($"At most {MostCountedAtOnce} posts at once.", nameof(uris));

        var path = "app.bsky.feed.getPosts?" + string.Join('&', uris.Select(u => "uris=" + Uri.EscapeDataString(u)));
        var answer = await SendAsync(HttpMethod.Get, PublicApi, path, null, null, signingIn: false, ct).ConfigureAwait(false);

        return answer.Value is { } body
            ? BlueskyResult<IReadOnlyList<BlueskyPostCounts>>.Ok(CountsOf(body))
            : BlueskyResult<IReadOnlyList<BlueskyPostCounts>>.Failed(answer.Failure!);
    }

    /// <summary>
    /// <c>app.bsky.actor.getProfile</c> on the public API, with no sign-in: the profile as Bluesky shows
    /// it, with the numbers of followers, follows and posts. <paramref name="actor"/> is a DID or a handle.
    /// The fields are those of <c>app.bsky.actor.defs#profileViewDetailed</c> (github.com/bluesky-social/atproto,
    /// lexicons/app/bsky/actor/defs.json).
    /// </summary>
    public async Task<BlueskyResult<BlueskyAccountProfile>> AccountProfileAsync(string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);

        var path = "app.bsky.actor.getProfile?actor=" + Uri.EscapeDataString(actor);
        var answer = await SendAsync(HttpMethod.Get, PublicApi, path, null, null, signingIn: false, ct).ConfigureAwait(false);

        if (answer.Value is not { } body)
            return BlueskyResult<BlueskyAccountProfile>.Failed(answer.Failure!);

        return AccountProfileOf(body) is { } profile
            ? BlueskyResult<BlueskyAccountProfile>.Ok(profile)
            : BlueskyResult<BlueskyAccountProfile>.Failed(new BlueskyFailure(BlueskyProblem.Other, 200));
    }

    /// <summary>Reads a <c>getProfile</c> answer. Null when it has no account; a missing number is null, a missing word is null.</summary>
    public static BlueskyAccountProfile? AccountProfileOf(JsonObject? body)
    {
        if (TextOf(body?["did"]) is not { Length: > 0 } did)
            return null;

        static string? Words(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        // A picture is shown by its address: only an https one is kept.
        static string? Address(string? text) =>
            Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

        static int? Number(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0 ? number : null;

        return new BlueskyAccountProfile(
            did,
            TextOf(body!["handle"]) ?? string.Empty,
            Words(TextOf(body["displayName"])),
            Words(TextOf(body["description"])),
            Address(TextOf(body["avatar"])),
            Address(TextOf(body["banner"])),
            Number(body["followersCount"]),
            Number(body["followsCount"]),
            Number(body["postsCount"]));
    }

    /// <summary>Reads the posts of a <c>getPosts</c> answer. A post without an address is skipped; a missing number is 0.</summary>
    public static IReadOnlyList<BlueskyPostCounts> CountsOf(JsonObject? body)
    {
        var counts = new List<BlueskyPostCounts>();

        if (body?["posts"] is not JsonArray posts)
            return counts;

        foreach (var post in posts)
        {
            if (post is not JsonObject view || view["uri"] is not JsonValue uri || !uri.TryGetValue<string>(out var text) || text.Length == 0)
                continue;

            counts.Add(new BlueskyPostCounts(text, CountOf(view["likeCount"]), CountOf(view["repostCount"]), CountOf(view["replyCount"])));
        }

        return counts;
    }

    private static int CountOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number > 0 ? number : 0;

    /// <summary>The reasons <see cref="MentionsAsync"/> asks for. Likes, reposts and follows are left out: too many to be useful.</summary>
    public static readonly IReadOnlyList<string> MentionReasons = ["reply", "mention", "quote"];

    /// <summary>The most one <see cref="MentionsAsync"/> page holds: <c>limit</c> is 1 to 100 in the lexicon.</summary>
    public const int MostMentionsAtOnce = 100;

    /// <summary>
    /// <c>app.bsky.notification.listNotifications</c>, the account's own, newest first: only the
    /// <see cref="MentionReasons"/>. The parameters are <c>limit</c> (1 to 100), <c>reasons</c> (a
    /// list), <c>cursor</c>; each notification has <c>uri</c>, <c>author</c> (a profile view with
    /// <c>did</c>, <c>handle</c>, <c>displayName</c>), <c>reason</c>, <c>reasonSubject</c>, <c>record</c> (the
    /// author's post, with <c>text</c>, and <c>reply.parent.uri</c> for a reply) and <c>indexedAt</c>
    /// (github.com/bluesky-social/atproto, lexicons/app/bsky/notification/listNotifications.json). The
    /// account's server passes it on to the AppView, which needs the <c>rpc</c> permission
    /// (<see cref="BlueskyOAuth.RepliesScope"/>) for a sign-in with Bluesky.
    /// </summary>
    public async Task<BlueskyResult<BlueskyMentionsPage>> MentionsAsync(BlueskyAccess access, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var path = "app.bsky.notification.listNotifications?limit=" + MostMentionsAtOnce
            + string.Concat(MentionReasons.Select(r => "&reasons=" + Uri.EscapeDataString(r)))
            + (string.IsNullOrEmpty(cursor) ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));

        var answer = await SendAsync(HttpMethod.Get, access.Server, path, access, null, signingIn: false, ct).ConfigureAwait(false);

        return answer.Value is { } body
            ? BlueskyResult<BlueskyMentionsPage>.Ok(MentionsOf(body))
            : BlueskyResult<BlueskyMentionsPage>.Failed(answer.Failure!);
    }

    /// <summary>
    /// Reads a <c>listNotifications</c> answer. A notification of another reason, or without an address,
    /// an author or a time, is skipped; a post without text reads as empty text.
    /// </summary>
    public static BlueskyMentionsPage MentionsOf(JsonObject? body)
    {
        var mentions = new List<BlueskyMention>();

        if (body?["notifications"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject n
                    || TextOf(n["reason"]) is not { } reason || !MentionReasons.Contains(reason)
                    || TextOf(n["uri"]) is not { Length: > 0 } uri
                    || n["author"] is not JsonObject author
                    || TextOf(author["did"]) is not { Length: > 0 } did
                    || TextOf(n["indexedAt"]) is not { } indexed
                    || !DateTimeOffset.TryParse(indexed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var indexedAt))
                {
                    continue;
                }

                var record = n["record"] as JsonObject;
                var name = TextOf(author["displayName"]);
                var answered = reason == "reply" ? TextOf(record?["reply"]?["parent"]?["uri"]) ?? TextOf(n["reasonSubject"]) : null;

                mentions.Add(new BlueskyMention(
                    reason, uri, did, TextOf(author["handle"]) ?? string.Empty,
                    string.IsNullOrWhiteSpace(name) ? null : name.Trim(), TextOf(record?["text"]) ?? string.Empty, answered, indexedAt));
            }
        }

        return new BlueskyMentionsPage(mentions, TextOf(body?["cursor"]) is { Length: > 0 } next ? next : null);
    }

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
