using Modbot.Companion.Instances;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// Which kinds of event raise a notification, and by which of the three ways. The defaults are the
/// load-bearing part: somebody who never opens this card must keep being told exactly what they
/// were told before it existed.
/// </summary>
public class NotificationFiltersTests
{
    [Fact]
    public void TheDefaultsAreWhatTheCompanionDidBeforeFiltersExisted()
    {
        var filters = NotificationFilters.Default;

        // The voice said joins and leaves; nothing else reacted to them at all.
        Assert.True(filters.VoiceSays(NotificationKind.Joined));
        Assert.True(filters.VoiceSays(NotificationKind.Left));
        Assert.False(filters.PopUpShows(NotificationKind.Joined));
        Assert.False(filters.SoundPlays(NotificationKind.Joined));
        Assert.False(filters.PopUpShows(NotificationKind.Left));
        Assert.False(filters.SoundPlays(NotificationKind.Left));

        // All three ways raised a flagged join and a problem.
        foreach (var way in NotificationFilters.Ways)
        {
            Assert.True(filters.Wants(way, NotificationKind.FlaggedJoin));
            Assert.True(filters.Wants(way, NotificationKind.Problem));
        }

        // The three nothing ever reacted to.
        foreach (var kind in new[] { NotificationKind.AlreadyThere, NotificationKind.ChangedAvatar, NotificationKind.LogStopped })
        {
            foreach (var way in NotificationFilters.Ways)
                Assert.False(filters.Wants(way, kind));
        }
    }

    [Theory]
    [InlineData(NotificationKind.Joined)]
    [InlineData(NotificationKind.AlreadyThere)]
    [InlineData(NotificationKind.Left)]
    [InlineData(NotificationKind.ChangedAvatar)]
    [InlineData(NotificationKind.FlaggedJoin)]
    [InlineData(NotificationKind.LogStopped)]
    [InlineData(NotificationKind.Problem)]
    public void EveryKindCanBeTurnedOnAndOffForEveryWay(NotificationKind kind)
    {
        foreach (var way in NotificationFilters.Ways)
        {
            var on = NotificationFilters.Nothing.With(way, kind, true);
            Assert.True(on.Wants(way, kind));

            // And only for that way.
            foreach (var other in NotificationFilters.Ways.Where(w => w != way))
                Assert.False(on.Wants(other, kind));

            var off = NotificationFilters.Everything.With(way, kind, false);
            Assert.False(off.Wants(way, kind));

            foreach (var other in NotificationFilters.Ways.Where(w => w != way))
                Assert.True(off.Wants(other, kind));
        }
    }

    [Fact]
    public void TurningOneKindOffLeavesTheOthersAlone()
    {
        var filters = NotificationFilters.Everything.With(NotificationWay.Sound, NotificationKind.Joined, false);

        Assert.False(filters.SoundPlays(NotificationKind.Joined));
        foreach (var kind in NotificationFilters.Kinds.Where(k => k != NotificationKind.Joined))
            Assert.True(filters.SoundPlays(kind));
    }

    [Fact]
    public void TheTestButtonIsNeverFiltered()
    {
        // A person pressed it and is waiting for the answer.
        foreach (var way in NotificationFilters.Ways)
            Assert.True(NotificationFilters.Nothing.Wants(way, NotificationKind.Test));
    }

    [Fact]
    public void TheTestKindIsNotARowAndCannotBeWrittenToTheFile()
    {
        Assert.DoesNotContain(NotificationKind.Test, NotificationFilters.Kinds);

        var sneaked = new NotificationFilters([NotificationKind.Test], [], []);
        Assert.Empty(sneaked.For(NotificationWay.PopUp));
        Assert.DoesNotContain("test", sneaked.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordsAreTheEventsPagesWords()
    {
        // One vocabulary for both filter bars, so a hand-edited settings.json reads the same way
        // wherever a kind is named.
        foreach (var kind in new[]
                 {
                     NotificationKind.Joined,
                     NotificationKind.AlreadyThere,
                     NotificationKind.Left,
                     NotificationKind.ChangedAvatar,
                     NotificationKind.LogStopped,
                 })
        {
            Assert.Contains(NotificationFilters.Word(kind), EventFilters.Kinds);
        }
    }

    [Fact]
    public void EveryKindHasItsOwnWordAndItsOwnLabel()
    {
        var words = NotificationFilters.Kinds.Select(NotificationFilters.Word).ToList();
        var labels = NotificationFilters.Kinds.Select(NotificationFilters.Label).ToList();

        Assert.Equal(words.Count, words.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(words, string.IsNullOrWhiteSpace);
    }

    [Theory]
    [InlineData(PresenceKind.Joined, NotificationKind.Joined)]
    [InlineData(PresenceKind.PresenceObserved, NotificationKind.AlreadyThere)]
    [InlineData(PresenceKind.Left, NotificationKind.Left)]
    [InlineData(PresenceKind.AvatarChanged, NotificationKind.ChangedAvatar)]
    [InlineData(PresenceKind.LogStopped, NotificationKind.LogStopped)]
    public void EveryKindTheLogReaderProducesIsAKindThatCanBeFiltered(PresenceKind presence, NotificationKind expected)
    {
        Assert.Equal(expected, NotificationFilters.KindOf(presence));
        Assert.Contains(expected, NotificationFilters.Kinds);
    }

    [Fact]
    public void TheJsonRoundTrips()
    {
        var filters = NotificationFilters.Nothing
            .With(NotificationWay.PopUp, NotificationKind.Joined, true)
            .With(NotificationWay.Sound, NotificationKind.ChangedAvatar, true)
            .With(NotificationWay.Voice, NotificationKind.LogStopped, true)
            .With(NotificationWay.Voice, NotificationKind.Problem, true);

        Assert.Equal(filters, NotificationFilters.FromJson(filters.ToJson()));
        Assert.Equal(filters.GetHashCode(), NotificationFilters.FromJson(filters.ToJson()).GetHashCode());
    }

    [Fact]
    public void TheOrderTicksWereMadeInDoesNotChangeWhatTheFiltersSay()
    {
        var one = new NotificationFilters([NotificationKind.Problem, NotificationKind.Joined], [], []);
        var other = new NotificationFilters([NotificationKind.Joined, NotificationKind.Problem], [], []);

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void AWordThisClientDoesNotKnowIsIgnoredRatherThanBreakingTheFile()
    {
        // A file written by a newer client -- one that has learnt "18+ verified", say -- still
        // loads here, with the words this one understands.
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            { "popUp": ["joined", "18+ verified"], "sound": [], "voice": [] }
            """);

        var filters = NotificationFilters.FromJson(json);

        Assert.Equal([NotificationKind.Joined], filters.For(NotificationWay.PopUp));
        Assert.Empty(filters.For(NotificationWay.Sound));
    }

    [Fact]
    public void AMissingColumnTakesThatColumnsDefault()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""{ "sound": ["joined"] }""");

        var filters = NotificationFilters.FromJson(json);

        Assert.True(filters.SoundPlays(NotificationKind.Joined));
        Assert.False(filters.SoundPlays(NotificationKind.FlaggedJoin));
        Assert.Equal(NotificationFilters.Default.For(NotificationWay.PopUp), filters.For(NotificationWay.PopUp));
        Assert.Equal(NotificationFilters.Default.For(NotificationWay.Voice), filters.For(NotificationWay.Voice));
    }

    [Fact]
    public void NoObjectAtAllWithNoVoiceToReadIsTheDefaults()
    {
        Assert.Equal(NotificationFilters.Default, NotificationFilters.FromJson(null));
    }

    [Fact]
    public void TheVoiceColumnOfAnOlderFileComesFromItsOwnThreeSwitches()
    {
        // Somebody who turned joins off a month ago must not find them back on because the client
        // learnt a new word for the same switch.
        var older = new VoiceSettings(On: true, Joins: false, Leaves: true, FlaggedJoins: false);

        var filters = NotificationFilters.FromJson(null, older);

        Assert.False(filters.VoiceSays(NotificationKind.Joined));
        Assert.True(filters.VoiceSays(NotificationKind.Left));
        Assert.False(filters.VoiceSays(NotificationKind.FlaggedJoin));
        Assert.True(filters.VoiceSays(NotificationKind.Problem));

        // The other two ways are untouched by the voice's switches.
        Assert.Equal(NotificationFilters.Default.For(NotificationWay.PopUp), filters.For(NotificationWay.PopUp));
        Assert.Equal(NotificationFilters.Default.For(NotificationWay.Sound), filters.For(NotificationWay.Sound));
    }

    [Fact]
    public void AnOlderFileWithEveryVoiceSwitchOnIsExactlyTheDefaults()
    {
        Assert.Equal(NotificationFilters.Default, NotificationFilters.FromJson(null, VoiceSettings.Default));
    }

    [Fact]
    public void TheVoicesOwnThreeSwitchesAreKeptInStepWithTheVoiceColumn()
    {
        var filters = NotificationFilters.Nothing.With(NotificationWay.Voice, NotificationKind.Left, true);

        var voice = filters.InStepWith(new VoiceSettings(On: true, Volume: 42));

        Assert.False(voice.Joins);
        Assert.True(voice.Leaves);
        Assert.False(voice.FlaggedJoins);

        // Nothing else about the voice is touched.
        Assert.True(voice.On);
        Assert.Equal(42, voice.Volume);
    }
}
