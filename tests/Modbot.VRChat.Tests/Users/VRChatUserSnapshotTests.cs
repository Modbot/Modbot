using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Users;
using VRChat.API.Model;
using Modbot.Core.Users;

namespace Modbot.VRChat.Tests.Users;

/// <summary>What a fetched user object becomes, and what of it is never kept.</summary>
public class VRChatUserSnapshotTests
{
    private static VRChatUserSnapshot Snapshot(
        string? status = "hidden", bool ageVerified = false, params string[] tags) => new()
        {
            UserId = "usr_a",
            AgeVerificationStatus = status,
            AgeVerified = ageVerified,
            Tags = tags,
        };

    /// <summary>
    /// Any positive signal counts (design §4). VRChat's verification confirms 18+ and nothing
    /// else, so all three fields are the same claim.
    /// </summary>
    [Theory]
    [InlineData("18+", false, true)]
    [InlineData("verified", false, true)]
    [InlineData("hidden", true, true)]
    [InlineData("hidden", false, false)]
    [InlineData(null, false, false)]
    [InlineData("something-new", false, false)]
    public void ShowsEighteenPlusOnAnyPositiveSignal(string? status, bool ageVerified, bool expected)
        => Assert.Equal(expected, Snapshot(status, ageVerified).ShowsEighteenPlus);

    /// <summary>The status comes from the body, not the SDK's enum, so a value VRChat adds tomorrow survives.</summary>
    [Fact]
    public void TheAgeStatusIsReadFromTheBodyFirst()
    {
        var user = new UserResponse { Id = "usr_a", DisplayName = "A", AgeVerificationStatus = AgeVerificationStatus.hidden };
        var raw = new JsonObject { ["ageVerificationStatus"] = "18+", ["ageVerified"] = true };

        var snapshot = VRChatUserSnapshot.From(user, raw);

        Assert.Equal("18+", snapshot.AgeVerificationStatus);
        Assert.True(snapshot.AgeVerified);
    }

    /// <summary>Without a body the SDK's enum is mapped to its wire word -- <c>plus18</c> is spelled <c>18+</c>.</summary>
    [Fact]
    public void WithoutABody_TheEnumIsSpelledTheWayVRChatSpellsIt()
    {
        var user = new UserResponse { Id = "usr_a", AgeVerificationStatus = AgeVerificationStatus.plus18, Status = UserStatus.JoinMe };

        var snapshot = VRChatUserSnapshot.From(user);

        Assert.Equal("18+", snapshot.AgeVerificationStatus);
        Assert.Equal("join me", snapshot.Status);
    }

    [Fact]
    public void TheDiffIsTheAuditLogsChangedShape()
    {
        var before = Snapshot("hidden", false, "a", "b") with { DisplayName = "Old", Bio = "same" };
        var after = Snapshot("18+", true, "a", "b") with { DisplayName = "New", Bio = "same" };

        var diff = after.DifferencesFrom(before);

        Assert.Equal(["displayName", "ageVerificationStatus", "ageVerified"], diff.Select(d => d.Key));
        Assert.Equal("Old", diff["displayName"]!["old"]!.GetValue<string>());
        Assert.Equal("New", diff["displayName"]!["new"]!.GetValue<string>());
    }

    /// <summary>Two responses that list the same tags in a different order are not a change.</summary>
    [Fact]
    public void TagOrderIsNotAChange()
    {
        var user = new UserResponse { Id = "usr_a", Tags = ["z", "a"] };

        Assert.Equal(["a", "z"], VRChatUserSnapshot.From(user).Tags);
    }

    /// <summary>
    /// The rank is read off the tags and travels with them: onto the row, into the baseline, and
    /// into the diff beside the tag diff, under Modbot's own field name.
    /// </summary>
    [Fact]
    public void TheTrustRankTravelsWithTheTags()
    {
        var before = Snapshot("hidden", false, "language_eng", "system_trust_known");
        var after = Snapshot("hidden", false, "language_eng", "system_trust_veteran");

        Assert.Equal(TrustRank.User, before.TrustRank);
        Assert.Equal(TrustRank.TrustedUser, after.TrustRank);

        var diff = after.DifferencesFrom(before);
        Assert.Equal(["tags", "trustRank"], diff.Select(d => d.Key));
        Assert.Equal("User", diff["trustRank"]!["old"]!.GetValue<string>());
        Assert.Equal("TrustedUser", diff["trustRank"]!["new"]!.GetValue<string>());

        Assert.Equal("TrustedUser", after.Baseline()["trustRank"]!.GetValue<string>());

        var row = new VRChatUser { UserId = "usr_a" };
        after.ApplyTo(row);
        Assert.Equal(TrustRank.TrustedUser, row.TrustRank);
    }

    /// <summary>A tag change that leaves the rank where it was is a tag diff and nothing more.</summary>
    [Fact]
    public void ATagChangeThatKeepsTheRankDoesNotClaimARankChange()
    {
        var before = Snapshot("hidden", false, "system_trust_known");
        var after = Snapshot("hidden", false, "system_trust_known", "system_supporter");

        Assert.Equal(["tags"], after.DifferencesFrom(before).Select(d => d.Key));
    }

    /// <summary>
    /// The public profile does not carry the tags, so recording one never touches the rank the
    /// user read filled in -- not even to Visitor.
    /// </summary>
    [Fact]
    public void ThePublicProfileNeverSpeaksForTheRank()
    {
        var row = new VRChatUser { UserId = "usr_a", Tags = "[\"system_trust_trusted\"]", TrustRank = TrustRank.KnownUser };
        var profile = new PublicProfile { Id = "usr_a", DisplayName = "Trinity", TrustTags = ["system_trust_veteran"] };

        VRChatUserSnapshot.FromPublicProfile("usr_a", profile).ApplyTo(row);

        Assert.Equal(TrustRank.KnownUser, row.TrustRank);
    }

    /// <summary>Spec 5.3: instance locations carry nonces, and Modbot never persists instance secrets.</summary>
    [Fact]
    public void TheStoredBodyDropsLocationsTheNoteAndTheFriendKey()
    {
        var raw = new JsonObject
        {
            ["id"] = "usr_a",
            ["displayName"] = "A",
            ["location"] = "wrld_x:1~private(usr_y)~nonce(secret)",
            ["travelingToLocation"] = "wrld_x:2~nonce(secret)",
            ["travelingToInstance"] = "2~nonce(secret)",
            ["instanceId"] = "1~nonce(secret)",
            ["note"] = "private",
            ["friendKey"] = "key",
        };

        var stored = VRChatUserSnapshot.StoredJson(raw)!;

        Assert.DoesNotContain("nonce", stored);
        Assert.DoesNotContain("private", stored);
        Assert.DoesNotContain("friendKey", stored);
        Assert.Contains("\"displayName\":\"A\"", stored);

        // And the original is untouched -- the caller may still be reading it.
        Assert.True(raw.ContainsKey("location"));
    }

    [Fact]
    public void ARowThatWasNeverRefreshedHasNoSnapshotToDiffAgainst()
        => Assert.Null(VRChatUserSnapshot.FromRow(new VRChatUser { UserId = "usr_a" }));

    /// <summary>A row either read has filled is a row there is something to diff against.</summary>
    [Fact]
    public void ARowFilledByEitherReadHasASnapshot()
    {
        Assert.NotNull(VRChatUserSnapshot.FromRow(
            new VRChatUser { UserId = "usr_a", LastUserReadAt = DateTimeOffset.UnixEpoch }));

        Assert.NotNull(VRChatUserSnapshot.FromRow(
            new VRChatUser { UserId = "usr_a", LastRefreshedAt = DateTimeOffset.UnixEpoch }));
    }

    // ── What a response speaks for ──────────────────────────────────────────────────────────

    /// <summary>
    /// A field the body did not carry is left as it is stored. This is the whole reason the
    /// snapshot tracks presence: VRChat stopped sending the bio on the user object, and the SDK
    /// gives a missing string the same value as a cleared one.
    /// </summary>
    [Fact]
    public void AFieldTheBodyDidNotCarryIsNotWrittenToTheRow()
    {
        var row = new VRChatUser
        {
            UserId = "usr_a",
            Bio = "written by the public profile",
            StatusDescription = "at work",
            LastRefreshedAt = DateTimeOffset.UnixEpoch,
        };

        // A user object with no bio in the body at all.
        var user = new UserResponse { Id = "usr_a", DisplayName = "A", StatusDescription = "away" };
        var raw = new JsonObject { ["id"] = "usr_a", ["displayName"] = "A", ["statusDescription"] = "away" };

        VRChatUserSnapshot.From(user, raw).ApplyTo(row);

        Assert.Equal("written by the public profile", row.Bio);
        Assert.Equal("away", row.StatusDescription);
        Assert.Equal("A", row.DisplayName);
    }

    /// <summary>
    /// The user object lost the bio, the avatar pictures and the profile picture in API
    /// specification v1.21.0, so a body that still sends them empty is not an edit and must not
    /// wipe what the public profile filled in.
    /// </summary>
    [Fact]
    public void TheUserObjectNeverSpeaksForTheBioOrThePictures()
    {
        var row = new VRChatUser
        {
            UserId = "usr_a",
            Bio = "written by the public profile",
            ProfilePictureUrl = "https://example.invalid/a.png",
            CurrentAvatarImageUrl = "https://example.invalid/avatar.png",
            LastUserReadAt = DateTimeOffset.UnixEpoch,
        };

        var user = new UserResponse { Id = "usr_a", DisplayName = "A" };
        var raw = new JsonObject
        {
            ["id"] = "usr_a",
            ["displayName"] = "A",
            ["bio"] = string.Empty,
            ["profilePicOverride"] = string.Empty,
            ["currentAvatarImageUrl"] = string.Empty,
        };

        VRChatUserSnapshot.From(user, raw).ApplyTo(row);

        Assert.Equal("written by the public profile", row.Bio);
        Assert.Equal("https://example.invalid/a.png", row.ProfilePictureUrl);
        Assert.Equal("https://example.invalid/avatar.png", row.CurrentAvatarImageUrl);
    }

    /// <summary>
    /// The same rule, held against the typed object rather than the body.
    /// </summary>
    /// <remarks>
    /// SDK 2.21.0's <c>User</c> had no picture properties at all, so reading one was a compile
    /// error. The regeneration against specification v1.21.0 answers Get User with
    /// <c>UserResponse</c>, a wide union that carries the avatar pictures again even though the
    /// call does not send them for another person. Nothing may reach them through it.
    /// </remarks>
    [Fact]
    public void TheUserObjectsPicturesAreNotReadOffTheTypedObjectEither()
    {
        var row = new VRChatUser
        {
            UserId = "usr_a",
            ProfilePictureUrl = "https://example.invalid/a.png",
            CurrentAvatarImageUrl = "https://example.invalid/avatar.png",
            CurrentAvatarThumbnailImageUrl = "https://example.invalid/thumb.png",
            LastUserReadAt = DateTimeOffset.UnixEpoch,
        };

        var user = new UserResponse
        {
            Id = "usr_a",
            DisplayName = "A",
            CurrentAvatarImageUrl = "https://example.invalid/from-the-model.png",
            CurrentAvatarThumbnailImageUrl = "https://example.invalid/from-the-model-thumb.png",
        };

        var snapshot = VRChatUserSnapshot.From(user);

        Assert.Null(snapshot.CurrentAvatarImageUrl);
        Assert.Null(snapshot.CurrentAvatarThumbnailImageUrl);
        Assert.Null(snapshot.ProfilePictureUrl);

        snapshot.ApplyTo(row);

        Assert.Equal("https://example.invalid/a.png", row.ProfilePictureUrl);
        Assert.Equal("https://example.invalid/avatar.png", row.CurrentAvatarImageUrl);
        Assert.Equal("https://example.invalid/thumb.png", row.CurrentAvatarThumbnailImageUrl);
    }

    /// <summary>
    /// The no-body fallback is bounded by what the call carries, not by what the model has.
    /// </summary>
    /// <remarks>
    /// When a response body is missing, the user read falls back to serialising the typed object
    /// (<c>UserProfileSync</c>). <c>UserResponse</c> serialises around a hundred properties, the
    /// pictures and a pile of the signed-in account's own fields among them, so the fallback body
    /// is much wider than the old <c>User</c>'s. The set of fields a user read speaks for must
    /// still be the shorter list.
    /// </remarks>
    [Fact]
    public void AFallbackBodyBuiltFromTheModelStillSpeaksOnlyForWhatGetUserCarries()
    {
        var user = new UserResponse
        {
            Id = "usr_a",
            DisplayName = "A",
            CurrentAvatarImageUrl = "https://example.invalid/from-the-model.png",
        };

        var snapshot = VRChatUserSnapshot.From(user, VRChatUserSnapshot.ParseRaw(user.ToJson()));

        Assert.DoesNotContain(VRChatUserSnapshot.Fields.CurrentAvatarImageUrl, snapshot.Carried);
        Assert.DoesNotContain(VRChatUserSnapshot.Fields.CurrentAvatarThumbnailImageUrl, snapshot.Carried);
        Assert.DoesNotContain(VRChatUserSnapshot.Fields.ProfilePicOverride, snapshot.Carried);
        Assert.DoesNotContain(VRChatUserSnapshot.Fields.Bio, snapshot.Carried);
        Assert.Contains(VRChatUserSnapshot.Fields.DisplayName, snapshot.Carried);
    }

    /// <summary>Present and empty is a real edit, and is written.</summary>
    [Fact]
    public void AFieldTheBodyCarriedAsEmptyIsCleared()
    {
        var row = new VRChatUser { UserId = "usr_a", Bio = "old", LastRefreshedAt = DateTimeOffset.UnixEpoch };

        var profile = new PublicProfile { Id = "usr_a", Bio = string.Empty };
        var raw = new JsonObject { ["id"] = "usr_a", ["bio"] = string.Empty };

        VRChatUserSnapshot.FromPublicProfile("usr_a", profile, raw).ApplyTo(row);

        Assert.Null(row.Bio);
    }

    /// <summary>
    /// The public profile speaks for the bio, the pronouns, the name and the age verification,
    /// and for nothing else -- not the status line, the join date, the tag list or the pictures.
    /// </summary>
    [Fact]
    public void ThePublicProfileSpeaksForItsOwnFieldsOnly()
    {
        var row = new VRChatUser
        {
            UserId = "usr_a",
            StatusDescription = "at work",
            DateJoined = new DateOnly(2018, 3, 4),
            Tags = "[\"system_trust_known\"]",
            ProfilePictureUrl = "https://example.invalid/a.png",
            LastUserReadAt = DateTimeOffset.UnixEpoch,
        };

        var profile = new PublicProfile
        {
            Id = "usr_a",
            DisplayName = "Trinity",
            Bio = "hello",
            Pronouns = "she/her",
            AgeVerificationStatus = AgeVerificationStatus.plus18,
            AgeVerified = true,
            TrustTags = ["system_trust_veteran"],
            IconUrl = "https://example.invalid/icon.png",
        };

        var raw = new JsonObject
        {
            ["id"] = "usr_a",
            ["displayName"] = "Trinity",
            ["bio"] = "hello",
            ["pronouns"] = "she/her",
            ["ageVerificationStatus"] = "18+",
            ["ageVerified"] = true,
            ["trustTags"] = new JsonArray("system_trust_veteran"),
            ["iconUrl"] = "https://example.invalid/icon.png",
        };

        var snapshot = VRChatUserSnapshot.FromPublicProfile("usr_a", profile, raw);
        Assert.Equal(VRChatReadKind.PublicProfile, snapshot.Source);
        Assert.True(snapshot.ShowsEighteenPlus);

        snapshot.ApplyTo(row);

        Assert.Equal("Trinity", row.DisplayName);
        Assert.Equal("hello", row.Bio);
        Assert.Equal("she/her", row.Pronouns);
        Assert.Equal("18+", row.AgeVerificationStatus);

        // Untouched: the trust tags are not the tag list and the icon is not the profile picture.
        Assert.Equal("at work", row.StatusDescription);
        Assert.Equal(new DateOnly(2018, 3, 4), row.DateJoined);
        Assert.Equal("[\"system_trust_known\"]", row.Tags);
        Assert.Equal("https://example.invalid/a.png", row.ProfilePictureUrl);

        // The icon has a column of its own.
        Assert.Equal("https://example.invalid/icon.png", row.IconUrl);
    }

    // ── The icon, the banner and the represented group ──────────────────────────────────────

    // The SDK's models are not annotated for nullability, so a null here is what a null on the wire becomes.
    private static PublicProfile ProfileWith(string? icon, string? banner, ProfileRepresentedGroup? group) => new()
    {
        Id = "usr_a",
        DisplayName = "Trinity",
        IconUrl = icon!,
        BannerUrl = banner!,
        RepresentedGroup = group!,
    };

    private static JsonObject RawWith(string? icon, string? banner, JsonNode? group) => new()
    {
        ["id"] = "usr_a",
        ["displayName"] = "Trinity",
        ["iconUrl"] = icon,
        ["bannerUrl"] = banner,
        ["representedGroup"] = group,
    };

    /// <summary>The three land in their own columns, and the group keeps only its id, name and icon.</summary>
    [Fact]
    public void TheIconTheBannerAndTheRepresentedGroupAreWrittenToTheirOwnColumns()
    {
        var row = new VRChatUser { UserId = "usr_a", ProfilePictureUrl = "https://example.invalid/override.png" };

        var group = new ProfileRepresentedGroup
        {
            Id = "grp_1", Name = "The Black Cat", IconUrl = "https://example.invalid/grp.png",
            BannerUrl = "https://example.invalid/grp-banner.png",
        };

        VRChatUserSnapshot.FromPublicProfile(
                "usr_a",
                ProfileWith("https://example.invalid/icon.png", "https://example.invalid/banner.png", group),
                RawWith("https://example.invalid/icon.png", "https://example.invalid/banner.png",
                    new JsonObject { ["id"] = "grp_1", ["name"] = "The Black Cat", ["iconUrl"] = "https://example.invalid/grp.png" }))
            .ApplyTo(row);

        Assert.Equal("https://example.invalid/icon.png", row.IconUrl);
        Assert.Equal("https://example.invalid/banner.png", row.BannerUrl);
        Assert.Equal("grp_1", row.RepresentedGroupId);
        Assert.Equal("The Black Cat", row.RepresentedGroupName);
        Assert.Equal("https://example.invalid/grp.png", row.RepresentedGroupIconUrl);

        // The override is a different field and is left alone.
        Assert.Equal("https://example.invalid/override.png", row.ProfilePictureUrl);
    }

    /// <summary>
    /// The per-call rule holds for the new fields too: a body without them leaves the columns as
    /// they are, and a body that carries them empty or null clears them.
    /// </summary>
    [Fact]
    public void ABodyWithoutTheNewFieldsLeavesThemAlone_AndOneThatCarriesThemNullClearsThem()
    {
        var row = new VRChatUser
        {
            UserId = "usr_a",
            IconUrl = "https://example.invalid/icon.png",
            BannerUrl = "https://example.invalid/banner.png",
            RepresentedGroupId = "grp_1",
            RepresentedGroupName = "The Black Cat",
            LastRefreshedAt = DateTimeOffset.UnixEpoch,
        };

        var profile = new PublicProfile { Id = "usr_a", Bio = "hello" };
        VRChatUserSnapshot.FromPublicProfile("usr_a", profile, new JsonObject { ["id"] = "usr_a", ["bio"] = "hello" }).ApplyTo(row);

        Assert.Equal("https://example.invalid/icon.png", row.IconUrl);
        Assert.Equal("https://example.invalid/banner.png", row.BannerUrl);
        Assert.Equal("grp_1", row.RepresentedGroupId);

        VRChatUserSnapshot.FromPublicProfile("usr_a", ProfileWith(null, null, null), RawWith(null, null, null)).ApplyTo(row);

        Assert.Null(row.IconUrl);
        Assert.Null(row.BannerUrl);
        Assert.Null(row.RepresentedGroupId);
        Assert.Null(row.RepresentedGroupName);
        Assert.Null(row.RepresentedGroupIconUrl);
    }

    /// <summary>
    /// The diff names the three under VRChat's own field names. The group changes as a whole:
    /// old and new are the group's id, name and icon, or null for "represented none".
    /// </summary>
    [Fact]
    public void TheDiffCarriesTheIconTheBannerAndTheWholeRepresentedGroup()
    {
        var before = Snapshot() with
        {
            IconUrl = "https://example.invalid/old-icon.png",
            BannerUrl = "https://example.invalid/banner.png",
            RepresentedGroup = null,
        };

        var after = Snapshot() with
        {
            IconUrl = "https://example.invalid/new-icon.png",
            BannerUrl = "https://example.invalid/banner.png",
            RepresentedGroup = new VRChatRepresentedGroup("grp_1", "The Black Cat", "https://example.invalid/grp.png"),
        };

        var diff = after.DifferencesFrom(before);

        Assert.Equal(["iconUrl", "representedGroup"], diff.Select(d => d.Key));
        Assert.Equal("https://example.invalid/old-icon.png", diff["iconUrl"]!["old"]!.GetValue<string>());
        Assert.Equal("https://example.invalid/new-icon.png", diff["iconUrl"]!["new"]!.GetValue<string>());

        Assert.Null(diff["representedGroup"]!["old"]);
        Assert.Equal("grp_1", diff["representedGroup"]!["new"]!["groupId"]!.GetValue<string>());
        Assert.Equal("The Black Cat", diff["representedGroup"]!["new"]!["name"]!.GetValue<string>());
        Assert.Equal("https://example.invalid/grp.png", diff["representedGroup"]!["new"]!["iconUrl"]!.GetValue<string>());
    }

    /// <summary>The same group, sent twice, is not a change -- and a renamed group is.</summary>
    [Fact]
    public void TheSameRepresentedGroupIsNotAChange()
    {
        var before = Snapshot() with { RepresentedGroup = new VRChatRepresentedGroup("grp_1", "A", null) };
        var same = Snapshot() with { RepresentedGroup = new VRChatRepresentedGroup("grp_1", "A", null) };
        var renamed = Snapshot() with { RepresentedGroup = new VRChatRepresentedGroup("grp_1", "B", null) };

        Assert.Empty(same.DifferencesFrom(before));
        Assert.Equal(["representedGroup"], renamed.DifferencesFrom(before).Select(d => d.Key));
    }

    /// <summary>The group round-trips through the shape the facts carry.</summary>
    [Fact]
    public void TheRepresentedGroupRoundTripsThroughItsJson()
    {
        var group = new VRChatRepresentedGroup("grp_1", "The Black Cat", null);

        Assert.Equal(group, VRChatRepresentedGroup.FromJson(group.ToJson()));
        Assert.Null(VRChatRepresentedGroup.FromJson(null));
        Assert.Null(VRChatRepresentedGroup.FromJson(new JsonObject { ["name"] = "no id" }));
    }

    /// <summary>The user object still carries the icon and the banner, so a user read speaks for them too.</summary>
    [Fact]
    public void TheUserObjectSpeaksForTheIconAndTheBanner()
    {
        var row = new VRChatUser { UserId = "usr_a", IconUrl = "https://example.invalid/old.png", LastUserReadAt = DateTimeOffset.UnixEpoch };

        var user = new UserResponse { Id = "usr_a", IconUrl = "https://example.invalid/new.png", BannerUrl = "https://example.invalid/banner.png" };
        var raw = new JsonObject
        {
            ["id"] = "usr_a",
            ["iconUrl"] = "https://example.invalid/new.png",
            ["bannerUrl"] = "https://example.invalid/banner.png",
        };

        VRChatUserSnapshot.From(user, raw).ApplyTo(row);

        Assert.Equal("https://example.invalid/new.png", row.IconUrl);
        Assert.Equal("https://example.invalid/banner.png", row.BannerUrl);
    }

    /// <summary>A diff only ever reports fields the response actually carried.</summary>
    [Fact]
    public void TheDiffIgnoresFieldsTheResponseDidNotCarry()
    {
        var stored = Snapshot("hidden") with { Bio = "old", StatusDescription = "at work" };

        var profile = new PublicProfile { Id = "usr_a", Bio = "new" };
        var raw = new JsonObject { ["id"] = "usr_a", ["bio"] = "new" };

        var diff = VRChatUserSnapshot.FromPublicProfile("usr_a", profile, raw).DifferencesFrom(stored);

        Assert.Equal(["bio"], diff.Select(d => d.Key));
    }
}
