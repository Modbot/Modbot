using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// VRChat's two reads of a person, served the way VRChat serves them: one person per request,
/// 404 for an id it does not know, and a raw JSON body beside the typed object.
/// </summary>
/// <remarks>
/// <para>
/// The body matters here more than for the group endpoints. The producer reads the age
/// verification status from the body rather than from the SDK's enum (research:
/// <c>vrchat-user-object-findings.md</c>), and which fields a body carries is what decides which
/// columns a read may overwrite (research: <c>vrchat-public-profile-findings.md</c> §4), so a
/// fake that answered with an empty body would test a code path production never takes.
/// </para>
/// <para>
/// The two calls answer from the same person but carry different fields, exactly as VRChat does:
/// the public profile has the bio, the pronouns and the age verification; the user object has the
/// status line, the join date, the tag list and the pictures.
/// </para>
/// </remarks>
public sealed class FakeUsers
{
    private readonly Dictionary<string, (User User, string Raw, PublicProfile Profile, string ProfileRaw)> _profiles =
        new(StringComparer.Ordinal);

    /// <summary>Force a status other than 200 on every request -- 429 for a limit, 500 for a fault.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The same, for the public profile alone, so one call can fail while the other works.</summary>
    public HttpStatusCode? ProfileStatus { get; set; }

    /// <summary>The same, for the user object alone.</summary>
    public HttpStatusCode? UserObjectStatus { get; set; }

    /// <summary>Ids the public profile has no answer for, while the user object still does.</summary>
    public HashSet<string> ProfileMissing { get; } = new(StringComparer.Ordinal);

    /// <summary>Ids the user object has no answer for, while the public profile still does.</summary>
    public HashSet<string> UserMissing { get; } = new(StringComparer.Ordinal);

    /// <summary>Every id asked for, in order, on either call.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every id the public profile was asked for, in order.</summary>
    public List<string> ProfileRequests { get; } = [];

    /// <summary>Every id the user object was asked for, in order.</summary>
    public List<string> UserRequests { get; } = [];

    public int RequestCount => Requests.Count;

    /// <summary>
    /// Leave the bio out of the user object's body entirely, the way VRChat now does. What the
    /// public profile says is then the only bio there is.
    /// </summary>
    public bool UserOmitsBio { get; set; }

    /// <summary>
    /// Serves a profile for this id. Any field left null is left out of the body, the way VRChat
    /// leaves out nothing but tests need to say less.
    /// </summary>
    public FakeUsers Has(
        string id,
        string displayName = "Someone",
        string ageVerificationStatus = "hidden",
        bool ageVerified = false,
        string? bio = null,
        string? pronouns = null,
        string? statusDescription = null,
        string? avatarThumbnail = null,
        IReadOnlyList<string>? tags = null,
        string? dateJoined = "2020-01-15")
    {
        var user = new User
        {
            Id = id,
            DisplayName = displayName,
            Bio = bio ?? string.Empty,
            Pronouns = pronouns ?? string.Empty,
            StatusDescription = statusDescription ?? string.Empty,
            CurrentAvatarThumbnailImageUrl = avatarThumbnail ?? string.Empty,
            CurrentAvatarImageUrl = string.Empty,
            ProfilePicOverride = string.Empty,
            Tags = tags?.ToList() ?? [],
            AgeVerified = ageVerified,
            AgeVerificationStatus = ageVerificationStatus switch
            {
                "18+" => AgeVerificationStatus.plus18,
                "verified" => AgeVerificationStatus.verified,
                _ => AgeVerificationStatus.hidden,
            },
            Status = UserStatus.Active,
            State = UserState.Online,
            DeveloperType = DeveloperType.None,
            LastPlatform = "standalonewindows",
            DateJoined = dateJoined is null ? default : DateOnly.Parse(dateJoined),

            // The fields Modbot must never keep, present so the tests can prove they are dropped.
            Location = "wrld_x:1234~private(usr_y)~nonce(secret)",
            Note = "private note",
            FriendKey = "friendkey",
        };

        var raw = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["pronouns"] = pronouns ?? string.Empty,
            ["statusDescription"] = statusDescription ?? string.Empty,
            ["currentAvatarThumbnailImageUrl"] = avatarThumbnail ?? string.Empty,
            ["currentAvatarImageUrl"] = string.Empty,
            ["profilePicOverride"] = string.Empty,
            ["tags"] = new JsonArray([.. (tags ?? []).Select(t => JsonValue.Create(t))]),
            ["ageVerified"] = ageVerified,
            ["ageVerificationStatus"] = ageVerificationStatus,
            ["status"] = "active",
            ["state"] = "online",
            ["last_platform"] = "standalonewindows",
            ["date_joined"] = dateJoined,
            ["location"] = "wrld_x:1234~private(usr_y)~nonce(secret)",
            ["note"] = "private note",
            ["friendKey"] = "friendkey",
        };

        // VRChat is reported to have stopped sending the bio here. Absent, not empty: those mean
        // different things to a read that may only overwrite what it carried.
        if (!UserOmitsBio)
            raw["bio"] = bio ?? string.Empty;

        var publicProfile = new PublicProfile
        {
            Id = id,
            DisplayName = displayName,
            Bio = bio ?? string.Empty,
            Pronouns = pronouns ?? string.Empty,
            AgeVerified = ageVerified,
            AgeVerificationStatus = ageVerificationStatus switch
            {
                "18+" => AgeVerificationStatus.plus18,
                "verified" => AgeVerificationStatus.verified,
                _ => AgeVerificationStatus.hidden,
            },
            TrustTags = [.. tags ?? []],
            BioLinks = [],
            Languages = [],
            Badges = [],
            IconUrl = string.Empty,
        };

        // Exactly the fields VRChat's public profile carries -- no status line, no join date, no
        // tag list, no avatar pictures.
        var profileRaw = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["bio"] = bio ?? string.Empty,
            ["pronouns"] = pronouns ?? string.Empty,
            ["ageVerified"] = ageVerified,
            ["ageVerificationStatus"] = ageVerificationStatus,
            ["trustTags"] = new JsonArray([.. (tags ?? []).Select(t => JsonValue.Create(t))]),
            ["bioLinks"] = new JsonArray(),
            ["languages"] = new JsonArray(),
            ["iconUrl"] = string.Empty,
            ["hasVrcPlus"] = false,
        };

        var compact = new JsonSerializerOptions { WriteIndented = false };

        _profiles[id] = (user, raw.ToJsonString(compact), publicProfile, profileRaw.ToJsonString(compact));
        return this;
    }

    public FakeUsers Forget(string id)
    {
        _profiles.Remove(id);
        return this;
    }

    public IUsersApi Build()
    {
        var users = Substitute.For<IUsersApi>();

        users
            .GetUserWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Answer(call.ArgAt<string>(0))));

        users
            .GetPublicProfileWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(AnswerProfile(call.ArgAt<string>(0))));

        return users;
    }

    private ApiResponse<User> Answer(string id)
    {
        Requests.Add(id);
        UserRequests.Add(id);

        var status = UserObjectStatus ?? Status;

        if (status != HttpStatusCode.OK)
            return new ApiResponse<User>(status, new Multimap<string, string>(), null!, "{}");

        if (UserMissing.Contains(id) || !_profiles.TryGetValue(id, out var profile))
            return new ApiResponse<User>(HttpStatusCode.NotFound, new Multimap<string, string>(), null!, Gone);

        return new ApiResponse<User>(HttpStatusCode.OK, new Multimap<string, string>(), profile.User, profile.Raw);
    }

    private ApiResponse<PublicProfile> AnswerProfile(string id)
    {
        Requests.Add(id);
        ProfileRequests.Add(id);

        var status = ProfileStatus ?? Status;

        if (status != HttpStatusCode.OK)
            return new ApiResponse<PublicProfile>(status, new Multimap<string, string>(), null!, "{}");

        if (ProfileMissing.Contains(id) || !_profiles.TryGetValue(id, out var profile))
            return new ApiResponse<PublicProfile>(HttpStatusCode.NotFound, new Multimap<string, string>(), null!, Gone);

        return new ApiResponse<PublicProfile>(
            HttpStatusCode.OK, new Multimap<string, string>(), profile.Profile, profile.ProfileRaw);
    }

    private const string Gone = "{\"error\":{\"message\":\"User not found\",\"status_code\":404}}";
}
