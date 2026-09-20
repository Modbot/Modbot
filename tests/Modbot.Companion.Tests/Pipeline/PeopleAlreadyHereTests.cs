using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;
using Modbot.Companion.Pipeline;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Pipeline;

/// <summary>
/// The people who were already in the instance when this client caught up with VRChat's log.
/// </summary>
/// <remarks>
/// The replayed history is not reported, and it should not be -- an evening of arrivals is over.
/// The roster it ends with is a different thing: those people are standing there now, and no later
/// line will ever name them again. Without one restatement a moderator who starts Modbot in a busy
/// instance is reported as alone in it.
/// </remarks>
public sealed class PeopleAlreadyHereTests : IDisposable
{
    private const string Instance = "wrld_cat:85019~group(grp_cats)~groupAccessType(public)~region(use)";

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-already-here-").FullName;
    private readonly FakeClock _clock = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LogPath => Path.Combine(_directory, "output_log_2026-09-03_20-26-45.txt");

    private PresenceObserver Observer() => new(new VRChatLogTail(_directory), _clock);

    /// <summary>A busy instance the moderator walked into at nine: three people, then them.</summary>
    private static readonly string[] ArrivedInABusyInstance =
    [
        $"2026.09.03 21:00:00 Debug      -  [Behaviour] Joining {Instance}",
        "2026.09.03 21:00:10 Debug      -  [Behaviour] OnPlayerJoined Ada (usr_ada)",
        "2026.09.03 21:00:10 Debug      -  [Behaviour] OnPlayerJoined Ben (usr_ben)",
        "2026.09.03 21:00:10 Debug      -  [Behaviour] OnPlayerJoined Cid (usr_cid)",
        "2026.09.03 21:00:11 Debug      -  [Behaviour] OnPlayerJoined bin¹ (usr_mod)",
        "2026.09.03 21:00:11 Debug      -  [Behaviour] Initialized PlayerAPI \"bin¹\" is local",
    ];

    /// <summary>VRChat writes one of these roughly every ten seconds for as long as it runs.</summary>
    private const string FrameRateLine =
        "2026.09.03 22:00:00 Debug      -  [VRCTrackingSteam] [IK Debug Log] FPS: 89.86 Quality: 3";

    [Fact]
    public void AClientStartedWhileVRChatIsAlreadyRunning_ReportsThePeopleAlreadyThere()
    {
        // The reported defect. An hour into an evening the moderator starts Modbot. The client
        // reads the whole log to work out where they are, reports none of it, and until this
        // change reported nobody in the instance until somebody happened to walk in -- a head
        // count of thirty-six beside a list of five names.
        File.WriteAllLines(LogPath, ArrivedInABusyInstance);

        var observer = Observer();
        Assert.Empty(observer.Poll());

        File.AppendAllLines(LogPath, [FrameRateLine]);
        var caughtUp = observer.Poll();

        Assert.Equal(
            ["usr_ada", "usr_ben", "usr_cid", "usr_mod"],
            caughtUp.Select(o => o.SubjectId).Order());

        // Every one of them "already here", the moderator included. Their real arrival is an hour
        // back in a log this client is not entitled to report, and nobody knows how long any of
        // the others had been standing there.
        Assert.All(caughtUp, o => Assert.Equal(PresenceKind.PresenceObserved, o.Kind));
        Assert.All(caughtUp, o => Assert.Equal(new DateTime(2026, 9, 3, 22, 0, 0), o.OccurredAtLocal));
        Assert.All(caughtUp, o => Assert.Equal("85019", o.Instance.InstanceId));
        Assert.All(caughtUp, o => Assert.Equal("grp_cats", o.Instance.GroupId));
    }

    [Fact]
    public void ThePeopleAlreadyThereAreReportedOnce_NotOnEveryPoll()
    {
        // Not a heartbeat. The client says who it found and then goes back to reporting only what
        // it sees happen.
        File.WriteAllLines(LogPath, ArrivedInABusyInstance);

        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath, [FrameRateLine]);
        Assert.Equal(4, observer.Poll().Count);

        for (var minute = 0; minute < 10; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            File.AppendAllLines(LogPath, [FrameRateLine]);
            Assert.Empty(observer.Poll());
        }
    }

    [Fact]
    public void SomebodyWhoWalksInAfterwards_IsAnArrival_NotAnObservation()
    {
        // The distinction the whole design turns on. Auto-invites and the kick-duration display
        // both read it, so catching up must not turn later arrivals into "already here" -- nor
        // the people it caught up on into arrivals.
        File.WriteAllLines(LogPath, ArrivedInABusyInstance);

        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath,
        [
            FrameRateLine,
            "2026.09.03 22:05:00 Debug      -  [Behaviour] OnPlayerJoined Dee (usr_dee)",
        ]);

        var observed = observer.Poll();

        var arrival = Assert.Single(observed, o => o.Kind is PresenceKind.Joined);
        Assert.Equal("usr_dee", arrival.SubjectId);
        Assert.Equal(new DateTime(2026, 9, 3, 22, 5, 0), arrival.OccurredAtLocal);

        Assert.DoesNotContain(observed, o => o.Kind is PresenceKind.PresenceObserved && o.SubjectId == "usr_dee");
    }

    [Fact]
    public void AModeratorWalkingIntoABusyInstance_ReportsEverybodyAlreadyInIt()
    {
        // The case that already worked, kept honest: a running client watches the arrival burst
        // live, so everybody in it is reported and only the moderator's own join is an arrival.
        File.WriteAllLines(LogPath, ["2026.09.03 20:59:00 Debug      -  [Behaviour] Using server environment: Release"]);

        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath, ArrivedInABusyInstance);
        var observed = observer.Poll();

        Assert.Equal(
            ["usr_ada", "usr_ben", "usr_cid", "usr_mod"],
            observed.Select(o => o.SubjectId).Order());

        Assert.Equal("usr_mod", Assert.Single(observed, o => o.Kind is PresenceKind.Joined).SubjectId);
        Assert.Equal(3, observed.Count(o => o.Kind is PresenceKind.PresenceObserved));
    }

    [Fact]
    public void ALogVRChatStoppedWritingBeforeModbotStarted_RestatesNobody()
    {
        // Friday's log, opened on Monday. It ends with a roster and every one of those people
        // went home days ago; reporting them would put a closed instance back on the Live page.
        File.WriteAllLines(LogPath, ArrivedInABusyInstance);

        var observer = Observer();
        Assert.Empty(observer.Poll());

        for (var minute = 0; minute < 30; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(observer.Poll());
        }
    }

    [Fact]
    public void AModeratorSittingOutsideAnyInstance_RestatesNobody()
    {
        // VRChat running, nobody anywhere: the client knows no instance, so there is nobody to
        // report and nothing to attribute them to.
        File.WriteAllLines(LogPath, ["2026.09.03 20:59:00 Debug      -  [Behaviour] Using server environment: Release"]);

        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath, [FrameRateLine]);
        Assert.Empty(observer.Poll());
    }

    [Fact]
    public void VRChatRestarting_DoesNotRestateTheOldSessionsPeople()
    {
        // A new log file is a new session. Its own arrival burst is live and reports itself; the
        // people in the file before it are not in this instance and must not be carried across.
        File.WriteAllLines(LogPath, ArrivedInABusyInstance);

        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath, [FrameRateLine]);
        Assert.Equal(4, observer.Poll().Count);

        var relaunched = Path.Combine(_directory, "output_log_2026-09-03_23-00-00.txt");
        File.WriteAllLines(relaunched, ["2026.09.03 23:00:00 Debug      -  [Behaviour] Using server environment: Release"]);
        File.SetLastWriteTimeUtc(relaunched, File.GetLastWriteTimeUtc(LogPath).AddMinutes(1));

        Assert.Empty(observer.Poll());
        Assert.Null(observer.CurrentInstance);
    }

    [Fact]
    public void PointingTheReaderAtAnotherFolder_CatchesUpOnWhateverIsStandingInThere()
    {
        // Changing the VRChat log folder is a fresh start in every sense: the log already sitting
        // in the new folder is history, and it is owed the same one report of who it leaves in the
        // instance as a log found there at startup would be.
        var tail = new VRChatLogTail(_directory);
        var observer = new PresenceObserver(tail, _clock);
        observer.Poll();

        var elsewhere = Directory.CreateTempSubdirectory("modbot-other-folder-").FullName;
        try
        {
            var other = Path.Combine(elsewhere, "output_log_2026-09-03_21-00-00.txt");
            File.WriteAllLines(other, ArrivedInABusyInstance);

            tail.Redirect(elsewhere);
            Assert.Empty(observer.Poll());

            File.AppendAllLines(other, [FrameRateLine]);
            var caughtUp = observer.Poll();

            Assert.Equal(4, caughtUp.Count);
            Assert.All(caughtUp, o => Assert.Equal(PresenceKind.PresenceObserved, o.Kind));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }
}
