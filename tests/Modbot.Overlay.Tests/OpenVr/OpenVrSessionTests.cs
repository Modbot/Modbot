using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// Two overlays, one attachment. OpenVR's init and shutdown are process-wide, so a second
/// <see cref="OpenVrOverlayRuntime"/> that did its own would land on top of the first and pull the
/// function table out from under it on the way down. These pin the counting and the generation
/// that make sharing safe -- on a machine with no SteamVR, which is where CI runs.
/// </summary>
[Collection(OpenVrCollection.Name)]
public class OpenVrSessionTests
{
    [Fact]
    public void AMachineWithoutSteamVrGetsAStateRatherThanAnException()
    {
        Assert.SkipWhen(OpenVrSession.IsSteamVrInstalled, "This says what happens with no SteamVR; this PC has one.");

        var session = new OpenVrSession();

        var status = session.Open();

        Assert.NotEqual(OverlayRuntimeState.Running, status.State);
        Assert.False(session.IsOpen);
        Assert.Equal(0, session.Users);
    }

    [Fact]
    public void ARefusedAttachmentCountsNobody()
    {
        Assert.SkipWhen(OpenVrSession.IsSteamVrInstalled, "This says what happens with no SteamVR; this PC has one.");

        // Only a Running answer holds the session open, so a client that keeps asking every ten
        // seconds does not run the count up to thousands while SteamVR is closed.
        var session = new OpenVrSession();

        session.Open();
        session.Open();
        session.Open();

        Assert.Equal(0, session.Users);
    }

    [Fact]
    public void ClosingMovesTheGenerationSoOtherOverlaysKnowTheirHandlesAreGone()
    {
        // The generation is the whole safety argument for two overlays: when one hears SteamVR
        // quit it closes the attachment for everybody, and the other one's next Poll sees this
        // number has moved and drops its handle rather than calling DestroyOverlay through a
        // function table that has been shut down.
        var session = new OpenVrSession();
        var before = session.Generation;

        session.Close();

        Assert.NotEqual(before, session.Generation);
        Assert.Equal(0, session.Users);
    }

    [Fact]
    public void ReleasingMoreThanWasTakenDoesNotGoNegative()
    {
        var session = new OpenVrSession();

        session.Release();
        session.Release();

        Assert.Equal(0, session.Users);
    }

    [Fact]
    public void TheTwoOverlaysHaveTheirOwnKeys()
    {
        // SteamVR stores the moderator's own position and curvature adjustments against these
        // strings. Sharing one would mean the two panels fighting over the same saved position;
        // changing either discards what a moderator has set.
        using var main = new OpenVrOverlayRuntime();
        using var notifications = new OpenVrOverlayRuntime(OverlayKind.Notification);

        Assert.Equal("moe.bin.modbot.overlay", main.Key);
        Assert.Equal("moe.bin.modbot.notifications", notifications.Key);
        Assert.NotEqual(main.Key, notifications.Key);
        Assert.Equal(OverlayKind.Main, main.Kind);
        Assert.Equal(OverlayKind.Notification, notifications.Kind);
    }

    [Fact]
    public void BothOverlaysSurviveAMachineWithNoHeadset()
    {
        Assert.SkipWhen(OpenVrSession.IsSteamVrInstalled, "This says what happens with no SteamVR; this PC has one.");

        var session = new OpenVrSession();
        using var main = new OpenVrOverlayRuntime(OverlayKind.Main, session: session);
        using var notifications = new OpenVrOverlayRuntime(OverlayKind.Notification, session: session);

        var first = main.Start();
        var second = notifications.Start();

        Assert.NotEqual(OverlayRuntimeState.Running, first.State);
        Assert.NotEqual(OverlayRuntimeState.Running, second.State);

        // Neither is holding anything, so neither can take anything away from the other.
        main.Poll();
        notifications.Poll();
        Assert.Equal(0, session.Users);
    }

    [Fact]
    public void DisposingOneOverlayDoesNotLetGoOfWhatTheOtherHolds()
    {
        Assert.SkipWhen(OpenVrSession.IsSteamVrInstalled, "This says what happens with no SteamVR; this PC has one.");

        var session = new OpenVrSession();
        var main = new OpenVrOverlayRuntime(OverlayKind.Main, session: session);
        using var notifications = new OpenVrOverlayRuntime(OverlayKind.Notification, session: session);

        main.Start();
        notifications.Start();
        var generation = session.Generation;

        main.Dispose();

        // With no SteamVR nothing was held either way; what matters is that disposing one overlay
        // never closes the attachment behind the other's back.
        Assert.Equal(generation, session.Generation);
    }
}
