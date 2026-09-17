using Modbot.Companion.Instances;
using Modbot.Companion.Voice;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// The queue between what happened and what is said: bursts become one sentence, old news is
/// dropped, and the lines somebody is waiting on go first.
/// </summary>
public class AnnouncementQueueTests
{
    private readonly FakeClock _clock = new();

    private AnnouncementQueue Queue() => new(_clock);

    [Fact]
    public void NothingWaitingIsNull()
    {
        Assert.Null(Queue().Next());
    }

    [Fact]
    public void OneJoinIsTheEventsScreensOwnSentence()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.Joined, "Rin");

        Assert.Equal("Rin joined your world", queue.Next());
        Assert.Null(queue.Next());
    }

    [Fact]
    public void TwoJoinsAreNamedTogether()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.Joined, "Rin");
        queue.Add(AnnouncementKind.Joined, "Kai");

        Assert.Equal("Rin and Kai joined your world", queue.Next());
    }

    [Fact]
    public void ABurstBecomesACount()
    {
        // Twenty people can arrive in the time one name takes to say. A list of five names is
        // not something anybody can hold; a count is.
        var queue = Queue();
        foreach (var name in new[] { "Rin", "Kai", "Mio", "Sol", "Ash" })
            queue.Add(AnnouncementKind.Joined, name);

        Assert.Equal("five people joined your world", queue.Next());
    }

    [Fact]
    public void LargeCountsAreDigits()
    {
        Assert.Equal(
            "12 people left your world",
            AnnouncementQueue.Coalesce(PresenceKind.Left, [.. Enumerable.Range(1, 12).Select(i => $"p{i}")]));
    }

    [Fact]
    public void TheSamePersonTwiceIsOnePerson()
    {
        // Somebody who dropped and came straight back is not "Rin and Rin".
        var queue = Queue();
        queue.Add(AnnouncementKind.Joined, "Rin");
        queue.Add(AnnouncementKind.Joined, "Rin");

        Assert.Equal("Rin joined your world", queue.Next());
    }

    [Fact]
    public void JoinsAndLeavesAreSeparateSentences()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.Left, "Kai");
        queue.Add(AnnouncementKind.Joined, "Rin");

        Assert.Equal("Rin joined your world", queue.Next());
        Assert.Equal("Kai left your world", queue.Next());
        Assert.Null(queue.Next());
    }

    [Fact]
    public void OldNewsIsDropped()
    {
        // A name spoken thirty seconds late describes a room that no longer looks like that.
        var queue = Queue();
        queue.Add(AnnouncementKind.Joined, "Rin");

        _clock.Advance(AnnouncementQueue.PresenceMaxAge + TimeSpan.FromMilliseconds(1));

        Assert.Null(queue.Next());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void JustInTimeIsStillSaid()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.Left, "Rin");

        _clock.Advance(AnnouncementQueue.PresenceMaxAge);

        Assert.Equal("Rin left your world", queue.Next());
    }

    [Fact]
    public void AlertsWaitLongerThanPresence()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.FlaggedJoin, "Flagged user Rin joined");
        queue.Add(AnnouncementKind.Problem, "cats rejected this device.");

        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("cats rejected this device.", queue.Next());
        Assert.Equal("Flagged user Rin joined", queue.Next());

        queue.Add(AnnouncementKind.FlaggedJoin, "Flagged user Kai joined");
        _clock.Advance(AnnouncementQueue.AlertMaxAge + TimeSpan.FromMilliseconds(1));

        Assert.Null(queue.Next());
    }

    [Fact]
    public void ProblemsThenFlaggedJoinsThenTestThenJoinsThenLeaves()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.Left, "Sol");
        queue.Add(AnnouncementKind.Joined, "Rin");
        queue.Add(AnnouncementKind.Test, VoiceAnnouncer.TestLine);
        queue.Add(AnnouncementKind.FlaggedJoin, "Flagged user Ash joined");
        queue.Add(AnnouncementKind.Problem, "cats rejected this device.");

        Assert.Equal("cats rejected this device.", queue.Next());
        Assert.Equal("Flagged user Ash joined", queue.Next());
        Assert.Equal(VoiceAnnouncer.TestLine, queue.Next());
        Assert.Equal("Rin joined your world", queue.Next());
        Assert.Equal("Sol left your world", queue.Next());
        Assert.Null(queue.Next());
    }

    [Fact]
    public void FlaggedJoinsAreNeverFoldedTogether()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.FlaggedJoin, "Flagged user Rin joined");
        queue.Add(AnnouncementKind.FlaggedJoin, "Flagged user Kai joined");

        Assert.Equal("Flagged user Rin joined", queue.Next());
        Assert.Equal("Flagged user Kai joined", queue.Next());
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var queue = Queue();
        queue.Add(AnnouncementKind.Joined, "Rin");
        queue.Add(AnnouncementKind.Problem, "x");

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Null(queue.Next());
    }
}
