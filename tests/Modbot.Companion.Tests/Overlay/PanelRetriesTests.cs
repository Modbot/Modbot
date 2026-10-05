using System.Runtime.InteropServices;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// A headset panel that could not start: caught whatever it threw, shown as its status, tried
/// again at most once a minute, and given up after five tries.
/// </summary>
/// <remarks>
/// The bug behind these: a graphics card out of memory threw a <c>SharpGenException</c> from the
/// start site, which caught only three named types, so the whole companion stopped with a crash
/// box. A COM error stands in for it here, because that is what the same failure is when it does
/// not come through SharpGen, and this project does not reference SharpGen.
/// </remarks>
public class PanelRetriesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static Exception OutOfMemory() => new COMException("Not enough memory resources are available.", unchecked((int)0x8007000E));

    [Fact]
    public void AStartSiteThatThrowsAGraphicsErrorKeepsTheSwitchRunningAndShowsWhy()
    {
        var retries = new PanelRetries();

        // Shaped as the companion's start sites are: catch everything but a cancel.
        var panel = new OverlaySwitch(
            () =>
            {
                try
                {
                    throw OutOfMemory();
                }
                catch (Exception ex) when (PanelRetries.IsPanelFailure(ex))
                {
                    retries.Failed(ex, Start);
                }
            },
            () => { });

        panel.StartIfOn();

        Assert.True(panel.Running);
        Assert.True(retries.Failing);

        var status = OverlayStatus.CouldNotStart(retries.Status);
        Assert.Equal(OverlayStatus.CouldNotStartState, status.State);
        Assert.Equal("Could not start: the graphics card ran out of memory.", status.Detail);
        Assert.False(status.Attached);

        var notify = NotifyOverlayStatus.CouldNotStart(retries.Status);
        Assert.Equal(OverlayStatus.CouldNotStartState, notify.State);
    }

    [Fact]
    public void EveryFailureIsCaughtButACancel()
    {
        Assert.True(PanelRetries.IsPanelFailure(OutOfMemory()));
        Assert.True(PanelRetries.IsPanelFailure(new InvalidOperationException()));
        Assert.True(PanelRetries.IsPanelFailure(new DllNotFoundException()));
        Assert.True(PanelRetries.IsPanelFailure(new Exception("anything")));
        Assert.False(PanelRetries.IsPanelFailure(new OperationCanceledException()));
        Assert.False(PanelRetries.IsPanelFailure(new TaskCanceledException()));
    }

    [Fact]
    public void ATryComesNoSoonerThanAMinuteLater()
    {
        var retries = new PanelRetries();

        Assert.True(retries.Failed(OutOfMemory(), Start));

        Assert.False(retries.Due(Start));
        Assert.False(retries.Due(Start + TimeSpan.FromSeconds(59)));
        Assert.True(retries.Due(Start + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void FiveTriesInAllThenItIsGivenUp()
    {
        var retries = new PanelRetries();
        var now = Start;

        for (var attempt = 1; attempt < PanelRetries.MostTries; attempt++)
        {
            Assert.True(retries.Failed(OutOfMemory(), now));
            now += PanelRetries.Wait;
            Assert.True(retries.Due(now));
        }

        Assert.False(retries.Failed(OutOfMemory(), now));
        Assert.Equal(PanelRetries.MostTries, retries.Tries);
        Assert.True(retries.GaveUp);
        Assert.False(retries.Due(now + TimeSpan.FromHours(1)));

        // Still says why, so the page keeps showing it.
        Assert.True(retries.Failing);
    }

    [Fact]
    public void AMissingFileIsNotTriedAgain()
    {
        var retries = new PanelRetries();

        Assert.False(retries.Failed(new DllNotFoundException("openvr_api"), Start));
        Assert.True(retries.GaveUp);
        Assert.False(retries.Due(Start + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ClearingGivesEveryTryBack()
    {
        var retries = new PanelRetries();
        for (var attempt = 0; attempt < PanelRetries.MostTries; attempt++)
            retries.Failed(OutOfMemory(), Start);

        retries.Clear();

        Assert.False(retries.Failing);
        Assert.False(retries.GaveUp);
        Assert.Equal(0, retries.Tries);
        Assert.False(retries.Due(Start + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void TheReasonIsShortAndPlain()
    {
        Assert.Equal("the graphics card ran out of memory", PanelRetries.ShortReason(OutOfMemory()));
        Assert.Equal(
            "a file it needs is missing from the Modbot installation",
            PanelRetries.ShortReason(new DllNotFoundException("openvr_api")));
        Assert.Equal("SteamVR is not attached", PanelRetries.ShortReason(new InvalidOperationException("SteamVR is not attached.")));

        var longest = PanelRetries.ShortReason(new InvalidOperationException(new string('x', 500) + "\nsecond line"));
        Assert.True(longest.Length <= 121);
        Assert.DoesNotContain("second line", longest, StringComparison.Ordinal);
    }
}
