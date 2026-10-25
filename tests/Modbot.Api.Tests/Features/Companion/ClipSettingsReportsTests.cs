using Modbot.Api.Features.Companion.Alerts;
using Modbot.Shared.Clips;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// What a companion tells its server about its Clips settings, and the one question asked of it:
/// is a clip for this moderator's actions to be counted on (automatic roles, 2026-10-24).
/// </summary>
public class ClipSettingsReportsTests
{
    [Theory]
    [InlineData(true, true, 10, true, true)]
    [InlineData(true, true, 9, true, false)]
    [InlineData(false, true, 10, true, false)]
    [InlineData(true, false, 10, true, false)]
    [InlineData(true, true, 10, false, false)]
    public void IsReady_NeedsAllFour(bool on, bool recording, int minutes, bool sendForActions, bool expected)
        => Assert.Equal(expected, new ClipSettingsReport(on, recording, minutes, sendForActions).IsReady);

    [Fact]
    public void ADeviceThatNeverReported_HasNoReport_AndIsNotReady()
    {
        var reports = new ClipSettingsReports();
        var device = Guid.NewGuid();

        Assert.Null(reports.For(device));
        Assert.False(reports.IsReady([device]));
    }

    [Fact]
    public void ALaterReport_ReplacesTheEarlierOne()
    {
        var reports = new ClipSettingsReports();
        var device = Guid.NewGuid();

        reports.Record(device, new ClipSettingsReport(true, true, 10, true));
        reports.Record(device, new ClipSettingsReport(true, true, 10, false));

        Assert.False(reports.IsReady([device]));
    }

    [Fact]
    public void IsReady_WhenAnyOfSeveralDevicesIs()
    {
        var reports = new ClipSettingsReports();
        var ready = Guid.NewGuid();
        var notReady = Guid.NewGuid();
        var silent = Guid.NewGuid();

        reports.Record(ready, new ClipSettingsReport(true, true, 10, true));
        reports.Record(notReady, new ClipSettingsReport(true, false, 10, true));

        Assert.True(reports.IsReady([silent, notReady, ready]));
        Assert.False(reports.IsReady([silent, notReady]));
    }
}
