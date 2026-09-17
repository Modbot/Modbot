using System.Numerics;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenXr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The handoff from the frame thread to the UI thread: nothing before the first publish, the
/// latest publish afterwards, and never half of one frame and half of the next.
/// </summary>
public class LatestTrackingTests
{
    /// <summary>A frame in which every number is the same value, so a torn read shows as a mismatch.</summary>
    private static OverlayTracking Frame(float value)
    {
        var pose = new Pose(new Vector3(value, value, value), Quaternion.Identity);
        var hand = new HandState(true, pose, value > 0f, value > 0f, new Vector2(value, value));
        return new OverlayTracking(pose, hand, hand);
    }

    [Fact]
    public void NothingHasBeenPublishedMeansNobodyTracked()
    {
        var latest = new LatestTracking();

        Assert.Equal(OverlayTracking.None, latest.Read());
    }

    [Fact]
    public void AReadAnswersWithTheLatestPublish()
    {
        var latest = new LatestTracking();

        latest.Publish(Frame(1f));
        latest.Publish(Frame(2f));

        Assert.Equal(Frame(2f), latest.Read());
        Assert.Equal(Frame(2f), latest.Read());
    }

    [Fact]
    public void ClearingGoesBackToNobodyTracked()
    {
        var latest = new LatestTracking();
        latest.Publish(Frame(3f));

        latest.Clear();

        Assert.Equal(OverlayTracking.None, latest.Read());
    }

    [Fact]
    public async Task PublishesFromOneThreadAndReadsFromAnotherNeverTearAFrame()
    {
        var latest = new LatestTracking();
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(
            () =>
            {
                var value = 1f;
                while (!stop.IsCancellationRequested)
                    latest.Publish(Frame(value++));
            },
            TestContext.Current.CancellationToken);

        for (var i = 0; i < 20_000; i++)
        {
            var read = latest.Read();
            if (read == OverlayTracking.None)
                continue;

            var value = read.Head.Position.X;
            Assert.Equal(new Vector3(value, value, value), read.Head.Position);
            Assert.Equal(new Vector3(value, value, value), read.Left.Aim.Position);
            Assert.Equal(new Vector3(value, value, value), read.Right.Aim.Position);
            Assert.Equal(new Vector2(value, value), read.Left.Scroll);
            Assert.Equal(new Vector2(value, value), read.Right.Scroll);
        }

        await stop.CancelAsync();
        await writer;
    }
}
