using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Time;

public class ServerClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 20, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Start);

    private ServerClock NewClock() => new(_clock);

    /// <summary>One probe: the client asks at t0, the server answers, the reply lands at t3.</summary>
    private ClockSample Sample(TimeSpan trueOffset, TimeSpan roundTrip)
    {
        var sentAt = _clock.UtcNow;
        _clock.Advance(roundTrip);
        var receivedAt = _clock.UtcNow;
        var serverTime = sentAt + (roundTrip / 2) + trueOffset;

        return new ClockSample(sentAt, serverTime, receivedAt);
    }

    [Fact]
    public void WithNoSamplesThereIsNoCorrectionAndNoConfidenceInIt()
    {
        var clock = NewClock();

        Assert.Equal(TimeSpan.Zero, clock.Offset);
        Assert.Equal(ClockConfidence.Unknown, clock.Confidence);
    }

    [Fact]
    public void RecoversTheOffsetFromTheRoundTrip()
    {
        var clock = NewClock();
        var offset = TimeSpan.FromMilliseconds(-412);

        for (var i = 0; i < 5; i++)
            clock.Add(Sample(offset, TimeSpan.FromMilliseconds(40)));

        Assert.Equal(-412, clock.Offset.TotalMilliseconds, 1);
        Assert.Equal(ClockConfidence.Good, clock.Confidence);
    }

    [Fact]
    public void ASingleSampleIsUsedButNotTrusted()
    {
        // Pairing hands over a server time and the first batch should already be corrected. One
        // measurement is still one measurement, and the server is told so.
        var clock = NewClock();
        clock.Add(Sample(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(30)));

        Assert.Equal(3, clock.Offset.TotalSeconds, 1);
        Assert.Equal(ClockConfidence.Poor, clock.Confidence);
    }

    [Fact]
    public void ASlowRoundTripIsDiscardedRatherThanAveragedIn()
    {
        // A request that sat in a queue for two seconds says almost nothing about the offset,
        // because the delay was not symmetric. Averaging it in drags the estimate towards the
        // asymmetry; NTP's answer is to keep the fastest samples and this does the same.
        var clock = NewClock();
        var truth = TimeSpan.FromMilliseconds(200);

        for (var i = 0; i < 4; i++)
            clock.Add(Sample(truth, TimeSpan.FromMilliseconds(40)));

        // One badly delayed probe whose apparent offset is wildly wrong.
        clock.Add(new ClockSample(
            _clock.UtcNow,
            _clock.UtcNow + TimeSpan.FromSeconds(9),
            _clock.UtcNow + TimeSpan.FromSeconds(8)));

        Assert.Equal(200, clock.Offset.TotalMilliseconds, 5);
    }

    [Fact]
    public void OneGoodSampleAmongJunkStillWins()
    {
        var clock = NewClock();

        for (var i = 0; i < 4; i++)
            clock.Add(new ClockSample(
                _clock.UtcNow,
                _clock.UtcNow + TimeSpan.FromSeconds(4),
                _clock.UtcNow + TimeSpan.FromSeconds(6)));

        clock.Add(Sample(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20)));

        Assert.Equal(150, clock.Offset.TotalMilliseconds, 5);
    }

    [Fact]
    public void OnlyTheRecentSamplesCount()
    {
        var clock = NewClock();

        for (var i = 0; i < ServerClock.SampleWindow; i++)
            clock.Add(Sample(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(30)));

        // The machine's clock was stepped; the offset really did change.
        for (var i = 0; i < ServerClock.SampleWindow; i++)
            clock.Add(Sample(TimeSpan.FromSeconds(-5), TimeSpan.FromMilliseconds(30)));

        Assert.Equal(-5, clock.Offset.TotalSeconds, 1);
    }

    [Fact]
    public void ConfidenceFallsWhenTheSamplesDisagree()
    {
        var clock = NewClock();
        clock.Add(Sample(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30)));
        clock.Add(Sample(TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(30)));
        clock.Add(Sample(TimeSpan.FromSeconds(-4), TimeSpan.FromMilliseconds(30)));

        Assert.NotEqual(ClockConfidence.Good, clock.Confidence);
    }

    [Fact]
    public void ConfidenceDecaysWhenTheMeasurementGetsOld()
    {
        // A correction measured this morning says little about this evening: laptops sleep, VMs
        // migrate, NTP steps. The server is told the estimate is stale rather than left to assume
        // it is fresh.
        var clock = NewClock();
        for (var i = 0; i < 5; i++)
            clock.Add(Sample(TimeSpan.FromMilliseconds(-412), TimeSpan.FromMilliseconds(40)));

        Assert.Equal(ClockConfidence.Good, clock.Confidence);

        _clock.Advance(ServerClock.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.Equal(ClockConfidence.Poor, clock.Confidence);
    }

    [Fact]
    public void CorrectsAnInstantIntoServerTime()
    {
        var clock = NewClock();
        for (var i = 0; i < 5; i++)
            clock.Add(Sample(TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(20)));

        var observed = new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero);

        Assert.Equal(observed.AddSeconds(90), clock.ToServerTime(observed));
    }

    [Fact]
    public void NeverTouchesTheMachineClock()
    {
        // The offset is applied to reported timestamps and nothing else. Stepping a moderator's
        // system clock to suit Modbot would be indefensible on a personal PC.
        var before = _clock.UtcNow;
        var clock = NewClock();
        clock.Add(Sample(TimeSpan.FromHours(2), TimeSpan.FromMilliseconds(20)));

        Assert.Equal(before + TimeSpan.FromMilliseconds(20), _clock.UtcNow);
    }

    [Theory]
    [InlineData(ClockConfidence.Unknown, "unknown")]
    [InlineData(ClockConfidence.Poor, "poor")]
    [InlineData(ClockConfidence.Fair, "fair")]
    [InlineData(ClockConfidence.Good, "good")]
    public void ConfidenceGoesOnTheWireInTheProtocolsSpelling(ClockConfidence confidence, string wire)
    {
        Assert.Equal(wire, confidence.ToWire());
    }
}
