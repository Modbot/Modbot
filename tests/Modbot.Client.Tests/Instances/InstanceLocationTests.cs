using Modbot.Client.Instances;
using Modbot.Client.Tests.LogReading;

namespace Modbot.Client.Tests.Instances;

public class InstanceLocationTests
{
    [Fact]
    public void ParsesTheRealGroupLocationFromTheFixture()
    {
        Assert.True(InstanceLocation.TryParse(LogFixture.GroupLocation, out var location));

        Assert.Equal(LogFixture.GroupWorldId, location.WorldId);
        Assert.Equal(LogFixture.GroupInstanceId, location.InstanceId);
        Assert.Equal(LogFixture.GroupId, location.GroupId);
        Assert.Equal("public", location.GroupAccessType);
        Assert.Equal("use", location.Region);
        Assert.True(location.IsGroupInstance);
    }

    [Fact]
    public void ToleratesValuelessQualifiers()
    {
        // "~ageGate" appears in the real log with no value at all and is in neither research note.
        Assert.True(InstanceLocation.TryParse(LogFixture.GroupLocation, out var location));

        Assert.Contains("ageGate", location.QualifierNames);
    }

    [Theory]
    [InlineData("wrld_4432ea9b:69955~private(usr_f2049d71)~region(use)")]
    [InlineData("wrld_266523e8:39047~friends(usr_527e5167)~region(use)")]
    [InlineData("wrld_266523e8:39047~hidden(usr_527e5167)~region(use)")]
    [InlineData("wrld_266523e8:39047~region(use)")]
    [InlineData("wrld_266523e8:39047")]
    public void AnInstanceWithNoGroupQualifierIsNotAGroupInstance(string raw)
    {
        // This is the whole of M3 3.1's local filter: a moderator's private, friends-only and
        // public VRChat use is excluded by the structure of the id, not by a heuristic.
        Assert.True(InstanceLocation.TryParse(raw, out var location));

        Assert.Null(location.GroupId);
        Assert.False(location.IsGroupInstance);
    }

    [Fact]
    public void NeverExposesTheInstanceSecret()
    {
        // ~nonce(...) is the instance secret for a non-group instance. It is dropped at the point
        // of parsing so no later code can persist or transmit it by accident, and so that a reader
        // can see it being dropped rather than take a README's word for it.
        Assert.True(InstanceLocation.TryParse(
            "wrld_x:1~private(usr_a)~nonce(SECRET-VALUE)~region(use)", out var location));

        Assert.DoesNotContain("nonce", location.QualifierNames);
        Assert.DoesNotContain("SECRET-VALUE", string.Join('|', location.QualifierNames));
        Assert.DoesNotContain("SECRET-VALUE", location.ToString());
    }

    [Fact]
    public void TheRawLocationStringIsNeverKept()
    {
        Assert.True(InstanceLocation.TryParse(
            "wrld_x:1~private(usr_a)~nonce(SECRET-VALUE)~region(use)", out var location));

        var everyStringProperty = location.GetType()
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(location));

        Assert.All(everyStringProperty, value => Assert.DoesNotContain("nonce", value ?? ""));
    }

    [Fact]
    public void IdentityIsWorldPlusInstanceBecauseInstanceIdsAreOnlyUniqueWithinAWorld()
    {
        Assert.True(InstanceLocation.TryParse("wrld_a:39911~group(grp_1)", out var first));
        Assert.True(InstanceLocation.TryParse("wrld_b:39911~group(grp_1)", out var second));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void InstanceIdsAreArbitraryUserControlledText()
    {
        // Groups routinely set a readable instance id through the API. It is not a number, and it
        // is hostile input wherever it is later displayed -- Discord mentions, markdown, bidi
        // overrides. Parsing keeps it verbatim; escaping is the display layer's job.
        Assert.True(InstanceLocation.TryParse(
            "wrld_a:@everyone <b>hi</b>~group(grp_1)~region(use)", out var location));

        Assert.Equal("@everyone <b>hi</b>", location.InstanceId);
        Assert.Equal("grp_1", location.GroupId);
    }

    [Fact]
    public void GroupIdsAreExtractedByDelimiterAndNotByShape()
    {
        Assert.True(InstanceLocation.TryParse("wrld_a:1~group(legacy-id-no-prefix)", out var location));

        Assert.Equal("legacy-id-no-prefix", location.GroupId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wrld_a")]
    [InlineData(":39911")]
    [InlineData("wrld_a:")]
    public void RejectsAnythingThatIsNotAWorldAndAnInstance(string? raw)
    {
        Assert.False(InstanceLocation.TryParse(raw, out _));
    }
}
