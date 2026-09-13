using Avalonia.Controls;
using Modbot.Client.Ingest;
using Modbot.Client.LogReading;
using Modbot.Client.Overlay;
using Modbot.Client.Pipeline;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The whole path, from a real VRChat log to pixels: file, parser, phantom-burst rules, the
/// instance the moderator is standing in, the server that manages it, its roster, and a frame.
/// </summary>
/// <remarks>
/// <para>Every piece of this was built and tested in isolation and none of them were joined up —
/// nothing ever set the instance, so the overlay sat correctly idle and contacted nobody, forever.
/// A test at each end of that gap proves nothing about the gap, which is why this one exists and
/// why it is driven by the log VRChat actually wrote rather than by an ideal one. The interesting
/// cases are the ones the real file contains: a home world first, a populated group instance
/// second, a friends-only instance third, and an exit that was never logged at all.</para>
/// <para>The log is fed a line at a time, the way VRChat writes it, so the client is following a
/// live session rather than reading a finished file.</para>
/// </remarks>
public sealed class LogToOverlayTests : IDisposable
{
    /// <summary>The one group instance in the fixture: "The Black Cat".</summary>
    private const string GroupInstance = "85019";

    private const string GroupWorld = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";

    private const string GroupId = "grp_c7ba8659-4bf5-462f-8a7c-2cc31591f560";

    private const string LocalUserId = "usr_f2049d71-e76b-42d2-a8bd-43deec9c004e";

    /// <summary>The moderator's own home world, and the friends-only instance the session ends in.</summary>
    private const string HomeInstance = "69955";

    private const string PrivateInstance = "39047";

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-e2e-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string FixturePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "behaviour-2026-09-03.txt");

    private string LogPath => Path.Combine(_directory, "output_log_2026-09-03_20-26-45.txt");

    /// <summary>Counts every real layout-and-rasterise — the 1.3 ms the no-change gate saves.</summary>
    private sealed class CountingRenderer : IFrameRenderer
    {
        private readonly AvaloniaFrameRenderer _inner = new(256, 256);

        public int Width => _inner.Width;

        public int Height => _inner.Height;

        public int Renders { get; private set; }

        public ReadOnlySpan<byte> Render(Control root)
        {
            Renders++;
            return _inner.Render(root);
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class FakeSurface : IOverlaySurface
    {
        public int Width => 256;

        public int Height => 256;

        public nint TextureHandle => 1;

        public void Upload(ReadOnlySpan<byte> bgra) { }

        public void Dispose() { }
    }

    /// <summary>Keeps the last screen pushed, without weakening the real host's no-change gate.</summary>
    private sealed class Recording : IOverlayPresenter
    {
        public Recording(OverlayHost host) => Host = host;

        public OverlayHost Host { get; }

        public OverlayScreen Last { get; private set; } = OverlayScreen.Idle;

        public bool Update(OverlayScreen screen)
        {
            Last = screen;
            return Host.Update(screen);
        }

        public void Show() => Host.Show();

        public void Hide() => Host.Hide();
    }

    /// <summary>
    /// One server's answers, and a record of every instance it was asked about.
    /// </summary>
    /// <remarks>
    /// The list of instances asked about is itself an assertion: a moderator's home world and
    /// friends-only instance must never appear in it, because the overlay only ever speaks to the
    /// server whose group owns the instance they are standing in.
    /// </remarks>
    private sealed class Server : IOverlayReadClient
    {
        public List<string> ContextsAskedFor { get; } = [];

        public Queue<FlaggedJoinAlert> Alerts { get; } = new();

        public Task<ReadResult<InstanceContext>> GetContextAsync(
            ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
        {
            ContextsAskedFor.Add(instanceId);

            return Task.FromResult(new ReadResult<InstanceContext>(
                ReadOutcome.Fetched,
                new InstanceContext(instanceId,
                [
                    new RosterMember("usr_winter", "-winter~", RosterStanding.Member, 0, []),
                    new RosterMember("usr_trouble", "Trouble", RosterStanding.Flagged, 2, ["2 prior actions"]),
                ])));
        }

        public Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(
            ServerPairing pairing, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(Alerts.Count > 0
                ? new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alerts.Dequeue())
                : new ReadResult<FlaggedJoinAlert>(
                    ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(
            ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>The client and the overlay, wired the way the tray application wires them.</summary>
    private sealed record Session(
        FakeClock Clock,
        PresenceObserver Observer,
        OverlayDriver Driver,
        Server Reads,
        CountingRenderer Renderer,
        Recording Screen);

    /// <summary>
    /// One turn: the log reader says where the moderator is, and the overlay follows.
    /// </summary>
    private static void Turn(Session session)
    {
        session.Observer.Poll();
        session.Driver.EnteredInstance(session.Observer.CurrentInstance);

        // Every read in this test completes synchronously, so nothing here blocks Avalonia's
        // thread; the loop is the same one the application runs on a timer.
        session.Driver.TickAsync().GetAwaiter().GetResult();
    }

    private void Run(Action<Session> body)
    {
        AvaloniaTestHost.Run(() =>
        {
            var clock = new FakeClock();
            var reads = new Server();
            var renderer = new CountingRenderer();

            using var host = new OverlayHost(new HeadlessOverlayRuntime(), new FakeSurface(), renderer);
            var screen = new Recording(host);
            using var driver = new OverlayDriver(screen, reads, clock);

            driver.Add(
                new ServerPairing("blackcat", new Uri("https://blackcat.example"), "token", GroupId),
                "The Black Cat");

            var observer = new PresenceObserver(new VRChatLogTail(_directory), clock);

            // Primed against an empty folder, so everything written afterwards is a live session
            // rather than history to be rebuilt from and then discarded.
            observer.Poll();

            body(new Session(clock, observer, driver, reads, renderer, screen));
        });
    }

    /// <summary>Writes the fixture out the way VRChat does, giving the client a turn per line.</summary>
    private void Replay(Session session, Action? afterEachTurn = null)
    {
        foreach (var line in File.ReadLines(FixturePath))
        {
            File.AppendAllLines(LogPath, [line]);
            Turn(session);
            afterEachTurn?.Invoke();
        }
    }

    [Fact]
    public void TheLogDrivesTheOverlayIntoTheGroupInstanceAndOutAgain()
    {
        Run(session =>
        {
            var serversSpokenFor = new List<string?>();
            Replay(session, () => serversSpokenFor.Add(session.Driver.CurrentServer?.ServerId));

            // The moderator passed through the group instance and the overlay spoke for that
            // server while they were in it -- the join that did not exist before this.
            Assert.Contains("blackcat", serversSpokenFor);

            // And it asked that server about exactly one instance: the group's. The home world and
            // the friends-only instance either side of it belong to no paired server, so nothing
            // was contacted at all while the moderator was in them.
            Assert.Equal([GroupInstance], session.Reads.ContextsAskedFor.Distinct());
            Assert.DoesNotContain(HomeInstance, session.Reads.ContextsAskedFor);
            Assert.DoesNotContain(PrivateInstance, session.Reads.ContextsAskedFor);

            // The log ends in a friends-only instance, so the overlay finishes idle: no group
            // label, no roster, and nobody being spoken to.
            Assert.Null(session.Driver.CurrentServer);
            Assert.Null(session.Screen.Last.GroupLabel);

            Assert.True(session.Renderer.Renders > 0, "the overlay never drew anything");
        });
    }

    [Fact]
    public void TheRosterReachesTheScreenWhileTheModeratorIsInTheGroupInstance()
    {
        Run(session =>
        {
            OverlayScreen? inside = null;

            Replay(session, () =>
            {
                if (session.Driver.CurrentServer is not null && session.Screen.Last.Roster.Value is not null)
                    inside ??= session.Screen.Last;
            });

            Assert.NotNull(inside);
            Assert.Equal("The Black Cat", inside.GroupLabel);
            Assert.Equal(GroupInstance, inside.Roster.Value!.InstanceId);
            Assert.Equal(Freshness.Fresh, inside.Freshness);
            Assert.Contains(inside.Roster.Value.Members, m => m.Standing == RosterStanding.Flagged);

            // Rendered from the cache rather than from the response: the screen is a snapshot with
            // its own age already worked out, which is what lets the overlay keep showing
            // something useful when the server stops answering.
            Assert.Equal("up to date", inside.Roster.Describe());
        });
    }

    [Fact]
    public void AFlaggedArrivalInThisRoomBecomesACardAndOneInAnotherRoomDoesNot()
    {
        Run(session =>
        {
            // Two alerts waiting: one for a different instance of the same group, one for the
            // instance the moderator will be standing in. The server is meant to have scoped these
            // already; the client filters again anyway, because it cannot verify that it did.
            session.Reads.Alerts.Enqueue(new FlaggedJoinAlert(
                "a1", "usr_elsewhere", "Somebody Else", "11111", "2 prior moderation actions", 2,
                DateTimeOffset.UnixEpoch));

            session.Reads.Alerts.Enqueue(new FlaggedJoinAlert(
                "a2", "usr_trouble", "Trouble", GroupInstance, "2 prior moderation actions", 2,
                DateTimeOffset.UnixEpoch));

            var shown = new List<string>();

            Replay(session, () =>
            {
                if (session.Screen.Last.Alert is { } alert
                    && !shown.Contains(alert.AlertId, StringComparer.Ordinal))
                {
                    shown.Add(alert.AlertId);
                }
            });

            Assert.Equal(["a2"], shown);
        });
    }

    [Fact]
    public void TheOverlayStopsDrawingOnceTheScreenHasSettled()
    {
        // The property that makes the overlay affordable beside a game using eight to twelve
        // gigabytes: a submitted texture is re-projected by the compositor at the headset's own
        // rate with the application uninvolved, so the cost that matters is per change, not per
        // frame. Joining the log up to the overlay must not have cost that.
        Run(session =>
        {
            Replay(session);

            var settled = session.Renderer.Renders;
            for (var i = 0; i < 200; i++)
                Turn(session);

            Assert.Equal(settled, session.Renderer.Renders);
        });
    }

    [Fact]
    public void AnUncleanExitTakesTheOverlayBackToIdleRatherThanLeavingItOnALiveInstance()
    {
        // The session the fixture records ends with no OnLeftRoom and no disconnect line: VRChat
        // was killed. Rejoin the group instance so there is something to lose, then let the log
        // stop the way a real one does, and the overlay has to stop speaking for that server.
        Run(session =>
        {
            Replay(session);

            File.AppendAllLines(LogPath,
            [
                $"2026.09.03 21:35:00 Debug      -  [Behaviour] Joining {GroupWorld}:{GroupInstance}"
                + $"~group({GroupId})~groupAccessType(public)~region(use)",
                $"2026.09.03 21:35:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ ({LocalUserId})",
            ]);

            Turn(session);
            Assert.Equal("blackcat", session.Driver.CurrentServer?.ServerId);

            // And then nothing: no teardown, no marker, just a log that stops. Left to itself the
            // client would go on believing the moderator was standing there for as long as it ran.
            session.Clock.Advance(PresenceObserver.InstanceStaleAfter + TimeSpan.FromSeconds(1));
            Turn(session);

            Assert.Null(session.Driver.CurrentServer);
            Assert.Null(session.Screen.Last.GroupLabel);
        });
    }
}
