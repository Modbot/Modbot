using Modbot.Core.Email;

namespace Modbot.Core.Tests.Email;

/// <summary>The daily email limit's rules on their own (accounts and access design §4.4).</summary>
public class EmailLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static List<DateTimeOffset> SentMinutesAgo(int count, int startMinutesAgo = 120)
        => [.. Enumerable.Range(0, count).Select(i => Now - TimeSpan.FromMinutes(startMinutesAgo - i))];

    [Fact]
    public void OtherEmail_MayUseTheLimitLessTwenty_AndAccountEmailTheWholeLimit()
    {
        Assert.Equal(80, EmailLimit.RoomFor(EmailKind.Other, 100));
        Assert.Equal(100, EmailLimit.RoomFor(EmailKind.Account, 100));
        Assert.Equal(EmailLimit.KeptForAccountEmails, EmailLimit.RoomFor(EmailKind.Account, 100) - EmailLimit.RoomFor(EmailKind.Other, 100));
    }

    [Fact]
    public void AtTheMinimum_OtherEmailHasNoRoom_AndALowerLimitCountsAsTheMinimum()
    {
        Assert.Equal(0, EmailLimit.RoomFor(EmailKind.Other, EmailLimit.Minimum));
        Assert.Equal(EmailLimit.Minimum, EmailLimit.RoomFor(EmailKind.Account, 5));

        var (queue, at) = EmailLimit.WhenCanSend([], [], EmailKind.Other, EmailLimit.Minimum, Now);
        Assert.True(queue);
        Assert.Null(at);
    }

    [Fact]
    public void WithRoomAndNothingAhead_ItGoesNow()
    {
        var (queue, at) = EmailLimit.WhenCanSend(SentMinutesAgo(79), [], EmailKind.Other, 100, Now);

        Assert.False(queue);
        Assert.Equal(Now, at);
    }

    [Fact]
    public void OtherEmail_QueuesAtLimitLessTwenty_WhileAccountEmailStillGoes()
    {
        var sent = SentMinutesAgo(80);

        Assert.True(EmailLimit.WhenCanSend(sent, [], EmailKind.Other, 100, Now).Queue);
        Assert.False(EmailLimit.WhenCanSend(sent, [], EmailKind.Account, 100, Now).Queue);
    }

    [Fact]
    public void AtTheLimit_ItGoesWhenTheOldestSendTurnsADayOld()
    {
        var sent = SentMinutesAgo(100);

        var (queue, at) = EmailLimit.WhenCanSend(sent, [], EmailKind.Account, 100, Now);

        Assert.True(queue);
        Assert.Equal(sent.Min() + TimeSpan.FromHours(24), at);
    }

    [Fact]
    public void SendsOlderThanADay_DoNotCount()
    {
        var old = Enumerable.Range(0, 100).Select(i => Now - TimeSpan.FromHours(25) - TimeSpan.FromMinutes(i));

        Assert.False(EmailLimit.WhenCanSend(old, [], EmailKind.Other, 100, Now).Queue);
    }

    [Fact]
    public void SomethingAhead_QueuesEvenWithRoom_AndWaitsBehindIt()
    {
        var sent = SentMinutesAgo(100);

        var (_, first) = EmailLimit.WhenCanSend(sent, [], EmailKind.Account, 100, Now);
        var (queue, second) = EmailLimit.WhenCanSend(sent, [EmailKind.Account], EmailKind.Account, 100, Now);

        Assert.True(queue);
        Assert.True(second > first);
        Assert.Equal(sent.Order().ElementAt(1) + TimeSpan.FromHours(24), second);
    }
}
