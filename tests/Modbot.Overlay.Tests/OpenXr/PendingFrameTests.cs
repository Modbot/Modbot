using Modbot.Overlay.OpenXr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The handoff between the UI thread and the frame thread (overlay-on-OpenXR spec, 3.3): a
/// picture offered before the runtime starts is kept for the first frame, and two pictures
/// offered before a frame upload once, the later one.
/// </summary>
public class PendingFrameTests
{
    [Fact]
    public void APictureOfferedBeforeAnyFrameIsKeptForTheFirstOne()
    {
        var pending = new PendingFrame();
        Assert.False(pending.HasPending);

        pending.Offer([1, 2, 3, 4]);
        Assert.True(pending.HasPending);

        var taken = new byte[4];
        Assert.True(pending.TryTake(taken));
        Assert.Equal([1, 2, 3, 4], taken);
        Assert.False(pending.HasPending);
    }

    [Fact]
    public void TwoOffersBeforeAFrameUploadOnceAndItIsTheLater()
    {
        var pending = new PendingFrame();
        pending.Offer([1, 1, 1, 1]);
        pending.Offer([2, 2, 2, 2]);

        var taken = new byte[4];
        Assert.True(pending.TryTake(taken));
        Assert.Equal([2, 2, 2, 2], taken);

        Assert.False(pending.TryTake(taken));
        Assert.Equal([2, 2, 2, 2], taken);
    }

    [Fact]
    public void NothingPendingMeansNothingTaken()
    {
        var pending = new PendingFrame();
        var taken = new byte[] { 9, 9, 9, 9 };

        Assert.False(pending.TryTake(taken));
        Assert.Equal([9, 9, 9, 9], taken);
    }

    [Fact]
    public void APictureOfTheWrongSizeIsDroppedRatherThanUploadedTorn()
    {
        var pending = new PendingFrame();
        pending.Offer([1, 2, 3]);

        var taken = new byte[4];
        Assert.False(pending.TryTake(taken));
        Assert.False(pending.HasPending);
    }

    [Fact]
    public async Task OffersFromOneThreadAndTakesFromAnotherNeverTearAPicture()
    {
        // Each offer is a whole picture of one value; a take must see one value throughout.
        var pending = new PendingFrame();
        const int size = 64 * 64 * 4;
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(
            () =>
            {
                var value = 0;
                while (!stop.IsCancellationRequested)
                {
                    var picture = new byte[size];
                    Array.Fill(picture, (byte)(value++ & 0xff));
                    pending.Offer(picture);
                }
            },
            TestContext.Current.CancellationToken);

        var taken = new byte[size];
        var takes = 0;
        for (var i = 0; i < 2000 || takes < 50; i++)
        {
            if (!pending.TryTake(taken))
                continue;

            takes++;
            var first = taken[0];
            Assert.All(taken, b => Assert.Equal(first, b));
        }

        await stop.CancelAsync();
        await writer;
    }
}
