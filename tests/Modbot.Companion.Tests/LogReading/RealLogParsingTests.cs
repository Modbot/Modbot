using Modbot.Companion.LogReading;

namespace Modbot.Companion.Tests.LogReading;

public class RealLogParsingTests
{
    [Fact]
    public void EveryLineOfTheRealLogParsesAsARecord()
    {
        var unparsed = LogFixture.Lines()
            .Where(raw => !string.IsNullOrWhiteSpace(raw))
            .Where(raw => !VRChatLogLineParser.TryParse(raw, out _))
            .ToList();

        Assert.Empty(unparsed);
    }

    [Fact]
    public void TheSubsetIsTheSevenHundredAndFiftyFourBehaviourLinesOfASeventeenThousandLineLog()
    {
        // 754 of 17,123 lines -- 4.4%. Being able to state that plainly is the point of parsing
        // by tag: a moderator can check the claim with findstr.
        var lines = LogFixture.Lines();

        Assert.Equal(754, lines.Count);
        Assert.All(lines, raw =>
        {
            Assert.True(VRChatLogLineParser.TryParse(raw, out var line));
            Assert.Equal("Behaviour", line.Tag);
        });
    }

    [Fact]
    public void OnlyASmallShareOfEvenTheBehaviourLinesIsRecognised()
    {
        // Most of the 754 is VRChat talking to itself about UI managers and youtube-dl. It was 88
        // until 2026-09-19, when the three "Joining or Creating Room" lines stopped being skipped
        // and became the world names a saved clip is named after.
        Assert.Equal(91, LogFixture.Events().Count());
    }

    [Fact]
    public void FindsEveryPresenceEventInTheRealLog()
    {
        var events = LogFixture.Events().ToList();

        Assert.Equal(17, events.OfType<PlayerJoinedEvent>().Count());
        Assert.Equal(16, events.OfType<PlayerLeftEvent>().Count());
        Assert.Equal(2, events.OfType<LocalPlayerLeftRoomEvent>().Count());
        Assert.Equal(4, events.OfType<RemotePlayerLeftRoomEvent>().Count());
        Assert.Equal(7, events.OfType<RemotePlayerEnteredRoomEvent>().Count());
        Assert.Equal(3, events.OfType<JoiningInstanceEvent>().Count());
        Assert.Equal(3, events.OfType<DestinationSetEvent>().Count());
        Assert.Equal(3, events.OfType<LocalPlayerIdentifiedEvent>().Count());
        Assert.Equal(33, events.OfType<AvatarSwitchedEvent>().Count());
        Assert.Equal(3, events.OfType<WorldNameEvent>().Count());
    }

    [Fact]
    public void FindsTheReadableNameOfEveryWorldInTheRealLog()
    {
        // One world name for each of the three "Joining" lines, in the same order, which is what
        // lets a clip saved in the second of them be named after the second of them.
        Assert.Equal(
            ["VRChat Home", "The Black Cat", "Popcorn Palace"],
            LogFixture.Events().OfType<WorldNameEvent>().Select(w => w.WorldName));
    }

    [Fact]
    public void RecoversTheLocalUsersIdentityFromTheRealLog()
    {
        var events = LogFixture.Events().ToList();

        var declared = events.OfType<LocalPlayerIdentifiedEvent>().First();
        Assert.Equal(LogFixture.LocalDisplayName, declared.DisplayName);

        // The "is local" line names only a display name. The id comes from matching it against a
        // join line -- which is why the two have to be read together.
        var resolved = events.OfType<PlayerJoinedEvent>()
            .First(j => j.DisplayName == declared.DisplayName);
        Assert.Equal(LogFixture.LocalUserId, resolved.UserId);
    }

    [Fact]
    public void ReadsTheAwkwardRealDisplayNamesAndAvatarNames()
    {
        var events = LogFixture.Events().ToList();

        var names = events.OfType<PlayerJoinedEvent>().Select(j => j.DisplayName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("~ RedZu ~", names);
        Assert.Contains("ΛƧƬΛ", names);
        Assert.Contains("bin¹", names);
        Assert.Contains("Hawk Echos", names);

        // Full-width quotes and braces in an avatar name, straight out of the log.
        var avatars = events.OfType<AvatarSwitchedEvent>()
            .SelectMany(a => a.CandidateSplits())
            .Select(s => s.AvatarName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("＂ Evur ＂ By Kaiylast ｛FT｝", avatars);
    }

    [Fact]
    public void NoUserIdIsEverEmpty()
    {
        Assert.All(
            LogFixture.Events().OfType<PlayerJoinedEvent>(),
            j => Assert.False(string.IsNullOrWhiteSpace(j.UserId)));
    }
}
