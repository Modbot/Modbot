using Modbot.Companion.Dictation;

namespace Modbot.Companion.Tests.Dictation;

/// <summary>What is left in the dictation folder once the pinned speech model is in place.</summary>
public class DictationDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "modbot-dictation-test-" + Guid.NewGuid().ToString("N"));

    public DictationDownloadTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string Make(string name)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "tokens.txt"), "x");
        return path;
    }

    [Fact]
    public void RemovesTheOldTinyModelAndKeepsTheCurrentOne()
    {
        var current = Make(DictationModel.Default.Name);
        var tiny = Make("sherpa-onnx-moonshine-tiny-en-int8");

        var removed = DictationDownload.RemoveOldModels(DictationModel.Default, _folder);

        Assert.Equal(["sherpa-onnx-moonshine-tiny-en-int8"], removed);
        Assert.False(Directory.Exists(tiny));
        Assert.True(Directory.Exists(current));
    }

    [Fact]
    public void LeavesEveryOtherFolderAndFileAlone()
    {
        Make(DictationModel.Default.Name);
        var voices = Make("voices");
        var other = Make("sherpa-onnx-whisper-tiny");
        var loose = Path.Combine(_folder, "sherpa-onnx-moonshine-old.txt");
        File.WriteAllText(loose, "x");

        var removed = DictationDownload.RemoveOldModels(DictationModel.Default, _folder);

        Assert.Empty(removed);
        Assert.True(Directory.Exists(voices));
        Assert.True(Directory.Exists(other));
        Assert.True(File.Exists(loose));
    }

    [Fact]
    public void SaysNothingAndFailsNothingWhenThereIsNoFolder()
    {
        var removed = DictationDownload.RemoveOldModels(DictationModel.Default, Path.Combine(_folder, "missing"));

        Assert.Empty(removed);
    }
}
