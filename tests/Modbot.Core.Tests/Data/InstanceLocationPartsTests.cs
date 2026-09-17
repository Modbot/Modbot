using Modbot.Core.Data;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// The split is by delimiters and nothing else (spec 3.1.1). Each case here is a string the
/// client's parser also accepts or rejects; the two are kept in step by hand, since the sync
/// side deliberately does not reference the client.
/// </summary>
public class InstanceLocationPartsTests
{
    /// <summary>
    /// A person's id has never had a colon in it and a location cannot do without one. This is
    /// what keeps an instance out of the profile queue, so both shapes of user id are pinned here.
    /// </summary>
    [Theory]
    [InlineData("wrld_06c991da-951b-4ca5-b7d2-e3f5a9839e28:03044~group(grp_0a17232e)~groupAccessType(plus)~region(use)", true)]
    [InlineData("wrld_a:12345", true)]
    [InlineData("Old Lobby:VIP Lounge~group(grp_x)", true)]
    [InlineData("usr_e94e15c9-d26b-4ebc-8906-b2f9162ef335", false)]
    [InlineData("8JoV9XEdpo", false)]
    [InlineData("wrld_a", false)]
    [InlineData("wrld_a:", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ALocationHasAColonWithANumberAfterIt_APersonNever(string? value, bool isLocation)
    {
        Assert.Equal(isLocation, InstanceLocationParts.LooksLikeALocation(value));
    }

    [Theory]
    [InlineData("wrld_44f4a344-2d1b-4c7e-9a3f-8b5e6d7c0f12:93927~group(grp_a)~groupAccessType(public)~region(us)", "wrld_44f4a344-2d1b-4c7e-9a3f-8b5e6d7c0f12", "93927")]
    [InlineData("wrld_a:12345", "wrld_a", "12345")]
    [InlineData("wrld_a:12345~ageGate", "wrld_a", "12345")]
    [InlineData("Old Lobby:VIP Lounge~group(grp_x)", "Old Lobby", "VIP Lounge")]
    [InlineData("wrld_a:has:colons~region(eu)", "wrld_a", "has:colons")]
    public void SplitsAtTheFirstColonAndTheFirstTilde(string raw, string worldId, string instanceId)
    {
        var parts = InstanceLocationParts.Split(raw);

        Assert.Equal(worldId, parts.WorldId);
        Assert.Equal(instanceId, parts.InstanceId);
    }

    /// <summary>
    /// No <c>:</c> is not an error and is not a reason to guess. The whole string is the world,
    /// and the instance is unknown.
    /// </summary>
    [Theory]
    [InlineData("wrld_a")]
    [InlineData("just-a-world")]
    [InlineData("wrld_a~region(us)")]
    public void AStringWithNoColonIsAllWorldAndNoInstance(string raw)
    {
        var parts = InstanceLocationParts.Split(raw);

        Assert.Equal(raw, parts.WorldId);
        Assert.Null(parts.InstanceId);
    }

    /// <summary>
    /// An empty stretch between the delimiters is nothing to key a row on, so it is null rather
    /// than the empty string -- which would otherwise look like a real instance called "".
    /// </summary>
    [Theory]
    [InlineData("wrld_a:", "wrld_a", null)]
    [InlineData("wrld_a:~group(grp_x)", "wrld_a", null)]
    [InlineData(":12345", null, "12345")]
    [InlineData(":", null, null)]
    public void AnEmptyPieceIsNullNotEmpty(string raw, string? worldId, string? instanceId)
    {
        var parts = InstanceLocationParts.Split(raw);

        Assert.Equal(worldId, parts.WorldId);
        Assert.Equal(instanceId, parts.InstanceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingInGivesNothingOut(string? raw)
    {
        var parts = InstanceLocationParts.Split(raw);

        Assert.Null(parts.WorldId);
        Assert.Null(parts.InstanceId);
    }
}
