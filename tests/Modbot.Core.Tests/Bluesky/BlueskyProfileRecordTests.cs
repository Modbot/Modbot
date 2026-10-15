using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// The profile record a save writes back (posts design §4.2g): only the name, the bio, the picture and the
/// banner change; the pinned post, the labels, the date the account began and any field Bluesky adds later
/// stay exactly as they were. The limits are Bluesky's own.
/// </summary>
public class BlueskyProfileRecordTests
{
    private static JsonObject Blob(string cid) => new()
    {
        ["$type"] = "blob",
        ["ref"] = new JsonObject { ["$link"] = cid },
        ["mimeType"] = "image/jpeg",
        ["size"] = 1234,
    };

    /// <summary>A profile as Bluesky keeps it, with fields Modbot has never heard of.</summary>
    private static JsonObject Record() => new()
    {
        ["$type"] = "app.bsky.actor.profile",
        ["displayName"] = "Our group",
        ["description"] = "We run the group.",
        ["avatar"] = Blob("bafkrei-old-avatar"),
        ["banner"] = Blob("bafkrei-old-banner"),
        ["labels"] = new JsonObject
        {
            ["$type"] = "com.atproto.label.defs#selfLabels",
            ["values"] = new JsonArray(new JsonObject { ["val"] = "bot" }),
        },
        ["pinnedPost"] = new JsonObject
        {
            ["$type"] = "com.atproto.repo.strongRef",
            ["uri"] = "at://did:plc:ewvi7nxzyoun6zhxrhs64oiz/app.bsky.feed.post/3l4pqjzzyde2a",
            ["cid"] = "bafy-post",
        },
        ["joinedViaStarterPack"] = new JsonObject { ["uri"] = "at://did:plc:other/app.bsky.graph.starterpack/abc", ["cid"] = "bafy-pack" },
        ["createdAt"] = "2024-03-01T10:00:00.000Z",
        ["somethingBlueskyAddsLater"] = new JsonArray(1, 2, 3),
    };

    private static void AssertOnlyTheseChanged(JsonObject before, JsonObject after, params string[] changed)
    {
        foreach (var (key, value) in before)
        {
            if (changed.Contains(key))
                continue;

            Assert.True(after.ContainsKey(key), $"{key} was dropped");
            Assert.True(JsonNode.DeepEquals(value, after[key]), $"{key} was changed");
        }

        foreach (var (key, _) in after)
            Assert.True(before.ContainsKey(key) || changed.Contains(key), $"{key} was added");
    }

    [Fact]
    public void AllFourFieldsAreReplaced_AndThePinAndEveryUnknownFieldStay()
    {
        var current = Record();

        var after = BlueskyProfileRecord.Apply(
            current, new BlueskyProfileChange("New name", "New bio", Blob("bafkrei-new-avatar"), Blob("bafkrei-new-banner")));

        Assert.Equal("New name", after["displayName"]!.GetValue<string>());
        Assert.Equal("New bio", after["description"]!.GetValue<string>());
        Assert.Equal("bafkrei-new-avatar", after["avatar"]!["ref"]!["$link"]!.GetValue<string>());
        Assert.Equal("bafkrei-new-banner", after["banner"]!["ref"]!["$link"]!.GetValue<string>());

        Assert.True(JsonNode.DeepEquals(current["pinnedPost"], after["pinnedPost"]));
        Assert.True(JsonNode.DeepEquals(current["labels"], after["labels"]));
        Assert.True(JsonNode.DeepEquals(current["joinedViaStarterPack"], after["joinedViaStarterPack"]));
        Assert.Equal("2024-03-01T10:00:00.000Z", after["createdAt"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(current["somethingBlueskyAddsLater"], after["somethingBlueskyAddsLater"]));

        AssertOnlyTheseChanged(current, after, "displayName", "description", "avatar", "banner");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("bio")]
    [InlineData("avatar")]
    [InlineData("banner")]
    public void EachFieldChangesAlone_AndTheRestOfTheRecordIsAsItWas(string field)
    {
        var current = Record();

        var change = field switch
        {
            "name" => new BlueskyProfileChange("Another name", null, null, null),
            "bio" => new BlueskyProfileChange(null, "Another bio", null, null),
            "avatar" => new BlueskyProfileChange(null, null, Blob("bafkrei-new-avatar"), null),
            _ => new BlueskyProfileChange(null, null, null, Blob("bafkrei-new-banner")),
        };

        var after = BlueskyProfileRecord.Apply(current, change);

        var key = field switch { "name" => "displayName", "bio" => "description", _ => field };
        Assert.False(JsonNode.DeepEquals(current[key], after[key]));
        AssertOnlyTheseChanged(current, after, key);
        Assert.NotNull(after["pinnedPost"]);
    }

    [Fact]
    public void TheRecordPassedInIsNotChanged()
    {
        var current = Record();
        var copy = (JsonObject)current.DeepClone();

        BlueskyProfileRecord.Apply(current, new BlueskyProfileChange("Another name", "", Blob("bafkrei-x"), null));

        Assert.True(JsonNode.DeepEquals(copy, current));
    }

    [Fact]
    public void AnEmptyNameOrBioClearsTheField_AndOnlySpacesDoToo()
    {
        var current = Record();

        var cleared = BlueskyProfileRecord.Apply(current, new BlueskyProfileChange("", "   ", null, null));

        Assert.False(cleared.ContainsKey("displayName"));
        Assert.False(cleared.ContainsKey("description"));
        AssertOnlyTheseChanged(current, cleared, "displayName", "description");
    }

    [Fact]
    public void ANameAndBioAreTrimmed()
    {
        var after = BlueskyProfileRecord.Apply(Record(), new BlueskyProfileChange("  Padded  ", "\n bio \n", null, null));

        Assert.Equal("Padded", after["displayName"]!.GetValue<string>());
        Assert.Equal("bio", after["description"]!.GetValue<string>());
    }

    [Fact]
    public void AnAccountWithNoProfileGetsANewRecordOfTheProfileType()
    {
        var after = BlueskyProfileRecord.Apply(null, new BlueskyProfileChange("Our group", null, null, null));

        Assert.Equal(BlueskyClient.ProfileCollection, after["$type"]!.GetValue<string>());
        Assert.Equal("Our group", after["displayName"]!.GetValue<string>());
        Assert.False(after.ContainsKey("description"));
        Assert.False(after.ContainsKey("pinnedPost"));
    }

    [Fact]
    public void ARecordWithNoPinStaysWithoutOne()
    {
        var current = Record();
        current.Remove("pinnedPost");

        var after = BlueskyProfileRecord.Apply(current, new BlueskyProfileChange("Another name", null, null, null));

        Assert.False(after.ContainsKey("pinnedPost"));
    }

    [Fact]
    public void ANameOfSixtyFourCharactersFits_AndSixtyFiveDoNot()
    {
        Assert.Null(BlueskyProfileRecord.ProblemWithName(new string('a', 64)));
        Assert.Equal(BlueskyProfileRecord.NameTooLong, BlueskyProfileRecord.ProblemWithName(new string('a', 65)));
        Assert.Null(BlueskyProfileRecord.ProblemWithName(null));
        Assert.Null(BlueskyProfileRecord.ProblemWithName(string.Empty));
    }

    [Fact]
    public void ABioOfTwoHundredFiftySixCharactersFits_AndTwoHundredFiftySevenDoNot()
    {
        Assert.Null(BlueskyProfileRecord.ProblemWithBio(new string('a', 256)));
        Assert.Equal(BlueskyProfileRecord.BioTooLong, BlueskyProfileRecord.ProblemWithBio(new string('a', 257)));
        Assert.Null(BlueskyProfileRecord.ProblemWithBio(null));
    }

    [Fact]
    public void CharactersAreCountedTheWayABrowserShowsThem()
    {
        // An "e" with an accent added is two code points and one character.
        var accented = "é";

        Assert.Null(BlueskyProfileRecord.ProblemWithName(string.Concat(Enumerable.Repeat(accented, 64))));
        Assert.Equal(BlueskyProfileRecord.NameTooLong, BlueskyProfileRecord.ProblemWithName(string.Concat(Enumerable.Repeat(accented, 65))));
    }

    [Fact]
    public void ANameOrBioOverTheByteLimitIsRefused_EvenWithFewCharacters()
    {
        // A family emoji is one character but 18 bytes: 36 of them are 36 characters and 648 bytes.
        var family = "👨‍👩‍👧";

        Assert.Null(BlueskyProfileRecord.ProblemWithName(string.Concat(Enumerable.Repeat(family, 35))));
        Assert.Equal(BlueskyProfileRecord.NameTooBig, BlueskyProfileRecord.ProblemWithName(string.Concat(Enumerable.Repeat(family, 36))));
        Assert.Null(BlueskyProfileRecord.ProblemWithBio(string.Concat(Enumerable.Repeat(family, 142))));
        Assert.Equal(BlueskyProfileRecord.BioTooBig, BlueskyProfileRecord.ProblemWithBio(string.Concat(Enumerable.Repeat(family, 143))));
    }

    [Fact]
    public void TheSpacesAroundANameAreNotCounted()
    {
        Assert.Null(BlueskyProfileRecord.ProblemWithName("  " + new string('a', 64) + "  "));
    }

    [Fact]
    public void ThePictureLimitIsOneMillionBytes_AndOnlyPngAndJpegAreTaken()
    {
        Assert.Equal(1_000_000, BlueskyProfileRecord.PictureLimit);
        Assert.Equal(new[] { "image/png", "image/jpeg" }, BlueskyProfileRecord.PictureTypes.ToArray());
    }

    [Fact]
    public void APictureIsShownFromBlueskysImageServerByTheContentIdInTheRecord()
    {
        var record = Record();

        Assert.Equal(
            "https://cdn.bsky.app/img/avatar/plain/did:plc:abc/bafkrei-old-avatar@jpeg",
            BlueskyProfileRecord.PictureAddress(record, "avatar", "did:plc:abc"));
        Assert.Equal(
            "https://cdn.bsky.app/img/banner/plain/did:plc:abc/bafkrei-old-banner@jpeg",
            BlueskyProfileRecord.PictureAddress(record, "banner", "did:plc:abc"));
    }

    [Fact]
    public void ARecordWithNoPictureGivesNoAddress()
    {
        var record = Record();
        record.Remove("avatar");

        Assert.Null(BlueskyProfileRecord.PictureAddress(record, "avatar", "did:plc:abc"));
        Assert.Null(BlueskyProfileRecord.PictureAddress(null, "banner", "did:plc:abc"));
    }
}
