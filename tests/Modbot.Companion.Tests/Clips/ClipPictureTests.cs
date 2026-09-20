using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// The last step of making a clip look right: VRChat's picture drawn into one frame at the size
/// <see cref="ClipWindowRule.Fit"/> worked out.
/// </summary>
/// <remarks>
/// This is the half of the recorder that decides whether a moderator opens a clip and sees what
/// they saw in VRChat or sees a small picture in the corner of a black rectangle, and it is all
/// arithmetic — no screen, no graphics card, no file. The frames below are made up in memory and
/// read back pixel by pixel.
/// </remarks>
public class ClipPictureTests
{
    private const int Bytes = 4;

    /// <summary>A picture whose every pixel says where it is, so a move of one shows up.</summary>
    private static byte[] Gradient(int width, int height, int stride)
    {
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * stride) + (x * Bytes);
                pixels[at] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[at + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[at + 2] = 40;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] Solid(int width, int height, int stride, byte blue, byte green, byte red)
    {
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * stride) + (x * Bytes);
                pixels[at] = blue;
                pixels[at + 1] = green;
                pixels[at + 2] = red;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    private static (byte Blue, byte Green, byte Red) At(byte[] frame, int stride, int x, int y)
    {
        var at = (y * stride) + (x * Bytes);
        return (frame[at], frame[at + 1], frame[at + 2]);
    }

    [Fact]
    public void APictureAlreadyTheRightSizeIsCarriedStraightAcross()
    {
        // The ordinary case and the cheap one: the graphics card handed over exactly the size
        // being drawn, so every pixel is the pixel it was.
        var source = Gradient(8, 4, 8 * Bytes);
        var frame = new byte[8 * 4 * Bytes];

        ClipPicture.DrawInto(source, 8 * Bytes, 8, 4, frame, 8 * Bytes, 0, 0, 8, 4);

        Assert.Equal(source, frame);
    }

    [Fact]
    public void ARowThatIsWiderThanThePictureIsRead()
    {
        // The graphics card lends its own rows, which are usually padded out past the picture's
        // width. Reading them as if they were not is how a picture comes out sheared.
        var source = Gradient(4, 3, 16 * Bytes);
        var frame = new byte[4 * 3 * Bytes];

        ClipPicture.DrawInto(source, 16 * Bytes, 4, 3, frame, 4 * Bytes, 0, 0, 4, 3);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
                Assert.Equal(At(source, 16 * Bytes, x, y), At(frame, 4 * Bytes, x, y));
        }
    }

    [Fact]
    public void ScalingOneColourDownGivesThatColour()
    {
        var source = Solid(64, 64, 64 * Bytes, 10, 200, 30);
        var frame = new byte[40 * 40 * Bytes];

        ClipPicture.DrawInto(source, 64 * Bytes, 64, 64, frame, 40 * Bytes, 0, 0, 40, 40);

        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
                Assert.Equal(((byte)10, (byte)200, (byte)30), At(frame, 40 * Bytes, x, y));
        }
    }

    [Fact]
    public void ScalingOneColourUpGivesThatColour()
    {
        var source = Solid(12, 9, 12 * Bytes, 90, 5, 250);
        var frame = new byte[48 * 27 * Bytes];

        ClipPicture.DrawInto(source, 12 * Bytes, 12, 9, frame, 48 * Bytes, 0, 0, 48, 27);

        for (var y = 0; y < 27; y++)
        {
            for (var x = 0; x < 48; x++)
                Assert.Equal(((byte)90, (byte)5, (byte)250), At(frame, 48 * Bytes, x, y));
        }
    }

    [Fact]
    public void TheCornersStayInTheCorners()
    {
        // A scaled picture that crept half a pixel would show up here first: the top-left of the
        // result is the top-left of the picture and the bottom-right is the bottom-right.
        var source = Gradient(100, 100, 100 * Bytes);
        var frame = new byte[50 * 50 * Bytes];

        ClipPicture.DrawInto(source, 100 * Bytes, 100, 100, frame, 50 * Bytes, 0, 0, 50, 50);

        var topLeft = At(frame, 50 * Bytes, 0, 0);
        var bottomRight = At(frame, 50 * Bytes, 49, 49);

        Assert.True(topLeft.Blue < 10 && topLeft.Green < 10, $"top left was {topLeft}");
        Assert.True(bottomRight.Blue > 245 && bottomRight.Green > 245, $"bottom right was {bottomRight}");
    }

    [Fact]
    public void NothingOutsideTheRectangleIsTouched()
    {
        // The caller paints the frame black once, when the rectangle moves, and relies on this.
        var source = Solid(10, 10, 10 * Bytes, 255, 255, 255);
        var frame = new byte[40 * 30 * Bytes];

        ClipPicture.DrawInto(source, 10 * Bytes, 10, 10, frame, 40 * Bytes, 8, 6, 20, 16);

        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var inside = x >= 8 && x < 28 && y >= 6 && y < 22;
                var pixel = At(frame, 40 * Bytes, x, y);

                if (inside)
                    Assert.Equal(((byte)255, (byte)255, (byte)255), pixel);
                else
                    Assert.Equal(((byte)0, (byte)0, (byte)0), pixel);
            }
        }
    }

    [Fact]
    public void TheWindowFromTheScreenshotFillsTheFrame()
    {
        // The case a moderator reported on 2026-09-19: a 1366 x 768 window, whose half is odd, in
        // a 682 x 384 frame. The clip that came out had the picture in the top-left quarter and
        // black everywhere else. End to end — the fit and the drawing together — it now covers
        // all but a two-pixel strip.
        var (width, height) = ClipWindowRule.RecordedSize(1366, 768);
        var fit = ClipWindowRule.Fit(1366, 768, width, height);

        var stride = (fit.SourceWidth + 7) * Bytes;
        var source = Solid(fit.SourceWidth, fit.SourceHeight, stride, 70, 140, 210);
        var frame = new byte[width * height * Bytes];

        ClipPicture.DrawInto(
            source, stride, fit.SourceWidth, fit.SourceHeight,
            frame, width * Bytes, fit.Left, fit.Top, fit.Width, fit.Height);

        var painted = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (At(frame, width * Bytes, x, y) == ((byte)70, (byte)140, (byte)210))
                    painted++;
            }
        }

        Assert.True(
            painted > width * height * 99 / 100,
            $"Only {painted} of {width * height} pixels carried the picture.");
    }

    [Fact]
    public void NothingIsDrawnWhenThereIsNothingToDraw()
    {
        var frame = new byte[4 * 4 * Bytes];

        ClipPicture.DrawInto([], 0, 0, 0, frame, 4 * Bytes, 0, 0, 0, 0);

        Assert.All(frame, b => Assert.Equal(0, b));
    }
}
