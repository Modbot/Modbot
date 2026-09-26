using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Scheduling;

/// <summary>
/// Spec 4.2.2: scheduling is relative and randomised, and measured on a monotonic source.
/// </summary>
public class DesyncedScheduleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    [Fact]
    public void TheOffsetIsSpreadAcrossTheWholeInterval()
    {
        var clock = new FakeMonotonicClock();

        // One "process" per seed. If every deployment started at phase zero they would all hit
        // VRChat on the same second, which is the failure this is guarding against -- so what
        // matters is the spread, not any one draw.
        var offsets = Enumerable.Range(0, 200)
            .Select(seed => new DesyncedSchedule(Interval, clock, new Random(seed)).Offset)
            .ToList();

        Assert.All(offsets, o => Assert.InRange(o, TimeSpan.Zero, Interval));

        var buckets = offsets
            .Select(o => (int)(o / Interval * 10))
            .Distinct()
            .Count();

        Assert.True(buckets >= 9, $"offsets only covered {buckets} of 10 phase buckets");
    }

    [Fact]
    public void OffsetsDifferPerSyncTypeWithinOneProcess()
    {
        var clock = new FakeMonotonicClock();
        var random = new Random(1234);

        var members = new DesyncedSchedule(Interval, clock, random);
        var bans = new DesyncedSchedule(Interval, clock, random);
        var auditLog = new DesyncedSchedule(TimeSpan.FromSeconds(8), clock, random);

        // Otherwise one server's own sync types fire together as a burst.
        Assert.NotEqual(members.Offset, bans.Offset);
        Assert.NotEqual(bans.Offset, auditLog.Offset);
    }

    [Fact]
    public void SuccessiveRunsAreOneIntervalApartPlusOrMinusJitter()
    {
        var clock = new FakeMonotonicClock();
        var schedule = new DesyncedSchedule(Interval, clock, new Random(7));

        var gaps = new List<TimeSpan>();
        var previous = TimeSpan.Zero;

        for (var i = 0; i < 50; i++)
        {
            var delay = schedule.NextDelay();
            clock.Advance(delay);

            if (i > 0)
                gaps.Add(clock.Elapsed - previous);

            previous = clock.Elapsed;
        }

        Assert.All(gaps, gap => Assert.InRange(gap, Interval * 0.8, Interval * 1.2));

        // Jitter is doing something: a schedule that settled into an exact comb would slowly
        // re-align with other servers, which is the point of having it at all.
        Assert.True(gaps.Distinct().Count() > 40);
    }

    [Fact]
    public void TheRateNeverExceedsOneRunPerIntervalOnAverage()
    {
        var clock = new FakeMonotonicClock();
        var schedule = new DesyncedSchedule(Interval, clock, new Random(11));

        const int runs = 500;
        for (var i = 0; i < runs; i++)
            clock.Advance(schedule.NextDelay());

        // Jitter is symmetric, so over many ticks the mean gap is the interval. A drift in either
        // direction would mean the pacing caps in spec 4.2 are not the caps they claim to be.
        var mean = clock.Elapsed / (runs - 1);
        Assert.InRange(mean, Interval * 0.98, Interval * 1.02);
    }

    [Fact]
    public void FallingBehindSkipsMissedSlotsRatherThanCatchingUp()
    {
        var clock = new FakeMonotonicClock();
        var schedule = new DesyncedSchedule(Interval, clock, new Random(3));

        schedule.NextDelay();

        // The container was paused, the machine slept, or a sync ran long.
        clock.Advance(TimeSpan.FromMinutes(5));

        // Every delay is a real wait: no burst of zero-length waits working through the backlog,
        // which is precisely the spike the pacing exists to prevent.
        for (var i = 0; i < 5; i++)
        {
            var delay = schedule.NextDelay();
            Assert.True(delay > TimeSpan.Zero, $"delay {i} was {delay}");
            Assert.True(delay <= Interval * 1.2);
            clock.Advance(delay);
        }
    }

    [Fact]
    public void TheDelayIsMeasuredPurelyFromElapsedTime()
    {
        // Two identical schedules -- same seed, so the same offset and the same jitter draws --
        // whose clocks differ by a known amount.
        var seed = Enumerable.Range(0, 100)
            .First(s => new DesyncedSchedule(Interval, new FakeMonotonicClock(), new Random(s)).Offset
                        > Interval * 0.5);

        var early = new FakeMonotonicClock();
        var late = new FakeMonotonicClock();
        var headstart = TimeSpan.FromMilliseconds(250);
        late.Advance(headstart);

        var a = new DesyncedSchedule(Interval, early, new Random(seed));
        var b = new DesyncedSchedule(Interval, late, new Random(seed));

        // The one whose process has been running longer waits exactly that much less. Nothing
        // else is consulted, so an NTP step or a DST change cannot move the schedule (spec 4.2.2).
        Assert.Equal(a.NextDelay() - headstart, b.NextDelay());
    }
}
