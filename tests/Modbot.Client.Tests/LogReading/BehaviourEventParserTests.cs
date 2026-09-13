using Modbot.Client.LogReading;

namespace Modbot.Client.Tests.LogReading;

public class BehaviourEventParserTests
{
    private static readonly DateTime At = new(2026, 9, 3, 20, 32, 18);

    private static VRChatLogEvent? Parse(string message)
        => BehaviourEventParser.Parse(new VRChatLogLine(At, "Debug", "Behaviour", message));

    [Fact]
    public void ReadsAJoinWithAPlainName()
    {
        var join = Assert.IsType<PlayerJoinedEvent>(
            Parse("OnPlayerJoined BlackIndium (usr_786ac3f2-8e1a-45b6-b8d1-65306d36a66b)"));

        Assert.Equal("BlackIndium", join.DisplayName);
        Assert.Equal("usr_786ac3f2-8e1a-45b6-b8d1-65306d36a66b", join.UserId);
    }

    [Theory]
    [InlineData("~ RedZu ~")]
    [InlineData("ΛƧƬΛ")]
    [InlineData("bin¹")]
    [InlineData("-winter~")]
    [InlineData("Hawk Echos")]
    [InlineData("-Traceless-")]
    [InlineData("  leading and trailing  ")]
    [InlineData("なまえ (かっこ)")]
    public void DisplayNamesAreNeverSplitOnWhitespace(string displayName)
    {
        // Every one of these except the last two is an actual name from the fixture log. Spaces,
        // tildes, superscripts, hyphens and non-Latin scripts are all normal.
        var join = Assert.IsType<PlayerJoinedEvent>(
            Parse($"OnPlayerJoined {displayName} (usr_x)"));

        Assert.Equal(displayName.Trim(), join.DisplayName);
        Assert.Equal("usr_x", join.UserId);
    }

    [Fact]
    public void TakesTheLastIdBecauseADisplayNameCanImpersonateOne()
    {
        // Anyone can set their display name to "(usr_someone-else)". A parser that took the first
        // match would attribute this person's presence to whoever they named.
        var join = Assert.IsType<PlayerJoinedEvent>(
            Parse("OnPlayerJoined I am (usr_victim) really (usr_attacker)"));

        Assert.Equal("usr_attacker", join.UserId);
        Assert.Equal("I am (usr_victim) really", join.DisplayName);
    }

    [Fact]
    public void ExtractsLegacyIdsThatDoNotLookLikeIds()
    {
        // VRChat changed its id format years ago and legacy ids follow no structure at all. The
        // anchor is the delimiter, never a shape; foundation spec section 3.1.1.
        var join = Assert.IsType<PlayerJoinedEvent>(Parse("OnPlayerJoined Founder (8JoV9XEdpo)"));

        Assert.Equal("8JoV9XEdpo", join.UserId);
        Assert.Equal("Founder", join.DisplayName);
    }

    [Fact]
    public void RejectsAJoinLineWithNoIdRatherThanGuessing()
    {
        // OnPlayerJoinComplete carries a name and no id, so it cannot identify anybody.
        Assert.Null(Parse("OnPlayerJoinComplete ΛƧƬΛ"));
        Assert.Null(Parse("OnPlayerJoined nobody"));
    }

    [Fact]
    public void ReadsALeave()
    {
        var left = Assert.IsType<PlayerLeftEvent>(
            Parse("OnPlayerLeft hevy1015 (usr_c4ec4f0e-a7d6-489b-b74d-4036e913312d)"));

        Assert.Equal("hevy1015", left.DisplayName);
        Assert.Equal("usr_c4ec4f0e-a7d6-489b-b74d-4036e913312d", left.UserId);
    }

    [Fact]
    public void OnLeftRoomAndOnPlayerLeftRoomAreDifferentEvents()
    {
        // One character apart, opposite meanings: OnLeftRoom is the local user leaving and marks
        // the start of the phantom leave burst; OnPlayerLeftRoom is a remote player leaving.
        Assert.IsType<LocalPlayerLeftRoomEvent>(Parse("OnLeftRoom"));
        Assert.IsType<RemotePlayerLeftRoomEvent>(Parse("OnPlayerLeftRoom"));
    }

    [Fact]
    public void ReadsTheLocalUsersIdentity()
    {
        var local = Assert.IsType<LocalPlayerIdentifiedEvent>(
            Parse("Initialized PlayerAPI \"bin¹\" is local"));

        Assert.Equal("bin¹", local.DisplayName);
        Assert.Null(Parse("Initialized PlayerAPI \"ΛƧƬΛ\" is remote"));
    }

    [Fact]
    public void LocalIdentityToleratesQuotesInsideTheName()
    {
        var local = Assert.IsType<LocalPlayerIdentifiedEvent>(
            Parse("Initialized PlayerAPI \"say \"hi\"\" is local"));

        Assert.Equal("say \"hi\"", local.DisplayName);
    }

    [Fact]
    public void ReadsTheLocationLines()
    {
        const string location =
            "wrld_4cf554b4:85019~group(grp_c7ba8659)~groupAccessType(public)~ageGate~region(use)";

        Assert.Equal(location, Assert.IsType<DestinationSetEvent>(
            Parse($"Destination set: {location}")).Location);
        Assert.Equal(location, Assert.IsType<JoiningInstanceEvent>(
            Parse($"Joining {location}")).Location);
    }

    [Fact]
    public void JoiningOrCreatingRoomIsNotALocationLine()
    {
        // "Joining or Creating Room: The Black Cat" carries a world display name, not a location.
        Assert.Null(Parse("Joining or Creating Room: The Black Cat"));
    }

    [Fact]
    public void ReadsAnAvatarSwitch()
    {
        var avatar = Assert.IsType<AvatarSwitchedEvent>(
            Parse("Switching SubKay_ to avatar Mr․ Capybara"));

        var split = Assert.Single(avatar.CandidateSplits());
        Assert.Equal("SubKay_", split.DisplayName);
        Assert.Equal("Mr․ Capybara", split.AvatarName);
    }

    [Fact]
    public void AvatarSwitchOffersEveryCandidateSplitLastFirst()
    {
        // Both halves are attacker-controlled: a display name or an avatar name may itself contain
        // " to avatar ". There is no split that is right in every case, so the parser hands over
        // every reading and the session tracker picks the one naming somebody actually present.
        var avatar = Assert.IsType<AvatarSwitchedEvent>(
            Parse("Switching a to avatar b to avatar c"));

        Assert.Equal(
            [("a to avatar b", "c"), ("a", "b to avatar c")],
            avatar.CandidateSplits());
    }

    [Fact]
    public void TheNetworkRegionLineIsNotAnAvatarSwitch()
    {
        Assert.Null(Parse("Switching to network region us (current state: ConnectedToNameServer)"));
    }

    [Fact]
    public void IgnoresTheNinetySixPercentOfLinesModbotHasNoInterestIn()
    {
        Assert.Null(Parse("checking for youtube-dl updates"));
        Assert.Null(Parse("Avatar is Ready, Initializing"));
        Assert.Null(Parse("Destroying -winter~"));
    }

    [Fact]
    public void OnlyBehaviourLinesAreConsidered()
    {
        Assert.Null(BehaviourEventParser.Parse(
            new VRChatLogLine(At, "Debug", "API", "OnPlayerJoined x (usr_y)")));
        Assert.Null(BehaviourEventParser.Parse(
            new VRChatLogLine(At, "Debug", null, "OnPlayerJoined x (usr_y)")));
    }
}
