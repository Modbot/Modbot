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
}
