using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Core.Bluesky;

/// <summary>A working sign-in: where to send, as whom, and the token to send with.</summary>
/// <param name="DpopKey">
/// For a sign-in with Bluesky (OAuth): the key every call's DPoP proof is signed with. Null for a
/// session made with the app password.
/// </param>
public sealed record BlueskyAccess(string Did, Uri Server, string AccessJwt, string? DpopKey = null)
{
    // The token and the key are secrets; keep them out of any ToString.
    public override string ToString() => $"BlueskyAccess({Did})";
}

/// <summary>Why there is no working sign-in, when it is Modbot's own reason rather than Bluesky's answer.</summary>
public enum BlueskyHold
{
    /// <summary>Nothing holds it back; see the failure, if any.</summary>
    None,

    /// <summary>No account, server or app password is saved.</summary>
    NotSetUp,

    /// <summary>Bluesky limited Modbot, and the limit has not reset yet.</summary>
    Stopped,

    /// <summary>Bluesky refused the app password, and no new one has been saved since.</summary>
    SignInRefused,

    /// <summary>Modbot's own guard: a sign-in in the last 10 minutes, or 20 today already.</summary>
    TooManySignIns,

    /// <summary>
    /// Signed in with Bluesky (OAuth), and Bluesky no longer takes the sign-in, or there is none: with
    /// no app password kept, nothing signs in until someone signs in with Bluesky again.
    /// </summary>
    SignInEnded,
}

/// <summary>What <see cref="BlueskySession.AccessAsync"/> found.</summary>
/// <param name="Access">The sign-in to use, or null.</param>
/// <param name="Hold">Modbot's own reason there is none.</param>
/// <param name="Failure">Bluesky's answer, when that is the reason.</param>
/// <param name="Until">For <see cref="BlueskyHold.Stopped"/> and <see cref="BlueskyHold.TooManySignIns"/>, when it may be tried again.</param>
/// <param name="SignedIn">This call signed in with the app password (it counted against the guard).</param>
public sealed record BlueskySignIn(
    BlueskyAccess? Access,
    BlueskyHold Hold = BlueskyHold.None,
    BlueskyFailure? Failure = null,
    DateTimeOffset? Until = null,
    bool SignedIn = false)
{
    /// <summary>The sentence the operator reads when there is no sign-in, or null when there is one.</summary>
    public string? Problem => Access is not null ? null : Hold switch
    {
        BlueskyHold.NotSetUp => "Bluesky is not set up.",
        BlueskyHold.Stopped => BlueskyErrors.Limited,
        BlueskyHold.SignInRefused => BlueskyErrors.NotAccepted,
        BlueskyHold.TooManySignIns => BlueskyErrors.TooManySignIns,
        BlueskyHold.SignInEnded => BlueskyErrors.SignInEnded,
        _ => Failure is { } failure ? BlueskyErrors.Sentence(failure) : BlueskyErrors.Unreachable,
    };
}

/// <summary>
/// The one owner of Modbot's Bluesky session (Bluesky design §3.1): the sending loop, Check and
/// Delete all get their sign-in here. One per process: a singleton, with a lock, so two callers at
/// once never sign in twice or use a refresh token twice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The tokens live in the database, encrypted</strong> (<c>settings.bluesky_session_encrypted</c>),
/// so a restart keeps the session and needs no new sign-in. An access token close to its end is
/// refreshed; <c>refreshSession</c> also replaces the refresh token, and the old one stops working,
/// so the new pair is written to the database <em>before</em> it is handed out. Each write is one
/// statement on its own columns, so nothing else on the settings row is touched.
/// </para>
/// <para>
/// <strong>Signing in with the app password is the last resort</strong>, and guarded: at most once
/// in <see cref="SignInFloor"/> and <see cref="SignInsPerDay"/> times a UTC day, well under the 300 a
/// day Bluesky documents and the 100 once reported (facts 9, 10). The attempt is counted before it is
/// sent. A refused app password stops every sign-in until a new one is saved.
/// </para>
/// <para>
/// <strong>A rate limit stops the whole lane</strong> until the time Bluesky says it resets
/// (<c>settings.bluesky_stopped_until</c>), and nothing is sent before then (CLAUDE.md: never
/// retry a 429).
/// </para>
/// <para>
/// <strong>Both ways of signing in end here</strong> (step 3b). A sign-in with Bluesky (OAuth)
/// keeps its tokens in the same column, with its sign-in server and DPoP key; it is renewed at its
/// sign-in server (<see cref="BlueskyOAuth.RefreshAsync"/>) under the same lock and the same rule:
/// the new tokens are written before they are used. It has no app password to fall back on, so a
/// renewal Bluesky refuses ends it (<see cref="BlueskyHold.SignInEnded"/>) until someone signs in
/// with Bluesky again. The sign-in guard counts app-password sign-ins only.
/// </para>
/// </remarks>
public sealed class BlueskySession(BlueskyClient client, ISecretProtector protector, IModbotClock clock, BlueskyOAuth oauth)
{
    /// <summary>The fewest minutes between two sign-ins with the app password.</summary>
    public static readonly TimeSpan SignInFloor = TimeSpan.FromMinutes(10);

    /// <summary>The most sign-ins with the app password in a UTC day.</summary>
    public const int SignInsPerDay = 20;

    /// <summary>How long before its end an access token is refreshed rather than used.</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The same for a sign-in with Bluesky, whose access tokens are short (the OAuth spec recommends
    /// five minutes): renewed within its last minute, so a five-minute token is used, not renewed at once.
    /// </summary>
    public static readonly TimeSpan OAuthRefreshBefore = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// A sign-in to use: the saved session while its access token has time left, otherwise a refresh,
    /// otherwise a new sign-in with the app password, within the guard.
    /// </summary>
    /// <param name="prove">
    /// Ask the server whether the saved session still works (<c>getSession</c>) rather than trust it,
    /// as Check does. Never a sign-in when it does work.
    /// </param>
    public async Task<BlueskySignIn> AccessAsync(ModbotContext db, bool prove, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await _lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var now = clock.UtcNow;
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

            if (settings is not { BlueskyDid: { Length: > 0 } did }
                || BlueskyIdentity.ServerAddress(settings.BlueskyServer) is not { } server
                || (string.IsNullOrEmpty(settings.BlueskyAppPasswordEncrypted) && !settings.BlueskyOAuthSignedIn))
            {
                return new BlueskySignIn(null, BlueskyHold.NotSetUp);
            }

            if (settings.BlueskyStoppedUntil is { } stopped && now < stopped)
                return new BlueskySignIn(null, BlueskyHold.Stopped, Until: stopped);

            var tokens = Read(settings.BlueskySessionEncrypted);
            if (tokens is not null && tokens.Did != did)
                tokens = null;

            // A sign-in with Bluesky is only ever its own tokens; one made with the app password is
            // not used for it, nor the other way round.
            if (tokens is not null && (tokens.OAuth is not null) != settings.BlueskyOAuthSignedIn)
                tokens = null;

            if (settings.BlueskyOAuthSignedIn)
                return await OAuthAccessAsync(db, settings, tokens, did, server, prove, now, ct).ConfigureAwait(false);

            if (tokens is not null)
            {
                if (!Ending(tokens.AccessJwt, now))
                {
                    if (!prove)
                        return new BlueskySignIn(new BlueskyAccess(did, server, tokens.AccessJwt));

                    var proof = await client.GetSessionAsync(new BlueskyAccess(did, server, tokens.AccessJwt), ct).ConfigureAwait(false);

                    if (proof.Value == did)
                        return new BlueskySignIn(new BlueskyAccess(did, server, tokens.AccessJwt));

                    if (await StoppedByAsync(db, proof.Failure, ct).ConfigureAwait(false) is { } held)
                        return held;

                    // Not an answer about the token: the server did not answer.
                    if (proof.Failure is { Unclear: true })
                        return new BlueskySignIn(null, Failure: proof.Failure);
                }

                if (tokens.RefreshJwt.Length > 0)
                {
                    var refreshed = await client.RefreshSessionAsync(server, tokens.RefreshJwt, ct).ConfigureAwait(false);

                    if (refreshed.Value is { } fresh && fresh.Did == did)
                    {
                        // Written before it is used: the old refresh token no longer works.
                        await SaveAsync(db, fresh, ct).ConfigureAwait(false);
                        return new BlueskySignIn(new BlueskyAccess(did, server, fresh.AccessJwt));
                    }

                    if (await StoppedByAsync(db, refreshed.Failure, ct).ConfigureAwait(false) is { } held)
                        return held;

                    if (refreshed.Failure is { Unclear: true })
                        return new BlueskySignIn(null, Failure: refreshed.Failure);

                    if (refreshed.Failure is { Problem: BlueskyProblem.AccountGone } gone)
                    {
                        await ProblemAsync(db, BlueskyErrors.Sentence(gone), ct).ConfigureAwait(false);
                        return new BlueskySignIn(null, Failure: gone);
                    }

                    // An ended or refused refresh token: sign in again, below.
                }
            }

            return await SignInAsync(db, settings, did, server, now, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// When Modbot's sign-in guard lets the next sign-in with the app password go: 10 minutes after
    /// the last one, or the next UTC midnight once 20 were made today. Null when one may go now.
    /// </summary>
    public static DateTimeOffset? SignInAllowedAt(Data.Entities.Settings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.BlueskySignedInAt is { } last && now >= last && now - last < SignInFloor)
            return last + SignInFloor;

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var used = settings.BlueskySignInsDay == today ? settings.BlueskySignInsUsed : 0;

        return used >= SignInsPerDay
            ? new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
    }

    /// <summary>
    /// The server answered that <paramref name="accessJwt"/> has ended or is refused: it is not handed
    /// out again, and the next <see cref="AccessAsync"/> refreshes the session.
    /// </summary>
    public async Task ExpiredAsync(ModbotContext db, string accessJwt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await _lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var stored = await db.Settings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.BlueskySessionEncrypted)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (Read(stored) is { } tokens && tokens.AccessJwt == accessJwt)
                await SaveAsync(db, tokens with { AccessJwt = string.Empty }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Stops every call to Bluesky until the limit in <paramref name="failure"/> resets, and answers
    /// until when. Never shortens a stop already longer.
    /// </summary>
    public async Task<DateTimeOffset> StopAsync(ModbotContext db, BlueskyFailure failure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(failure);

        var until = BlueskyErrors.StopUntil(failure, clock.UtcNow);

        await db.Settings
            .Where(s => s.Id == 1 && (s.BlueskyStoppedUntil == null || s.BlueskyStoppedUntil < until))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyStoppedUntil, until), ct)
            .ConfigureAwait(false);

        return until;
    }

    /// <summary>The session's tokens as stored, or null when there are none or they cannot be read.</summary>
    public BlueskyTokens? Read(string? encrypted)
    {
        string? json;

        try
        {
            json = protector.Unprotect(encrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject value
                || value["accessJwt"] is not JsonValue a || !a.TryGetValue<string>(out var access)
                || value["refreshJwt"] is not JsonValue r || !r.TryGetValue<string>(out var refresh)
                || value["did"] is not JsonValue d || !d.TryGetValue<string>(out var owner))
            {
                return null;
            }

            var handle = value["handle"] is JsonValue h && h.TryGetValue<string>(out var named) ? named : null;

            if (value["oauth"] is not JsonObject grant)
                return new BlueskyTokens(access, refresh, owner, handle);

            string? Text(string name) => grant[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

            // A sign-in with Bluesky that cannot be read whole cannot be renewed: none.
            return Text("issuer") is { } issuer
                && Text("tokenEndpoint") is { } endpoint
                && Text("clientId") is { } clientId
                && Text("dpopKey") is { } key
                && Text("scope") is { } scope
                && grant["expiresAt"] is JsonValue e && e.TryGetValue<DateTimeOffset>(out var expiresAt)
                    ? new BlueskyTokens(access, refresh, owner, handle, new BlueskyOAuthGrant(issuer, endpoint, clientId, key, expiresAt, scope))
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The tokens as they are stored: one JSON, encrypted.</summary>
    public string Protect(BlueskyTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var value = new JsonObject
        {
            ["accessJwt"] = tokens.AccessJwt,
            ["refreshJwt"] = tokens.RefreshJwt,
            ["did"] = tokens.Did,
            ["handle"] = tokens.Handle,
        };

        if (tokens.OAuth is { } grant)
        {
            value["oauth"] = new JsonObject
            {
                ["issuer"] = grant.Issuer,
                ["tokenEndpoint"] = grant.TokenEndpoint,
                ["clientId"] = grant.ClientId,
                ["dpopKey"] = grant.DpopKey,
                ["expiresAt"] = grant.ExpiresAt,
                ["scope"] = grant.Scope,
            };
        }

        return protector.Protect(value.ToJsonString());
    }

    /// <summary>
    /// A sign-in with Bluesky has finished: its tokens become the session, under the lock, in one
    /// statement with the account they are for. The app password goes (one way of signing in at a
    /// time), and so does a refusal: the sign-in proved itself.
    /// </summary>
    public async Task SignedInWithBlueskyAsync(ModbotContext db, BlueskyTokens tokens, string handle, Uri server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(server);

        if (tokens.OAuth is null)
            throw new ArgumentException("Not a sign-in with Bluesky.", nameof(tokens));

        var sealedTokens = Protect(tokens);
        var serverText = server.ToString();

        await _lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await db.Settings
                .Where(s => s.Id == 1)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.BlueskyHandle, handle)
                    .SetProperty(s => s.BlueskyDid, tokens.Did)
                    .SetProperty(s => s.BlueskyServer, serverText)
                    .SetProperty(s => s.BlueskySessionEncrypted, sealedTokens)
                    .SetProperty(s => s.BlueskyOAuthSignedIn, true)
                    .SetProperty(s => s.BlueskyAppPasswordEncrypted, (string?)null)
                    .SetProperty(s => s.BlueskySignInRefused, false), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Whether a session's access token needs renewing before it is used: none left, or for a sign-in
    /// with Bluesky within <see cref="OAuthRefreshBefore"/> of the end the server gave, otherwise by the
    /// token's own <c>exp</c> (<see cref="Ending"/>).
    /// </summary>
    public static bool NeedsRenewing(BlueskyTokens tokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (string.IsNullOrEmpty(tokens.AccessJwt))
            return true;

        return tokens.OAuth is { } grant ? grant.ExpiresAt - now < OAuthRefreshBefore : Ending(tokens.AccessJwt, now);
    }

    /// <summary>
    /// Whether an access token has less than <see cref="RefreshBefore"/> left, by its own <c>exp</c>.
    /// The token is only read, never checked: the server decides whether it takes it. One that cannot
    /// be read is used, and a refusal from the server ends it.
    /// </summary>
    public static bool Ending(string? accessJwt, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(accessJwt))
            return true;

        var parts = accessJwt.Split('.');
        if (parts.Length != 3)
            return false;

        try
        {
            var payload = Base64Url.DecodeFromChars(parts[1]);
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("exp", out var exp)
                && exp.TryGetInt64(out var seconds)
                && DateTimeOffset.FromUnixTimeSeconds(seconds) - now < RefreshBefore;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<BlueskySignIn> SignInAsync(
        ModbotContext db, Data.Entities.Settings settings, string did, Uri server, DateTimeOffset now, CancellationToken ct)
    {
        if (settings.BlueskySignInRefused)
            return new BlueskySignIn(null, BlueskyHold.SignInRefused);

        if (SignInAllowedAt(settings, now) is { } allowed)
            return new BlueskySignIn(null, BlueskyHold.TooManySignIns, Until: allowed);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var used = settings.BlueskySignInsDay == today ? settings.BlueskySignInsUsed : 0;

        string? password;

        try
        {
            password = protector.Unprotect(settings.BlueskyAppPasswordEncrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            password = null;
        }

        if (string.IsNullOrEmpty(password))
            return new BlueskySignIn(null, BlueskyHold.NotSetUp);

        // Counted before it is sent, so a crash in the middle cannot let one more through.
        var count = used + 1;
        await db.Settings
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.BlueskySignedInAt, now)
                .SetProperty(s => s.BlueskySignInsDay, today)
                .SetProperty(s => s.BlueskySignInsUsed, count), ct)
            .ConfigureAwait(false);

        // The DID, not the handle: a handle changed on Bluesky does not stop the sign-in.
        var created = await client.CreateSessionAsync(server, did, password, ct).ConfigureAwait(false);

        if (created.Value is { } tokens && tokens.Did == did)
        {
            await SaveAsync(db, tokens, ct).ConfigureAwait(false);
            return new BlueskySignIn(new BlueskyAccess(did, server, tokens.AccessJwt), SignedIn: true);
        }

        if (await StoppedByAsync(db, created.Failure, ct).ConfigureAwait(false) is { } held)
            return held with { SignedIn = true };

        var failure = created.Failure ?? new BlueskyFailure(BlueskyProblem.BadSignIn, 200);

        if (failure.Problem == BlueskyProblem.BadSignIn || created.Value is not null)
        {
            // Nothing signs in again until a new app password is saved. The old session is useless now.
            await db.Settings
                .Where(s => s.Id == 1)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.BlueskySignInRefused, true)
                    .SetProperty(s => s.BlueskySessionEncrypted, (string?)null)
                    .SetProperty(s => s.BlueskyProblem, BlueskyErrors.NotAccepted), ct)
                .ConfigureAwait(false);

            return new BlueskySignIn(null, BlueskyHold.SignInRefused, failure, SignedIn: true);
        }

        if (failure.Problem == BlueskyProblem.AccountGone)
            await ProblemAsync(db, BlueskyErrors.Sentence(failure), ct).ConfigureAwait(false);

        return new BlueskySignIn(null, Failure: failure, SignedIn: true);
    }

    /// <summary>
    /// A sign-in for an account signed in with Bluesky: its token while it has time left (asked with
    /// <c>getSession</c> when <paramref name="prove"/>), otherwise renewed at its sign-in server and
    /// written before it is used. A renewal Bluesky refuses ends the sign-in; nothing signs in with an
    /// app password in its place.
    /// </summary>
    private async Task<BlueskySignIn> OAuthAccessAsync(
        ModbotContext db, Data.Entities.Settings settings, BlueskyTokens? tokens, string did, Uri server, bool prove, DateTimeOffset now, CancellationToken ct)
    {
        if (settings.BlueskySignInRefused || tokens?.OAuth is not { } grant)
            return new BlueskySignIn(null, BlueskyHold.SignInEnded);

        if (!NeedsRenewing(tokens, now))
        {
            var access = new BlueskyAccess(did, server, tokens.AccessJwt, grant.DpopKey);

            if (!prove)
                return new BlueskySignIn(access);

            var proof = await client.GetSessionAsync(access, ct).ConfigureAwait(false);

            if (proof.Value == did)
                return new BlueskySignIn(access);

            if (await StoppedByAsync(db, proof.Failure, ct).ConfigureAwait(false) is { } held)
                return held;

            if (proof.Failure is { Unclear: true })
                return new BlueskySignIn(null, Failure: proof.Failure);
        }

        var renewed = await oauth.RefreshAsync(db, tokens, ct).ConfigureAwait(false);

        if (renewed.Value is { } fresh && fresh.Did == did && fresh.OAuth is { } freshGrant)
        {
            // Written before it is used: the old refresh token no longer works.
            await SaveAsync(db, fresh, ct).ConfigureAwait(false);
            return new BlueskySignIn(new BlueskyAccess(did, server, fresh.AccessJwt, freshGrant.DpopKey));
        }

        if (await StoppedByAsync(db, renewed.Failure, ct).ConfigureAwait(false) is { } stopped)
            return stopped;

        if (renewed.Failure is { Unclear: true })
            return new BlueskySignIn(null, Failure: renewed.Failure);

        // Refused: the sign-in is over. Its tokens go, and nothing tries again until someone signs in.
        await db.Settings
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.BlueskySignInRefused, true)
                .SetProperty(s => s.BlueskySessionEncrypted, (string?)null)
                .SetProperty(s => s.BlueskyProblem, BlueskyErrors.SignInEnded), ct)
            .ConfigureAwait(false);

        return new BlueskySignIn(null, BlueskyHold.SignInEnded, renewed.Failure);
    }

    /// <summary>For a rate limit: the lane stopped, and the answer that says so. Null for anything else.</summary>
    private async Task<BlueskySignIn?> StoppedByAsync(ModbotContext db, BlueskyFailure? failure, CancellationToken ct)
    {
        if (failure is not { StopsTheLane: true })
            return null;

        var until = await StopAsync(db, failure, ct).ConfigureAwait(false);
        return new BlueskySignIn(null, BlueskyHold.Stopped, failure, until);
    }

    private Task SaveAsync(ModbotContext db, BlueskyTokens tokens, CancellationToken ct)
    {
        var sealedTokens = Protect(tokens);

        return db.Settings
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskySessionEncrypted, sealedTokens), ct);
    }

    private static Task ProblemAsync(ModbotContext db, string problem, CancellationToken ct) =>
        db.Settings
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyProblem, problem), ct);
}
