using Modbot.Companion.Listening;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// Bringing a microphone's rate to the phrase model's, a buffer at a time, with no seam between
/// buffers and no read past the end of one.
/// </summary>
public class ResamplerTests
{
    private const int ModelRate = 16_000;

    private static float[] Ramp(int start, int count)
        => Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    [Theory]
    [InlineData(8_000)]
    [InlineData(16_000)]
    [InlineData(22_050)]
    [InlineData(32_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void NoRateReadsPastTheEndOfABuffer(int rate)
    {
        // A Bluetooth headset in call mode runs at the model's own rate, where every sample —
        // the last one of each buffer included — is one the resampler lands on exactly. That used
        // to read the sample after the end and throw on Windows' audio thread.
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        foreach (var frames in new[] { 160, 441, 480, 1, 2, 3, 1600, 4410 })
            resampler.Add(Ramp(0, frames), rate, into);

        Assert.NotEmpty(into);
    }

    [Fact]
    public void AtTheModelsOwnRateEverySampleComesOutOnceAndInOrder()
    {
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        resampler.Add(Ramp(0, 5), ModelRate, into);
        resampler.Add(Ramp(5, 5), ModelRate, into);
        resampler.Add(Ramp(10, 5), ModelRate, into);

        // The last sample of the last buffer waits for a buffer that has not come yet.
        Assert.Equal(Ramp(0, 14), into);
    }

    [Fact]
    public void ThreeTimesTheRateKeepsEveryThirdSampleAcrossBuffers()
    {
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        // Buffers of a size that is not a multiple of three, so the positions carried over are
        // not whole numbers of buffers.
        resampler.Add(Ramp(0, 7), 48_000, into);
        resampler.Add(Ramp(7, 7), 48_000, into);
        resampler.Add(Ramp(14, 7), 48_000, into);

        Assert.Equal(new float[] { 0, 3, 6, 9, 12, 15, 18 }, into);
    }

    [Fact]
    public void ASampleBetweenTwoBuffersIsReadFromBoth()
    {
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        // Half the model's rate: a sample every half step, so one falls between the last sample
        // of the first buffer and the first of the second.
        resampler.Add([0f, 2f], 8_000, into);
        resampler.Add([4f, 6f], 8_000, into);

        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5 }, into);
    }

    [Fact]
    public void ABufferOfOneSampleIsCarriedToTheNext()
    {
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        resampler.Add([7f], ModelRate, into);
        Assert.Empty(into);

        resampler.Add([8f, 9f], ModelRate, into);
        Assert.Equal(new float[] { 7, 8 }, into);
    }

    [Fact]
    public void AnEmptyBufferChangesNothing()
    {
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        resampler.Add([], 48_000, into);
        resampler.Add(Ramp(0, 3), ModelRate, into);

        Assert.Equal(new float[] { 0, 1 }, into);
    }

    [Fact]
    public void ResetForgetsTheBufferBefore()
    {
        var resampler = new Resampler(ModelRate);
        var into = new List<float>();

        resampler.Add(Ramp(100, 3), ModelRate, into);
        resampler.Reset();
        into.Clear();

        resampler.Add(Ramp(0, 3), ModelRate, into);

        Assert.Equal(new float[] { 0, 1 }, into);
    }
}
