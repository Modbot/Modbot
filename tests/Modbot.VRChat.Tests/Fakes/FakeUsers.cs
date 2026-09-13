using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// The user endpoint, served the way VRChat serves it: one user per request, 404 for an id it
/// does not know, and a raw JSON body beside the typed object.
/// </summary>
/// <remarks>
/// The body matters here more than for the group endpoints. The producer reads the age
/// verification status from the body rather than from the SDK's enum (research:
/// <c>vrchat-user-object-findings.md</c>), so a fake that answered with an empty body would test
/// a code path production never takes.
/// </remarks>
public sealed class FakeUsers
{
    private readonly Dictionary<string, (User User, string Raw)> _profiles = new(StringComparer.Ordinal);

    /// <summary>Force a status other than 200 on every request -- 429 for a limit, 500 for a fault.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Every id asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    public int RequestCount => Requests.Count;

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
            ["bio"] = bio ?? string.Empty,
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

        _profiles[id] = (user, raw.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
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

        return users;
    }

    private ApiResponse<User> Answer(string id)
    {
        Requests.Add(id);

        if (Status != HttpStatusCode.OK)
            return new ApiResponse<User>(Status, new Multimap<string, string>(), null!, "{}");

        if (!_profiles.TryGetValue(id, out var profile))
        {
            return new ApiResponse<User>(
                HttpStatusCode.NotFound, new Multimap<string, string>(), null!,
                "{\"error\":{\"message\":\"User not found\",\"status_code\":404}}");
        }

        return new ApiResponse<User>(HttpStatusCode.OK, new Multimap<string, string>(), profile.User, profile.Raw);
    }
}
