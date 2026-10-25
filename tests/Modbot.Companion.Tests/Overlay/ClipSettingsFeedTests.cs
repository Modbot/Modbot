using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;
using Modbot.Shared.Clips;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>The Clips settings the companion tells its server (automatic roles, 2026-10-24).</summary>
public class ClipSettingsFeedTests
{
    private static readonly ClipSettingsReport Ready = new(true, true, 10, true);

    [Fact]
    public void Set_ReportsAChange_OnlyWhenTheReportDiffers()
    {
        var feed = new ClipSettingsFeed();
        var raised = new List<ClipSettingsReport>();
        feed.Changed += raised.Add;

        Assert.Null(feed.Current);
        Assert.True(feed.Set(Ready));
        Assert.False(feed.Set(Ready with { }));
        Assert.True(feed.Set(Ready with { Recording = false }));

        Assert.Equal(2, raised.Count);
        Assert.Equal(Ready with { Recording = false }, feed.Current);
    }

    [Fact]
    public void TheLivePoll_CarriesTheFourValues_OnlyWhenThereIsAReport()
    {
        var pairing = new ServerPairing("cats", new Uri("https://modbot.example"), "token", "grp_cats");

        Assert.DoesNotContain("clips", pairing.LivePollEndpoint("Main", null, 20).Query);
        Assert.EndsWith(
            "&wait=20&clipsOn=true&clipsRecording=false&clipsMinutes=10&clipsSendForActions=true",
            pairing.LivePollEndpoint("Main", null, 20, null, new ClipSettingsReport(true, false, 10, true)).Query);
    }
}
