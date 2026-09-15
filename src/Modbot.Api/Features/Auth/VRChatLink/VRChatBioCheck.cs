using Modbot.VRChat;
using Modbot.VRChat.Users;
using VRChat.API.Model;

namespace Modbot.Api.Features.Auth.VRChatLink;

/// <summary>What reading a VRChat bio for a code found.</summary>
/// <param name="Read">The profile was fetched. False means <see cref="Problem"/> says why not.</param>
/// <param name="CodeFound">The code is in the bio, in any case.</param>
/// <param name="Profile">The profile as fetched, when it was.</param>
/// <param name="Problem">Why the profile could not be read, in words, when it could not.</param>
public sealed record BioCheck(bool Read, bool CodeFound, User? Profile, string? Problem);

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
/// One request through <see cref="IVRChatGate"/> on <c>users.read</c>, interactive priority,
/// <c>GetUserWithHttpInfoAsync</c>. Its rate limit question was answered when the class was added
/// (foundation §4.2.5). A 429 is a cold stop and nothing here retries it. The limits on how often a
/// person may ask belong to the callers, which know who is asking.
/// </para>
/// <para>
/// A fetched profile is recorded as a sighting whether or not the code is there: the row, the
/// profile facts and the sticky 18+ flag come from the same object profile sync would have fetched.
/// </para>
/// </remarks>
public sealed class VRChatBioCheck
{
    private readonly IVRChatGate _gate;
    private readonly VRChatUserProfiles _profiles;

    public VRChatBioCheck(IVRChatGate gate, VRChatUserProfiles profiles)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(profiles);

        _gate = gate;
        _profiles = profiles;
    }

    public async Task<BioCheck> CheckAsync(string vrchatUserId, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vrchatUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var result = await _gate.ExecuteAsync<User>(
            new VRChatEndpoint(VRChatEndpointClass.UsersRead, null, "GetUser"),
            (vrchat, token) => vrchat.Users.GetUserWithHttpInfoAsync(vrchatUserId, token),
            VRChatCallPriority.Interactive,
            ct);

        if (!result.Success)
            return new BioCheck(false, false, null, Explain(result));

        var profile = result.Value;

        if (profile is not null)
            await _profiles.RecordProfileAsync(VRChatUserSnapshot.From(profile), raw: null, ct);

        var bio = profile?.Bio ?? string.Empty;
        return new BioCheck(true, bio.Contains(code, StringComparison.OrdinalIgnoreCase), profile, null);
    }

    private static string Explain<T>(VRChatResult<T> result) => result.Kind switch
    {
        VRChatFailureKind.NotConfigured =>
            "Modbot's own VRChat account is not set up yet.",
        VRChatFailureKind.RateLimited =>
            "VRChat is rate limiting Modbot.",
        VRChatFailureKind.WafBlocked =>
            "Cloudflare is blocking Modbot's connection to VRChat.",
        _ when result.StatusCode == 404 =>
            "VRChat does not know that user id.",
        _ => $"Modbot could not read that profile: {result.ErrorMessage ?? "no answer from VRChat"}.",
    };
}
