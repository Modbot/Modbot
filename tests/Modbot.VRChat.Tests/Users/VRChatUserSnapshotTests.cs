using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Users;
using VRChat.API.Model;

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
        var user = new User { Id = "usr_a", DisplayName = "A", AgeVerificationStatus = AgeVerificationStatus.hidden };
        var raw = new JsonObject { ["ageVerificationStatus"] = "18+", ["ageVerified"] = true };

        var snapshot = VRChatUserSnapshot.From(user, raw);

        Assert.Equal("18+", snapshot.AgeVerificationStatus);
        Assert.True(snapshot.AgeVerified);
    }

    /// <summary>Without a body the SDK's enum is mapped to its wire word -- <c>plus18</c> is spelled <c>18+</c>.</summary>
    [Fact]
    public void WithoutABody_TheEnumIsSpelledTheWayVRChatSpellsIt()
    {
        var user = new User { Id = "usr_a", AgeVerificationStatus = AgeVerificationStatus.plus18, Status = UserStatus.JoinMe };

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
        var user = new User { Id = "usr_a", Tags = ["z", "a"] };

        Assert.Equal(["a", "z"], VRChatUserSnapshot.From(user).Tags);
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
        var user = new User { Id = "usr_a", DisplayName = "A", Bio = string.Empty, StatusDescription = "away" };
        var raw = new JsonObject { ["id"] = "usr_a", ["displayName"] = "A", ["statusDescription"] = "away" };

        VRChatUserSnapshot.From(user, raw).ApplyTo(row);

        Assert.Equal("written by the public profile", row.Bio);
        Assert.Equal("away", row.StatusDescription);
        Assert.Equal("A", row.DisplayName);
    }

    /// <summary>Present and empty is a real edit, and is written.</summary>
    [Fact]
    public void AFieldTheBodyCarriedAsEmptyIsCleared()
    {
        var row = new VRChatUser { UserId = "usr_a", Bio = "old", LastRefreshedAt = DateTimeOffset.UnixEpoch };

        var user = new User { Id = "usr_a", Bio = string.Empty };
        var raw = new JsonObject { ["id"] = "usr_a", ["bio"] = string.Empty };

        VRChatUserSnapshot.From(user, raw).ApplyTo(row);

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
