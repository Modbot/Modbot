using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// The pinned voice and the ten voices inside it: which one a name means, and what the Settings
/// card is told to show.
/// </summary>
public class VoiceModelTests
{
    [Fact]
    public void TheVoicesAreNamedThingsAPersonWouldRecognise()
    {
        var voices = VoiceModel.Default.Voices;

        Assert.Equal(10, voices.Count);
        Assert.Equal(
            ["Bella", "Nicole", "Sarah", "Sky", "Adam", "Michael", "Emma", "Isabella", "George", "Lewis"],
            voices.Select(v => v.Name));

        // Every one is a different voice in the file, and none of them is the blend at number 0.
        Assert.Equal(voices.Count, voices.Select(v => v.Number).Distinct().Count());
        Assert.All(voices, v => Assert.InRange(v.Number, 1, 10));
    }

    [Fact]
    public void TheDefaultVoiceIsOneOfThem()
    {
        Assert.Contains(VoiceModel.Default.Voices, v => v.Name == VoiceModel.DefaultName);
        Assert.Equal(VoiceModel.DefaultName, VoiceSettings.Default.VoiceName);
    }

    [Theory]
    [InlineData("Bella", 1)]
    [InlineData("bella", 1)]
    [InlineData("GEORGE", 9)]
    [InlineData("Lewis", 10)]
    public void ANameIsTheNumberTheEngineKnowsItBy(string name, int number)
    {
        Assert.Equal(number, VoiceModel.Default.Number(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Gilgamesh")]
    public void ANameThisVoiceDoesNotHaveIsTheDefaultOne(string? name)
    {
        Assert.Equal(VoiceModel.Default.Number(VoiceModel.DefaultName), VoiceModel.Default.Number(name));
    }

    [Fact]
    public void ANothingSaidYetStatusOffersTheSameVoicesAndSize()
    {
        var none = VoiceStatus.None;

        Assert.Equal(VoiceModel.Default.Size, none.DownloadSize);
        Assert.Equal(VoiceModel.Default.Voices, none.Voices);
        Assert.False(none.IsDownloading);
    }

    [Theory]
    [InlineData(VoiceState.Downloading, true)]
    [InlineData(VoiceState.Replacing, true)]
    [InlineData(VoiceState.Ready, false)]
    [InlineData(VoiceState.Failed, false)]
    [InlineData(VoiceState.NotDownloaded, false)]
    public void BothKindsOfDownloadCountAsDownloading(VoiceState state, bool downloading)
    {
        Assert.Equal(downloading, (VoiceStatus.None with { State = state }).IsDownloading);
    }

    [Fact]
    public void TheVoiceLivesUnderTheCompanionsOwnFolder()
    {
        var voices = VoiceModel.VoicesFolder(Path.Combine("C:", "Modbot"));
        var model = VoiceModel.Default;

        Assert.Equal(Path.Combine("C:", "Modbot", "voices"), voices);
        Assert.Equal(Path.Combine(voices, model.Name), model.Folder(voices));
        Assert.Equal(Path.Combine(voices, model.Name, "model.onnx"), model.ModelPath(voices));
        Assert.Equal(Path.Combine(voices, model.Name, "voices.bin"), model.VoicesPath(voices));
        Assert.Equal(Path.Combine(voices, model.Name, "tokens.txt"), model.TokensPath(voices));
        Assert.Equal(Path.Combine(voices, model.Name, "espeak-ng-data"), model.DataPath(voices));
        Assert.Equal(Path.Combine(voices, model.Name, VoiceModel.MarkerFile), model.MarkerPath(voices));
    }

    [Fact]
    public void AVoiceIsNotPresentUntilEveryPieceAndTheMarkerAreThere()
    {
        var folder = Directory.CreateTempSubdirectory("modbot-voice-model-").FullName;
        try
        {
            var model = VoiceModel.Default;
            Assert.False(model.IsPresent(folder));

            Directory.CreateDirectory(model.Folder(folder));
            Directory.CreateDirectory(model.DataPath(folder));
            File.WriteAllText(model.ModelPath(folder), "model");
            File.WriteAllText(model.TokensPath(folder), "tokens");

            // Everything but the voices file: not a voice this client can speak with.
            Assert.False(model.IsPresent(folder));

            File.WriteAllText(model.VoicesPath(folder), "voices");
            Assert.False(model.IsPresent(folder));

            File.WriteAllText(model.MarkerPath(folder), model.MarkerText());
            Assert.True(model.IsPresent(folder));

            // A marker naming a different download does not count, so a newer pinned voice is
            // fetched rather than an older folder with the right name being trusted.
            File.WriteAllText(model.MarkerPath(folder), (model with { Sha256 = new string('a', 64) }).MarkerText());
            Assert.False(model.IsPresent(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
