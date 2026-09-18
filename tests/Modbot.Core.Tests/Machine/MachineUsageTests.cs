using Modbot.Core.Machine;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Machine;

/// <summary>
/// Turning running totals into rates, keeping the window small, and saying nothing rather than
/// something wrong on a host that does not carry a counter.
/// </summary>
public class MachineUsageSamplerTests
{
    private static MachineCounters Counters(
        double processorSeconds = 0,
        long memoryBytes = 100,
        long? memoryLimitBytes = null,
        long? diskReadBytes = null,
        long? diskWrittenBytes = null) =>
        new(
            TimeSpan.FromSeconds(processorSeconds),
            memoryBytes,
            memoryLimitBytes,
            diskReadBytes,
            diskWrittenBytes);

    [Fact]
    public void TheFirstReadingIsOnlyABaseline()
    {
        var sampler = new MachineUsageSampler(new FakeClock());

        Assert.Null(sampler.Take(Counters()));
        Assert.Empty(sampler.Points);
    }

    [Fact]
    public void ProcessorUseIsWorkedOutAcrossTwoMoments()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock) { Processors = 2 };

        sampler.Take(Counters(processorSeconds: 12));
        clock.Advance(TimeSpan.FromSeconds(10));

        // Four processor-seconds over ten seconds of two processors is a fifth of the machine.
        var point = sampler.Take(Counters(processorSeconds: 16));

        Assert.NotNull(point);
        Assert.Equal(20, point.ProcessorPercent!.Value, 6);
        Assert.Equal(clock.UtcNow, point.At);
    }

    [Fact]
    public void ProcessorUseNeverLeavesWhatAProcessorCanDo()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock) { Processors = 1 };

        sampler.Take(Counters(processorSeconds: 0));
        clock.Advance(TimeSpan.FromSeconds(10));

        // More processor time than wall-clock time on one processor: a reading that straddled a
        // step in either counter, not a machine at 200%.
        var point = sampler.Take(Counters(processorSeconds: 20));

        Assert.Equal(100, point!.ProcessorPercent!.Value, 6);
    }

    [Fact]
    public void DiskActivityIsBytesASecondBetweenTheTwoReadings()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock);

        sampler.Take(Counters(diskReadBytes: 1_000, diskWrittenBytes: 2_000));
        clock.Advance(TimeSpan.FromSeconds(10));

        var point = sampler.Take(Counters(diskReadBytes: 21_000, diskWrittenBytes: 2_000));

        Assert.Equal(2_000, point!.DiskReadBytesPerSecond!.Value, 6);
        Assert.Equal(0, point.DiskWrittenBytesPerSecond!.Value, 6);
    }

    [Fact]
    public void ACounterThatFellIsNothingRatherThanANegativeRate()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock);

        sampler.Take(Counters(diskReadBytes: 5_000));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(sampler.Take(Counters(diskReadBytes: 4_000))!.DiskReadBytesPerSecond);
    }

    [Fact]
    public void AFigureTheHostDoesNotCarryStaysNull()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock);

        sampler.Take(new MachineCounters(null, null, null, null, null));
        clock.Advance(TimeSpan.FromSeconds(10));

        var point = sampler.Take(new MachineCounters(null, null, null, null, null));

        Assert.NotNull(point);
        Assert.Null(point.ProcessorPercent);
        Assert.Null(point.MemoryBytes);
        Assert.Null(point.DiskReadBytesPerSecond);
        Assert.Null(point.DiskWrittenBytesPerSecond);
    }

    [Fact]
    public void AClockThatHasNotMovedProducesNoPoint()
    {
        var sampler = new MachineUsageSampler(new FakeClock());

        sampler.Take(Counters(processorSeconds: 1));

        Assert.Null(sampler.Take(Counters(processorSeconds: 2)));
        Assert.Empty(sampler.Points);
    }

    [Fact]
    public void TheWindowStaysBoundedAndDropsTheOldest()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock);
        var start = clock.UtcNow;

        sampler.Take(Counters());

        // Twenty more readings than the window holds: the count stops at the capacity and the
        // oldest twenty are gone, so a server running for a week costs the same as one just
        // started.
        for (var i = 1; i <= MachineUsageSampler.Capacity + 20; i++)
        {
            clock.Advance(MachineUsageSampler.Every);
            sampler.Take(Counters(processorSeconds: i));
        }

        var points = sampler.Points;

        Assert.Equal(MachineUsageSampler.Capacity, points.Count);
        Assert.Equal(clock.UtcNow, points[^1].At);
        Assert.Equal(start + MachineUsageSampler.Every * 21, points[0].At);
    }

    [Fact]
    public void TheWindowIsHalfAnHourOfTenSecondReadings()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), MachineUsageSampler.Every);
        Assert.Equal(TimeSpan.FromMinutes(30), MachineUsageSampler.Window);
        Assert.Equal(180, MachineUsageSampler.Capacity);
    }

    [Fact]
    public void TheMemoryLimitIsWhateverTheLastReadingSaid()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock);

        Assert.Null(sampler.MemoryLimitBytes);

        sampler.Take(Counters(memoryLimitBytes: 2_147_483_648));

        Assert.Equal(2_147_483_648, sampler.MemoryLimitBytes);
    }
}

/// <summary>
/// Reading the files the runtime has no API for, on hosts that may not have them.
/// </summary>
public class MachineReaderTests
{
    /// <summary>A real <c>/proc/self/io</c>, lines and all.</summary>
    private const string ProcSelfIo = """
        rchar: 323934931
        wchar: 323929600
        syscr: 632687
        syscw: 632675
        read_bytes: 22102016
        write_bytes: 2314240
        cancelled_write_bytes: 1024

        """;

    [Fact]
    public void DiskCountersAreTheBytesThatReachedTheDisk()
    {
        var (read, written) = MachineReader.ParseDiskCounters(ProcSelfIo);

        // Not rchar and wchar, which count every read and write call including the ones the page
        // cache answered, and not cancelled_write_bytes, which merely starts the same way.
        Assert.Equal(22_102_016, read);
        Assert.Equal(2_314_240, written);
    }

    [Fact]
    public void CountersThatAreNotThereAreNulls()
    {
        var (read, written) = MachineReader.ParseDiskCounters("rchar: 12\nsyscr: 4\n");

        Assert.Null(read);
        Assert.Null(written);
    }

    [Fact]
    public void AMissingDiskCountersFileIsNotAnError()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"modbot-no-such-file-{Guid.NewGuid():n}", "io");

        var (read, written) = MachineReader.DiskCounters(absent);

        Assert.Null(read);
        Assert.Null(written);
    }

    [Fact]
    public void AMissingControlGroupFileIsNotAnError()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"modbot-no-such-file-{Guid.NewGuid():n}", "memory.max");

        Assert.Null(MachineReader.MemoryLimitFrom(absent));
    }

    [Fact]
    public void AControlGroupLimitIsReadFromEitherVersion()
    {
        // Version 2 writes bytes, or the word max.
        Assert.Equal(536_870_912, MachineReader.ParseMemoryLimit("536870912\n"));
        Assert.Null(MachineReader.ParseMemoryLimit("max\n"));

        // Version 1 writes a number near the largest there is instead of saying there is no limit.
        Assert.Null(MachineReader.ParseMemoryLimit("9223372036854771712"));

        Assert.Null(MachineReader.ParseMemoryLimit(""));
        Assert.Null(MachineReader.ParseMemoryLimit("not a number"));
        Assert.Null(MachineReader.ParseMemoryLimit("0"));
    }

    [Fact]
    public void ReadingWorksOnWhateverHostTheTestsRunOn()
    {
        // Every figure is allowed to be null -- that is the whole point on a host that does not
        // carry it -- but nothing may throw, on any platform.
        var counters = MachineReader.Read();

        if (counters.ProcessorTime is { } used)
            Assert.True(used >= TimeSpan.Zero);

        if (counters.MemoryBytes is { } held)
            Assert.True(held > 0);

        if (counters.MemoryLimitBytes is { } limit)
            Assert.True(limit > 0);
    }
}
