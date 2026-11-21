using Modbot.Companion.Dictation;

namespace Modbot.Companion.Tests.Dictation;

/// <summary>The pinned speech model: one address, one size, one hash, and the five files kept.</summary>
public class DictationModelTests
{
    private static readonly DictationModel Model = DictationModel.Default;

    [Fact]
    public void IsPinnedToOneAddressOneSizeAndOneHash()
    {
        Assert.Equal("https", Model.Url.Scheme);
        Assert.Equal("github.com", Model.Url.Host);
        Assert.True(Model.Size > 0);
        Assert.Matches("^[0-9a-f]{64}$", Model.Sha256);
    }

    [Fact]
    public void IsMoonshineBaseWithTheMeasuredSizeAndHash()
    {
        Assert.Equal("sherpa-onnx-moonshine-base-en-int8", Model.Name);
        Assert.Equal("sherpa-onnx-moonshine-base-en-int8", Model.ArchiveFolder);
        Assert.Equal(
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-moonshine-base-en-int8.tar.bz2",
            Model.Url.ToString());
        Assert.Equal(250_807_309, Model.Size);
        Assert.Equal("21870cecaa2e44e4e2bf63e02d1072bed183ccd10284871353bd9d24dad14e5e", Model.Sha256);
    }

    [Fact]
    public void KeepsTheFiveFilesTheEngineNeedsAndNothingElse()
    {
        Assert.Equal(
            ["preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt"],
            DictationModel.Files);
    }

    [Fact]
    public void StopsARecordingAtOneMinuteOfSoundAtTheModelsRate()
    {
        Assert.Equal(16_000, Model.SampleRate);
        Assert.Equal(60 * 16_000, Model.MostSamples);
    }

    [Fact]
    public void IsNotPresentInAFolderThatDoesNotHaveIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "modbot-dictation-test-" + Guid.NewGuid().ToString("N"));

        Assert.False(Model.IsPresent(folder));
    }
}
