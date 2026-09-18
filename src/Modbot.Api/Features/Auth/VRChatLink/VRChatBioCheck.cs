using Modbot.VRChat;
using Modbot.VRChat.Users;
using VRChat.API.Model;

namespace Modbot.Api.Features.Auth.VRChatLink;

/// <summary>What reading a VRChat bio for a code found.</summary>
/// <param name="Read">The profile was fetched. False means <see cref="Problem"/> says why not.</param>
/// <param name="CodeFound">The code is in the bio, in any case.</param>
/// <param name="Profile">The profile as fetched, when it was.</param>
/// <param name="Problem">Why the profile could not be read, in words, when it could not.</param>
public sealed record BioCheck(bool Read, bool CodeFound, PublicProfile? Profile, string? Problem);

/// <summary>
/// Proves control of a VRChat account: fetch the profile and look for the code in its bio
/// (accounts and access design §4.3).
/// </summary>
/// <remarks>
/// <para>
/// One piece of code for both links that use it -- a staff account's own VRChat account, and a
/// member's Discord account (Discord account linking design §2) -- so the proof is the same
/// wherever it is asked for.
/// </para>
/// <para>
/// One request through <see cref="IVRChatGate"/> on <c>users.profile</c>, interactive priority,
/// <c>GetPublicProfileWithHttpInfoAsync</c>. The public profile, not the user object: VRChat
/// stopped returning the bio on <c>GET /users/{userId}</c>, and the bio is the whole point of this
/// check (research: <c>vrchat-public-profile-findings.md</c>). Its rate limit came from the
/// maintainer on 2026-09-15 — the same as <c>users.read</c>, its own budget. A 429 is a cold stop
/// and nothing here retries it. The limits on how often a person may ask belong to the callers,
/// which know who is asking.
/// </para>
/// <para>
/// A fetched profile is recorded as a sighting whether or not the code is there: the row, the
/// profile facts and the sticky 18+ flag come from the same object profile sync would have fetched.
/// </para>
/// </remarks>
public sealed class VRChatBioCheck
{
    private readonly IVRChatGate _gate;
    private readonly VRChatUserProfiles? _profiles;

    /// <param name="profiles">
    /// The writer of <c>vrchat_user</c> rows, when this process has one. A demo registers no
    /// profile sync and therefore no writer, and the same is true of any host that wires the API
    /// without it -- so the check reads the bio and simply records nothing, the way every other
    /// endpoint that takes this writer already treats it as optional. Required rather than
    /// optional, it makes the whole container fail to build on a demo, which is the app refusing
    /// to start rather than one link check losing a sighting.
    /// </param>
    public VRChatBioCheck(IVRChatGate gate, VRChatUserProfiles? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(gate);

        _gate = gate;
        _profiles = profiles;
    }

    public async Task<BioCheck> CheckAsync(string vrchatUserId, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vrchatUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        // users.lookup, not users.profile: somebody is waiting on this read, and the background
        // profile sync's bucket -- and any cold stop it has earned -- is not theirs (spec 4.3.5).
        var result = await _gate.ExecuteAsync<PublicProfile>(
            new VRChatEndpoint(VRChatEndpointClass.UsersLookup, null, "GetPublicProfile"),
            (vrchat, token) => vrchat.Users.GetPublicProfileWithHttpInfoAsync(vrchatUserId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);

        if (!result.Success)
            return new BioCheck(false, false, null, Explain(result));

        var profile = result.Value;

        if (profile is not null && _profiles is not null)
        {
            // Recorded as the profile sync would have recorded it: the body decides which fields
            // are written, so a response that carries no bio leaves the stored one alone.
            var raw = VRChatUserSnapshot.ParseRaw(result.RawResponse);

            await _profiles.RecordProfileAsync(
                VRChatUserSnapshot.FromPublicProfile(vrchatUserId, profile, raw), raw, ct);
        }

        var bio = profile?.Bio ?? string.Empty;
        return new BioCheck(true, bio.Contains(code, StringComparison.OrdinalIgnoreCase), profile, null);
    }

    private static string Explain<T>(VRChatResult<T> result) => result.Kind switch
    {
        VRChatFailureKind.NotConfigured =>
            "Modbot's own VRChat account is not set up yet.",
        VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting =>
            "VRChat is rate limiting Modbot.",
        VRChatFailureKind.WafBlocked =>
            "Cloudflare is blocking Modbot's connection to VRChat.",
        _ when result.StatusCode == 404 =>
            "VRChat does not know that user id.",
        _ => $"Modbot could not read that profile: {result.ErrorMessage ?? "no answer from VRChat"}.",
    };
}
