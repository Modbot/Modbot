using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;
using Modbot.Companion.Pipeline;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Pipeline;

/// <summary>
/// The one report a client makes when VRChat's log stops: once, for the instance the moderator was in,
/// and never as a repeating "still here".
/// </summary>
public sealed class LogStoppedTests : IDisposable
{
    private const string Instance = "wrld_w:85019~group(grp_cats)~groupAccessType(plus)~region(use)";

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-stopped-").FullName;
    private readonly FakeClock _clock = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LogPath => Path.Combine(_directory, "output_log_2026-09-03_20-26-45.txt");

    private PresenceObserver Observer() => new(new VRChatLogTail(_directory), _clock);

    private static readonly string[] Arrival =
    [
        $"2026.09.03 21:00:00 Debug      -  [Behaviour] Joining {Instance}",
        "2026.09.03 21:00:10 Debug      -  [Behaviour] OnPlayerJoined Ada (usr_ada)",
        "2026.09.03 21:00:10 Debug      -  [Behaviour] OnPlayerJoined bin¹ (usr_mod)",
        "2026.09.03 21:00:10 Debug      -  [Behaviour] Initialized PlayerAPI \"bin¹\" is local",
    ];

    private const string FrameRateLine =
        "2026.09.03 21:30:00 Debug      -  [VRCTrackingSteam] [IK Debug Log] FPS: 89.86 Quality: 3";

    /// <summary>A running client that watched the moderator walk into a group instance.</summary>
    private PresenceObserver InTheInstance()
    {
        File.WriteAllLines(LogPath, ["2026.09.03 20:59:00 Debug      -  [Behaviour] Using server environment: Release"]);
        var observer = Observer();
        observer.Poll();

        File.AppendAllLines(LogPath, Arrival);
        Assert.Equal(2, observer.Poll().Count);

        return observer;
    }

    [Fact]
    public void ALogThatStopsIsReportedOnce_ForTheModerator_AtTheLastLineItWrote()
    {
        var observer = InTheInstance();

        _clock.Advance(PresenceObserver.InstanceStaleAfter + TimeSpan.FromSeconds(1));
        var stopped = Assert.Single(observer.Poll());

        Assert.Equal(PresenceKind.LogStopped, stopped.Kind);
        Assert.Equal("usr_mod", stopped.SubjectId);
        Assert.Equal("85019", stopped.Instance.InstanceId);
        Assert.Equal("grp_cats", stopped.Instance.GroupId);
        Assert.Equal(new DateTime(2026, 9, 3, 21, 0, 10), stopped.OccurredAtLocal);

        // Not a heartbeat: an hour of silence afterwards says nothing more.
        for (var minute = 0; minute < 60; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(observer.Poll());
        }
    }

    [Fact]
    public void AQuietInstanceWhoseLogKeepsGrowingReportsNoStop()
    {
        // The file never goes quiet while VRChat runs -- a frame-rate line every ten seconds --
        // however long it has been since anybody came or went.
        var observer = InTheInstance();

        for (var minute = 0; minute < 30; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            File.AppendAllLines(LogPath, [FrameRateLine]);
            Assert.Empty(observer.Poll());
        }
    }

    [Fact]
    public void ALogThatWasAlreadyDeadWhenModbotStartedIsNotReported()
    {
        // History replayed at startup tells the client where the moderator was. It is not a
        // session that stopped while anybody was watching, and last night's instance is not news.
        File.WriteAllLines(LogPath, Arrival);
        var observer = Observer();
        Assert.Empty(observer.Poll());

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(observer.Poll());
    }

    [Fact]
    public void WhenTheLogStartsAgain_TheInstanceIsRestatedOnce_AsAlreadyHere()
    {
        // A slept laptop woke up. The server ended the watch at the stop, so it has to hear once
        // that the moderator is watching again -- and "already here" is all that is known.
        var observer = InTheInstance();

        _clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Single(observer.Poll());

        File.AppendAllLines(LogPath, [FrameRateLine]);
        var restated = observer.Poll();

        Assert.Equal(2, restated.Count);
        Assert.All(restated, o => Assert.Equal(PresenceKind.PresenceObserved, o.Kind));
        Assert.All(restated, o => Assert.Equal(new DateTime(2026, 9, 3, 21, 30, 0), o.OccurredAtLocal));
        Assert.Equal(["usr_ada", "usr_mod"], restated.Select(o => o.SubjectId).Order());

        _clock.Advance(TimeSpan.FromSeconds(5));
        File.AppendAllLines(LogPath, [FrameRateLine]);
        Assert.Empty(observer.Poll());
    }

    [Fact]
    public void AModeratorWhoLeftBeforeTheLogStoppedIsNotReported()
    {
        var observer = InTheInstance();

        File.AppendAllLines(LogPath, ["2026.09.03 21:05:00 Debug      -  [Behaviour] OnLeftRoom"]);
        Assert.Single(observer.Poll());

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(observer.Poll());
    }

    [Fact]
    public void VRChatRestartingIsANewSession_NotAResume()
    {
        var observer = InTheInstance();

        _clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Single(observer.Poll());

        var relaunched = Path.Combine(_directory, "output_log_2026-09-03_22-00-00.txt");
        File.WriteAllLines(relaunched, ["2026.09.03 22:00:00 Debug      -  [Behaviour] Using server environment: Release"]);
        File.SetLastWriteTimeUtc(relaunched, File.GetLastWriteTimeUtc(LogPath).AddMinutes(1));

        Assert.Empty(observer.Poll());
    }
}
