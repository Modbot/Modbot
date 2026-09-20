namespace Modbot.Companion.Clips;

/// <summary>
/// Puts VRChat's picture into one frame of a clip at the size <see cref="ClipWindowRule.Fit"/>
/// worked out.
/// </summary>
/// <remarks>
/// <para><strong>It reads nothing and writes nothing.</strong> Two blocks of pixels go in and one
/// of them comes out changed; there is no screen, no file and no graphics card anywhere in here,
/// which is the point — this is the last step of making a clip look right and it can be checked
/// without any of those.</para>
/// <para><strong>Why the processor does this at all.</strong> The graphics card can halve a
/// picture for free, and it does every halving it can before this is reached. What it cannot do is
/// the step in between: a window that is 1.4 times the frame is too big for one halving and too
/// small for two. That last step is this, over at most four pixels for every one it writes, and it
/// is what stops a clip being a small picture in the corner of a black frame.</para>
/// <para><strong>Four pixels for one.</strong> Each pixel written is the blend of the four around
/// where it came from, which is what keeps a shrunk picture from crawling and a stretched one from
/// going blocky. Everything is BGRA — four bytes a pixel, in the order Windows hands them over and
/// the order the encoder takes them — and the fourth byte is carried through untouched because the
/// encoder ignores it.</para>
/// </remarks>
public static class ClipPicture
{
    /// <summary>Bytes in one pixel: blue, green, red, and one the encoder ignores.</summary>
    private const int BytesPerPixel = 4;

    /// <summary>How finely the blend is measured. A power of two, so the maths stays exact.</summary>
    private const int Steps = 256;

    /// <summary>
    /// Draws <paramref name="source"/> into <paramref name="frame"/>, scaled to
    /// <paramref name="width"/> by <paramref name="height"/> and starting at
    /// <paramref name="left"/>, <paramref name="top"/>.
    /// </summary>
    /// <remarks>
    /// Nothing outside that rectangle is touched, so whatever the caller last put there stands —
    /// which is why the caller paints the frame black once, when the rectangle changes, rather
    /// than on every frame.
    /// </remarks>
    /// <param name="source">The picture to draw, BGRA, rows top to bottom.</param>
    /// <param name="sourceStride">How many bytes one row of it takes, which may be more than its width.</param>
    /// <param name="sourceWidth">How wide it is.</param>
    /// <param name="sourceHeight">How tall it is.</param>
    /// <param name="frame">The frame to draw into, BGRA, rows top to bottom.</param>
    /// <param name="frameStride">How many bytes one row of the frame takes.</param>
    /// <param name="left">How far in from the frame's left edge to start.</param>
    /// <param name="top">How far down from the frame's top edge to start.</param>
    /// <param name="width">How wide to draw it.</param>
    /// <param name="height">How tall to draw it.</param>
    public static void DrawInto(
        ReadOnlySpan<byte> source,
        int sourceStride,
        int sourceWidth,
        int sourceHeight,
        Span<byte> frame,
        int frameStride,
        int left,
        int top,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return;

        // The ordinary case, and the cheap one: the graphics card already handed over exactly the
        // size being drawn, so every pixel is carried straight across.
        if (sourceWidth == width && sourceHeight == height)
        {
            for (var y = 0; y < height; y++)
            {
                source.Slice(y * sourceStride, width * BytesPerPixel)
                    .CopyTo(frame.Slice(((top + y) * frameStride) + (left * BytesPerPixel), width * BytesPerPixel));
            }

            return;
        }

        // Where each column comes from, worked out once rather than once per row.
        Span<int> columnAt = width <= ClipWindowRule.MaxWidth
            ? stackalloc int[ClipWindowRule.MaxWidth]
            : new int[width];

        Span<int> columnPart = width <= ClipWindowRule.MaxWidth
            ? stackalloc int[ClipWindowRule.MaxWidth]
            : new int[width];

        for (var x = 0; x < width; x++)
        {
            var (at, part) = Between(x, width, sourceWidth);
            columnAt[x] = at;
            columnPart[x] = part;
        }

        for (var y = 0; y < height; y++)
        {
            var (row, downPart) = Between(y, height, sourceHeight);
            var nextRow = Math.Min(row + 1, sourceHeight - 1);

            var above = source.Slice(row * sourceStride, sourceWidth * BytesPerPixel);
            var below = source.Slice(nextRow * sourceStride, sourceWidth * BytesPerPixel);
            var into = frame.Slice(((top + y) * frameStride) + (left * BytesPerPixel), width * BytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                var column = columnAt[x];
                var nextColumn = Math.Min(column + 1, sourceWidth - 1);
                var acrossPart = columnPart[x];

                var here = column * BytesPerPixel;
                var next = nextColumn * BytesPerPixel;
                var to = x * BytesPerPixel;

                for (var channel = 0; channel < BytesPerPixel; channel++)
                {
                    var topEdge = Blend(above[here + channel], above[next + channel], acrossPart);
                    var bottomEdge = Blend(below[here + channel], below[next + channel], acrossPart);
                    into[to + channel] = (byte)Blend(topEdge, bottomEdge, downPart);
                }
            }
        }
    }

    /// <summary>
    /// Which pixel of the source one pixel of the result sits on, and how far past it, in
    /// <see cref="Steps"/>ths.
    /// </summary>
    /// <remarks>
    /// Measured from the middle of each pixel rather than its corner, which is what keeps a scaled
    /// picture in the same place instead of creeping half a pixel towards one edge. A result pixel
    /// that lands before the first source pixel or after the last is held at the edge.
    /// </remarks>
    private static (int At, int Part) Between(int at, int outOf, int sourceSize)
    {
        var middle = ((((2L * at) + 1) * sourceSize * Steps) / (2L * outOf)) - (Steps / 2);
        if (middle < 0)
            middle = 0;

        var last = ((long)sourceSize - 1) * Steps;
        if (middle > last)
            middle = last;

        return ((int)(middle / Steps), (int)(middle % Steps));
    }

    /// <summary><paramref name="part"/>/<see cref="Steps"/> of the way from one value to the other.</summary>
    private static int Blend(int from, int to, int part) => from + (((to - from) * part) / Steps);
}
