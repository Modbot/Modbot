using Avalonia.Controls;
using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The drive loop through the real <see cref="OverlayHost"/>, counting actual rasterisations.
/// </summary>
/// <remarks>
/// <see cref="OverlayDriverTests"/> checks the loop against a presenter that reimplements the
/// no-change gate, which proves the loop does not force a redraw but not that the gate itself
/// holds. This closes that circle: the same ticks run through the host that really builds the
/// visual tree, and the assertion is on the renderer — the thing that costs 1.3 ms and a core
/// beside a game already using eight to twelve gigabytes.
/// </remarks>
public class DriverThroughRealHostTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    /// <summary>Counts every real layout-and-rasterise. The number that matters.</summary>
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

        public int Uploads { get; private set; }

        public void Upload(ReadOnlySpan<byte> bgra) => Uploads++;

        public void Dispose() { }
    }

    private sealed class QuietReads : IOverlayReadClient
    {
        public Queue<ReadResult<InstanceContext>> Contexts { get; } = new();

        public Task<ReadResult<InstanceContext>> GetContextAsync(
            ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => Task.FromResult(Contexts.Count > 0
                ? Contexts.Dequeue()
                : new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(
            ServerPairing pairing, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<FlaggedJoinAlert>(
                ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(30)));

        public Task<ReadResult<UserSummary>> GetUserAsync(
            ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static InstanceLocation Location()
    {
        Assert.True(InstanceLocation.TryParse(
            $"wrld_4b34:{Instance}~group({Group})~groupAccessType(members)~region(use)", out var location));

        return location;
    }

    private static InstanceContext Roster(params string[] names) => new(
        Instance,
        [.. names.Select(n => new RosterMember($"usr_{n}", n, RosterStanding.Ordinary, 0, []))]);

    [Fact]
    public void TheRealRendererIsNotCalledWhenNothingChanges()
    {
        AvaloniaTestHost.Run(() =>
        {
            var clock = new FakeClock();
            var reads = new QuietReads();
            var renderer = new CountingRenderer();
            var surface = new FakeSurface();

            using var host = new OverlayHost(new HeadlessOverlayRuntime(), surface, renderer);
            using var driver = new OverlayDriver(host, reads, clock);

            driver.Add(
                new ServerPairing("cats", new Uri("https://cats.example"), "token", Group),
                "Cat Lounge");

            driver.EnteredInstance(Location());
            reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));

            // The reads are already-completed tasks, so nothing here blocks the Avalonia thread.
            driver.TickAsync().GetAwaiter().GetResult();

            var afterFirst = renderer.Renders;
            Assert.True(afterFirst >= 1, "the first tick should have drawn something");

            for (var i = 0; i < 300; i++)
                driver.TickAsync().GetAwaiter().GetResult();

            Assert.Equal(afterFirst, renderer.Renders);
            Assert.Equal(afterFirst, surface.Uploads);
        });
    }

    [Fact]
    public void TheRealRendererIsCalledOnceWhenTheRosterChanges()
    {
        AvaloniaTestHost.Run(() =>
        {
            var clock = new FakeClock();
            var reads = new QuietReads();
            var renderer = new CountingRenderer();
            var surface = new FakeSurface();

            using var host = new OverlayHost(new HeadlessOverlayRuntime(), surface, renderer);
            using var driver = new OverlayDriver(host, reads, clock);

            driver.Add(
                new ServerPairing("cats", new Uri("https://cats.example"), "token", Group),
                "Cat Lounge");

            driver.EnteredInstance(Location());
            reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));
            driver.TickAsync().GetAwaiter().GetResult();

            var before = renderer.Renders;

            reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin", "Mei")));
            clock.Advance(OverlayDriver.ContextRefreshInterval);
            driver.TickAsync().GetAwaiter().GetResult();

            Assert.Equal(before + 1, renderer.Renders);
            Assert.Equal(renderer.Renders, surface.Uploads);
        });
    }

    [Fact]
    public void AFrameReachesTheHeadsetRuntimeWhenOneIsDrawn()
    {
        AvaloniaTestHost.Run(() =>
        {
            var clock = new FakeClock();
            var reads = new QuietReads();
            var runtime = new HeadlessOverlayRuntime();

            using var host = new OverlayHost(runtime, new FakeSurface(), new CountingRenderer());
            using var driver = new OverlayDriver(host, reads, clock);

            driver.Add(
                new ServerPairing("cats", new Uri("https://cats.example"), "token", Group),
                "Cat Lounge");

            driver.EnteredInstance(Location());
            reads.Contexts.Enqueue(new ReadResult<InstanceContext>(ReadOutcome.Fetched, Roster("Rin")));

            driver.TickAsync().GetAwaiter().GetResult();
            Assert.Equal(1, runtime.Submissions);

            // And nothing more is submitted while the screen stands still. The compositor keeps
            // re-projecting the texture it already has, at the headset's own rate.
            for (var i = 0; i < 50; i++)
                driver.TickAsync().GetAwaiter().GetResult();

            Assert.Equal(1, runtime.Submissions);
        });
    }
}
