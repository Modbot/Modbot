using Modbot.Companion.App.Listening;
using NAudio.Wave;

namespace Modbot.Companion.App.Tests;

/// <summary>Windows describes a shared microphone as "extensible"; its real kind has to be read.</summary>
public class PhraseListeningFormatTests
{
    private static readonly Guid FloatKind = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid WholeNumberKind = new("00000001-0000-0010-8000-00aa00389b71");

    [Fact]
    public void PlainDecimalNumbersAreRead()
        => Assert.True(PhraseListening.IsFloat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1)));

    [Fact]
    public void ExtensibleDecimalNumbersAreRead()
    {
        var format = new WaveFormatExtensible(48_000, 32, 2, FloatKind, 32, 0);

        Assert.Equal(WaveFormatEncoding.Extensible, format.Encoding);
        Assert.True(PhraseListening.IsFloat(format));
    }

    [Fact]
    public void ExtensibleWholeNumbersAreNotDecimal()
    {
        var format = new WaveFormatExtensible(48_000, 16, 1, WholeNumberKind, 16, 0);

        Assert.Equal(WaveFormatEncoding.Extensible, format.Encoding);
        Assert.False(PhraseListening.IsFloat(format));
    }

    [Fact]
    public void PlainWholeNumbersAreNotDecimal()
        => Assert.False(PhraseListening.IsFloat(new WaveFormat(48_000, 16, 1)));
}
