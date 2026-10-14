using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>How long Modbot waits before asking VRChat again for a date's interested count.</summary>
public class CalendarInterestRecorderTests
{
    [Fact]
    public void AfterAFailedRead_TheWaitDoublesFromFiveMinutes_ToAtMostTwenty()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CalendarInterestRecorder.BackOff(1));
        Assert.Equal(TimeSpan.FromMinutes(10), CalendarInterestRecorder.BackOff(2));
        Assert.Equal(TimeSpan.FromMinutes(20), CalendarInterestRecorder.BackOff(3));
        Assert.Equal(TimeSpan.FromMinutes(20), CalendarInterestRecorder.BackOff(8));
    }
}
