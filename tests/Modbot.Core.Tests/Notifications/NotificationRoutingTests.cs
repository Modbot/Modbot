using Modbot.Core.Data.Entities;
using Modbot.Core.Notifications;

namespace Modbot.Core.Tests.Notifications;

/// <summary>
/// The notification pipeline's rules, with no database and no clock (foundation §4.5.1).
/// </summary>
/// <remarks>
/// These are the decisions that decide whether a moderator is interrupted, so they are pinned
/// exactly rather than exercised through a pipeline that could be right by accident.
/// </remarks>
public class NotificationRoutingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NotificationSeverity.Critical, NotificationLevels.Critical, true)]
    [InlineData(NotificationSeverity.Warning, NotificationLevels.Critical, false)]
    [InlineData(NotificationSeverity.Information, NotificationLevels.Critical, false)]
    [InlineData(NotificationSeverity.Critical, NotificationLevels.Warning, true)]
    [InlineData(NotificationSeverity.Warning, NotificationLevels.Warning, true)]
    [InlineData(NotificationSeverity.Information, NotificationLevels.Warning, false)]
    [InlineData(NotificationSeverity.Critical, NotificationLevels.Everything, true)]
    [InlineData(NotificationSeverity.Warning, NotificationLevels.Everything, true)]
    [InlineData(NotificationSeverity.Information, NotificationLevels.Everything, true)]
    public void SeverityDecidesWhatGoesOutOnAChannel(
        NotificationSeverity severity, string level, bool expected)
        => Assert.Equal(expected, NotificationRouting.GoesNow(severity, level));

    [Fact]
    public void AChannelThatIsOffTakesNothingAtAll()
    {
        foreach (var severity in new[]
                 {
                     NotificationSeverity.Critical,
                     NotificationSeverity.Warning,
                     NotificationSeverity.Information,
                 })
        {
            Assert.False(NotificationRouting.GoesNow(severity, NotificationLevels.Off));
        }
    }

    /// <summary>
    /// Whatever the level silenced goes in the summary, not only information: "critical only" must
    /// not quietly mean "and forget everything else".
    /// </summary>
    [Fact]
    public void WhatTheLevelSilencedStillGoesInTheDailySummary()
    {
        Assert.True(NotificationRouting.GoesInSummary(
            NotificationSeverity.Warning, NotificationLevels.Critical, dailySummary: true));

        Assert.True(NotificationRouting.GoesInSummary(
            NotificationSeverity.Information, NotificationLevels.Critical, dailySummary: true));

        // Something already going out now is not also put in the summary.
        Assert.False(NotificationRouting.GoesInSummary(
            NotificationSeverity.Critical, NotificationLevels.Critical, dailySummary: true));

        // And with the summary off, nothing waits for one.
        Assert.False(NotificationRouting.GoesInSummary(
            NotificationSeverity.Information, NotificationLevels.Critical, dailySummary: false));
    }

    [Fact]
    public void TheSameThingInsideTheQuietTimeIsARepeat()
        => Assert.True(NotificationRouting.IsRepeat(
            NotificationSeverity.Warning,
            Noon,
            NotificationSeverity.Warning,
            Noon.AddHours(5),
            TimeSpan.FromHours(6)));

    [Fact]
    public void TheSameThingPastTheQuietTimeIsNotARepeat()
        => Assert.False(NotificationRouting.IsRepeat(
            NotificationSeverity.Warning,
            Noon,
            NotificationSeverity.Warning,
            Noon.AddHours(6),
            TimeSpan.FromHours(6)));

    /// <summary>
    /// A quiet time somebody set for a warning must not swallow the moment it becomes critical.
    /// </summary>
    [Fact]
    public void SomethingThatGotWorseIsNotARepeat()
        => Assert.False(NotificationRouting.IsRepeat(
            NotificationSeverity.Warning,
            Noon,
            NotificationSeverity.Critical,
            Noon.AddMinutes(1),
            TimeSpan.FromHours(6)));

    /// <summary>The other way round is the same problem, still there.</summary>
    [Fact]
    public void SomethingThatGotMilderIsStillARepeat()
        => Assert.True(NotificationRouting.IsRepeat(
            NotificationSeverity.Critical,
            Noon,
            NotificationSeverity.Warning,
            Noon.AddMinutes(1),
            TimeSpan.FromHours(6)));

    [Fact]
    public void AQuietTimeOfNoneRepeatsNothing()
        => Assert.False(NotificationRouting.IsRepeat(
            NotificationSeverity.Warning, Noon, NotificationSeverity.Warning, Noon, TimeSpan.Zero));

    [Fact]
    public void TheFirstDailySummaryWaitsADayFromTheOldestThingInIt()
    {
        Assert.False(NotificationRouting.SummaryDue(Noon, null, Noon.AddHours(23)));
        Assert.True(NotificationRouting.SummaryDue(Noon, null, Noon.AddHours(24)));
    }

    [Fact]
    public void LaterSummariesAreCountedFromTheLastOne()
    {
        var lastSummary = Noon;
        var oldestWaiting = Noon.AddHours(1);

        Assert.False(NotificationRouting.SummaryDue(oldestWaiting, lastSummary, Noon.AddHours(20)));
        Assert.True(NotificationRouting.SummaryDue(oldestWaiting, lastSummary, Noon.AddHours(24)));
    }

    [Fact]
    public void AQuietTimeIsKeptInsideItsBounds()
    {
        Assert.Equal(
            TimeSpan.FromHours(NotificationSettings.MaxQuietHours),
            NotificationRouting.QuietTime(new NotificationSettings { QuietHours = 10_000 }));

        Assert.Equal(TimeSpan.Zero, NotificationRouting.QuietTime(new NotificationSettings { QuietHours = -5 }));
    }

    [Fact]
    public void SeverityNamesSurviveARoundTrip()
    {
        foreach (var severity in new[]
                 {
                     NotificationSeverity.Critical,
                     NotificationSeverity.Warning,
                     NotificationSeverity.Information,
                 })
        {
            var name = NotificationRouting.NameOf(severity);

            Assert.True(NotificationSeverities.IsKnown(name));
            Assert.Equal(severity, NotificationRouting.SeverityOf(name));
        }

        // Anything a newer Modbot wrote and this one does not understand is treated as the
        // quietest thing there is, never as the loudest.
        Assert.Equal(NotificationSeverity.Information, NotificationRouting.SeverityOf("something-new"));
    }

    /// <summary>
    /// The defaults are the quiet ones §4.5.1 asks for: nobody has to opt out of anything.
    /// </summary>
    [Fact]
    public void TheDefaultsAreQuiet()
    {
        var (emailLevel, emailSummary) = NotificationChannels.Default(NotificationChannels.Email);

        Assert.Equal(NotificationLevels.Critical, emailLevel);
        Assert.True(emailSummary);

        var (discordLevel, discordSummary) = NotificationChannels.Default(NotificationChannels.Discord);

        Assert.Equal(NotificationLevels.Warning, discordLevel);
        Assert.False(discordSummary);
    }
}
