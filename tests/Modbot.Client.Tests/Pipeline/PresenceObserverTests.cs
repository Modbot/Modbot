using Modbot.Client.Instances;
using Modbot.Client.LogReading;
using Modbot.Client.Pipeline;
using Modbot.Client.Tests.LogReading;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Pipeline;

public sealed class PresenceObserverTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-observer-").FullName;
    private readonly FakeClock _clock = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LogPath => Path.Combine(_directory, "output_log_2026-09-03_20-26-45.txt");

    private PresenceObserver Observer() => new(new VRChatLogTail(_directory), _clock);

    [Fact]
    public void ReportsNothingWhenVRChatIsNotRunning()
    {
        var observer = Observer();

        Assert.Empty(observer.Poll());
        Assert.Equal(LogHealthStatus.Idle, observer.Health.Evaluate(_clock.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void HistoryAlreadyInTheFileRebuildsStateWithoutBeingReported()
    {
        // The moderator started Modbot mid-session. The client needs to know where they are, and
        // must not re-report an evening's worth of arrivals as if it had just seen them.
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();

        Assert.Empty(observer.Poll());
        Assert.Equal("39047", observer.CurrentInstance?.InstanceId);
        Assert.Equal(2, observer.Roster.Count);
    }

    [Fact]
    public void EventsWrittenAfterStartupAreReported()
    {
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath,
        [
            "2026.09.03 21:00:00 Debug      -  [Behaviour] OnPlayerEnteredRoom",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined newcomer (usr_new)",
        ]);

        var observed = Assert.Single(observer.Poll());
        Assert.Equal(PresenceKind.Joined, observed.Kind);
        Assert.Equal("usr_new", observed.SubjectId);
        Assert.Equal("39047", observed.Instance.InstanceId);
    }

    [Fact]
    public void CountsHowMuchOfTheLogItActuallyLooksAt()
    {
        File.WriteAllLines(LogPath,
        [
            "2026.09.03 20:27:14 Debug      -  [Behaviour] OnPlayerJoined a (usr_a)",
            "2026.09.03 20:27:14 Debug      -  [Behaviour] checking for youtube-dl updates",
            "2026.09.03 20:27:14 Debug      -  [IK Debug Log] something about elbows",
            "2026.09.03 20:27:14 Debug      -  [API] a request Modbot never reads",
        ]);

        var observer = Observer();
        observer.Poll();

        Assert.Equal(4, observer.Health.LinesRead);
        Assert.Equal(2, observer.Health.BehaviourLines);
        Assert.Equal(1, observer.Health.RecognisedEvents);
    }

    [Fact]
    public void ALogThatGrowsButStopsMakingSenseIsAFault()
    {
        // The format-break alarm. VRChat is demonstrably running and writing, and Modbot has
        // recognised nothing for long enough that this is not just a quiet instance.
        File.WriteAllLines(LogPath, ["2026.09.03 20:27:14 Debug      -  [Behaviour] OnPlayerJoined a (usr_a)"]);
        var observer = Observer();
        observer.Poll();

        _clock.Advance(TimeSpan.FromMinutes(10));
        File.AppendAllLines(LogPath, ["2026.09.03 20:37:14 Debug      -  [Behaviour] OnPlayerArrived::v2 a usr_a"]);
        observer.Poll();

        Assert.Equal(
            LogHealthStatus.NotUnderstood,
            observer.Health.Evaluate(_clock.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void AQuietInstanceIsNotAFault()
    {
        File.WriteAllLines(LogPath, ["2026.09.03 20:27:14 Debug      -  [Behaviour] OnPlayerJoined a (usr_a)"]);
        var observer = Observer();
        observer.Poll();

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            LogHealthStatus.Healthy,
            observer.Health.Evaluate(_clock.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void VRChatBeingClosedIsNotAFault()
    {
        File.WriteAllLines(LogPath, ["2026.09.03 20:27:14 Debug      -  [Behaviour] OnPlayerJoined a (usr_a)"]);
        var observer = Observer();
        observer.Poll();

        _clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(
            LogHealthStatus.Idle,
            observer.Health.Evaluate(_clock.UtcNow, TimeSpan.FromMinutes(5)));
    }
}
