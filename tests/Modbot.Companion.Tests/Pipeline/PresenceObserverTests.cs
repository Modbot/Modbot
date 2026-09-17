using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Tests.LogReading;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Pipeline;

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

    [Fact]
    public void AnUncleanExitStopsTheClientBelievingItKnowsWhereTheModeratorIs()
    {
        // The fixture is an unclean exit and they are the ordinary case: the log has no OnLeftRoom,
        // no OnPlayerLeft and no disconnect line anywhere near its end -- VRChat was simply killed.
        // Left alone, the client would report the moderator as standing in that instance for as
        // long as it ran, and the overlay would go on fetching and showing its roster.
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        Assert.Equal("39047", observer.CurrentInstance?.InstanceId);

        _clock.Advance(PresenceObserver.InstanceStaleAfter + TimeSpan.FromSeconds(1));
        observer.Poll();

        Assert.Null(observer.CurrentInstance);
        Assert.False(observer.LogIsLive);
    }

    [Fact]
    public void AQuietInstanceDoesNotLookLikeAnExit()
    {
        // The distinction the staleness rule has to make. In the real sample there is a
        // forty-five minute stretch with no [Behaviour] line at all while the moderator was
        // demonstrably still present -- but the file itself never went quiet for more than eleven
        // seconds, because VRChat writes a frame-rate line roughly every ten. So liveness is
        // measured on the file, not on the events.
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        for (var minute = 0; minute < 45; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            File.AppendAllLines(LogPath,
            [
                "2026.09.03 21:00:00 Debug      -  [VRCTrackingSteam] [IK Debug Log] "
                + "Selfie Expression --- DISABLED - FPS: 89.86181 Quality: 3 Auto Quality: False",
            ]);

            observer.Poll();
        }

        Assert.Equal("39047", observer.CurrentInstance?.InstanceId);
    }

    [Fact]
    public void AMachineThatWokeUpAgainKnowsWhereItIsOnceTheLogResumes()
    {
        // A slept laptop, a hung frame, a VM paused for a demo. Nothing ended; the reading stopped.
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        _clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(observer.CurrentInstance);

        File.AppendAllLines(LogPath, ["2026.09.03 21:30:00 Debug      -  [Behaviour] OnPlayerEnteredRoom"]);
        observer.Poll();

        Assert.Equal("39047", observer.CurrentInstance?.InstanceId);
    }

    [Fact]
    public void VRChatRestartingDropsEverythingKnownAboutTheOldSession()
    {
        // VRChat opens a new log file on every launch, and there is a minute or two between that
        // and the first world load. Carrying the old instance across it would have the overlay
        // fetching last night's roster from a server, and showing somebody else's room.
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        var relaunched = Path.Combine(_directory, "output_log_2026-09-03_22-00-00.txt");
        File.WriteAllLines(relaunched,
        [
            "2026.09.03 22:00:00 Debug      -  [Behaviour] Using server environment: Release, bf0942f7",
        ]);

        File.SetLastWriteTimeUtc(relaunched, File.GetLastWriteTimeUtc(LogPath).AddMinutes(1));

        _clock.Advance(TimeSpan.FromSeconds(1));
        observer.Poll();

        Assert.True(observer.LogIsLive);
        Assert.Null(observer.CurrentInstance);
    }

    [Fact]
    public void LeavingAnInstanceIsNotStillBeingInIt()
    {
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath, ["2026.09.03 21:00:00 Debug      -  [Behaviour] OnLeftRoom"]);
        observer.Poll();

        Assert.True(observer.LogIsLive);
        Assert.Null(observer.CurrentInstance);
    }

    [Fact]
    public void WalkingIntoAnotherInstanceMovesWhereTheModeratorIs()
    {
        File.Copy(LogFixture.Path, LogPath);
        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath,
        [
            "2026.09.03 21:00:00 Debug      -  [Behaviour] OnLeftRoom",
            "2026.09.03 21:00:10 Debug      -  [Behaviour] Joining " + LogFixture.GroupLocation,
            "2026.09.03 21:00:11 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
        ]);

        observer.Poll();

        Assert.Equal(LogFixture.GroupInstanceId, observer.CurrentInstance?.InstanceId);
        Assert.Equal(LogFixture.GroupId, observer.CurrentInstance?.GroupId);
    }
}
