using Modbot.Core.Time;

namespace Modbot.Core.Tests.Time;

public class SystemModbotClockTests
{
    [Fact]
    public void UtcNow_IsUtc()
    {
        var clock = new SystemModbotClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public void UtcNow_AdvancesBetweenReads()
    {
        var clock = new SystemModbotClock();

        var first = clock.UtcNow;
        Thread.Sleep(2);
        var second = clock.UtcNow;

        Assert.True(second >= first, $"clock went backwards: {second} < {first}");
    }
}
