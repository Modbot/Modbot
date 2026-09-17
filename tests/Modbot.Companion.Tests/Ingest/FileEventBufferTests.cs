using Modbot.Companion.Ingest;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Ingest;

public sealed class FileEventBufferTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-buffer-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Path => System.IO.Path.Combine(_directory, "cats", "pending.jsonl");

    private FileEventBuffer Buffer(EventBufferLimits? limits = null) => new(Path, _clock, limits);

    private static ClientEvent Event(string id) => new()
    {
        ClientEventId = id,
        Type = ClientEventType.InstanceJoined,
        OccurredAt = new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero),
        SubjectId = "usr_subject",
        WorldId = "wrld_w",
        InstanceId = "85019",
        GroupId = "grp_cats",
    };

    [Fact]
    public void HoldsEventsInTheOrderTheyHappened()
    {
        var buffer = Buffer();
        buffer.Add(Event("a"));
        buffer.Add(Event("b"));

        Assert.Equal(["a", "b"], buffer.Peek(10).Select(e => e.ClientEventId));
    }

    [Fact]
    public void PeekingDoesNotRemove()
    {
        // A send that never completes must not lose its events. Removal happens only when the
        // server says it has them.
        var buffer = Buffer();
        buffer.Add(Event("a"));

        buffer.Peek(10);

        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void AcceptedEventsAreRemoved()
    {
        var buffer = Buffer();
        buffer.Add(Event("a"));
        buffer.Add(Event("b"));

        buffer.Remove(["a"]);

        Assert.Equal(["b"], buffer.Peek(10).Select(e => e.ClientEventId));
    }

    [Fact]
    public void SurvivesARestartWithTheSameIdempotencyKeys()
    {
        // The whole point of the id being written down: a retry after a crash carries the same key
        // the first attempt carried, so the server recognises the repeat.
        var first = Buffer();
        first.Add(Event("stable-id"));

        var second = Buffer();

        Assert.Equal("stable-id", Assert.Single(second.Peek(10)).ClientEventId);
    }

    [Fact]
    public void SurvivesARestartAfterARemoval()
    {
        var first = Buffer();
        first.Add(Event("a"));
        first.Add(Event("b"));
        first.Remove(["a"]);

        Assert.Equal(["b"], Buffer().Peek(10).Select(e => e.ClientEventId));
    }

    [Fact]
    public void DropsTheOldestWhenItIsFull()
    {
        var buffer = Buffer(new EventBufferLimits(MaxEvents: 3, MaxAge: TimeSpan.FromDays(1)));

        foreach (var id in new[] { "a", "b", "c", "d", "e" })
            buffer.Add(Event(id));

        Assert.Equal(["c", "d", "e"], buffer.Peek(10).Select(e => e.ClientEventId));
        Assert.Equal(2, buffer.Dropped);
    }

    [Fact]
    public void DropsWhatHasAgedOut()
    {
        var buffer = Buffer(new EventBufferLimits(MaxEvents: 1000, MaxAge: TimeSpan.FromHours(6)));
        buffer.Add(Event("stale"));

        _clock.Advance(TimeSpan.FromHours(7));
        buffer.Add(Event("fresh"));

        Assert.Equal(["fresh"], buffer.Peek(10).Select(e => e.ClientEventId));
        Assert.Equal(1, buffer.Dropped);
    }

    [Fact]
    public void AgeOutIsAppliedOnLoadToo()
    {
        // A client that was off for a week must not wake up and report a week of stale arrivals.
        var limits = new EventBufferLimits(MaxEvents: 1000, MaxAge: TimeSpan.FromHours(6));
        Buffer(limits).Add(Event("stale"));

        _clock.Advance(TimeSpan.FromDays(7));

        Assert.Empty(Buffer(limits).Peek(10));
    }

    [Fact]
    public void DropsAreCountedRatherThanSilent()
    {
        var buffer = Buffer(new EventBufferLimits(MaxEvents: 1, MaxAge: TimeSpan.FromDays(1)));
        buffer.Add(Event("a"));
        buffer.Add(Event("b"));

        Assert.Equal(1, buffer.Dropped);
    }

    [Fact]
    public void AHalfWrittenLastLineDoesNotStopTheClient()
    {
        // A power cut mid-append. Losing one observation is bad; refusing to start and losing every
        // observation from here on is worse.
        var buffer = Buffer();
        buffer.Add(Event("good"));
        File.AppendAllText(Path, "{\"enqueuedAt\":\"2026-09-12T20:0");

        Assert.Equal(["good"], Buffer().Peek(10).Select(e => e.ClientEventId));
    }

    [Fact]
    public void TwoServersKeepSeparateBuffers()
    {
        var cats = new FileEventBuffer(System.IO.Path.Combine(_directory, "cats.jsonl"), _clock);
        var dogs = new FileEventBuffer(System.IO.Path.Combine(_directory, "dogs.jsonl"), _clock);

        cats.Add(Event("cat-event"));

        Assert.Empty(dogs.Peek(10));
    }

    [Fact]
    public void ClearingRemovesTheFileContents()
    {
        var buffer = Buffer();
        buffer.Add(Event("a"));

        buffer.Clear();

        Assert.Empty(Buffer().Peek(10));
        Assert.Empty(File.ReadAllText(Path).Trim());
    }

    [Fact]
    public void NothingButTheAgreedFieldsIsWrittenToDisk()
    {
        var buffer = Buffer();
        buffer.Add(Event("a"));

        var written = File.ReadAllText(Path);

        Assert.Contains("\"subjectId\":\"usr_subject\"", written);
        Assert.DoesNotContain("Behaviour", written);
        Assert.DoesNotContain("output_log", written);
    }
}
