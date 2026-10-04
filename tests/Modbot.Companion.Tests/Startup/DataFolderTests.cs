using Modbot.Companion.CloudBackup;
using Modbot.Companion.Credits;
using Modbot.Companion.Pairing;
using Modbot.Companion.Presentation;
using Modbot.Companion.Startup;

namespace Modbot.Companion.Tests.Startup;

/// <summary>
/// <c>MODBOT_DATA_FOLDER</c> makes a test copy that keeps everything in its own folder and keeps
/// out of the installed copy's way; unset, nothing changes.
/// </summary>
public class DataFolderTests
{
    private static readonly string AppData = Path.Combine(Path.GetTempPath(), "modbot-data-folder-tests", "Roaming");

    private static Func<string, string?> Env(string? value)
        => name => name == DataFolder.Variable ? value : null;

    [Fact]
    public void UnsetIsTheRealFolderAsBefore()
    {
        var choice = DataFolder.Choose(Env(null), AppData);

        Assert.Null(choice.Refusal);
        Assert.NotNull(choice.Folder);
        Assert.False(choice.Folder.IsTestCopy);
        Assert.Equal(Path.Combine(AppData, "Modbot"), choice.Folder.Path);
        Assert.Equal(@"Local\Modbot.Companion", choice.Folder.SingleInstanceName);
        Assert.Equal(PairingLinkInbox.DefaultPipeName, choice.Folder.PipeName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCountsAsUnset(string value)
    {
        var choice = DataFolder.Choose(Env(value), AppData);

        Assert.False(choice.Folder!.IsTestCopy);
    }

    [Fact]
    public void AnotherFolderMakesATestCopyThatKeepsEverythingThere()
    {
        var test = Path.Combine(Path.GetTempPath(), "modbot-data-folder-tests", "TestCopy");

        var choice = DataFolder.Choose(Env(test), AppData);

        Assert.Null(choice.Refusal);
        Assert.True(choice.Folder!.IsTestCopy);
        Assert.Equal(test, choice.Folder.Path);
    }

    [Fact]
    public void ATestCopyHasItsOwnLockAndPipe()
    {
        var one = DataFolder.Choose(Env(Path.Combine(AppData, "..", "One")), AppData).Folder!;
        var two = DataFolder.Choose(Env(Path.Combine(AppData, "..", "Two")), AppData).Folder!;
        var oneAgain = DataFolder.Choose(Env(Path.Combine(AppData, "..", "One")), AppData).Folder!;

        Assert.NotEqual(@"Local\Modbot.Companion", one.SingleInstanceName);
        Assert.NotEqual(PairingLinkInbox.DefaultPipeName, one.PipeName);
        Assert.StartsWith(@"Local\", one.SingleInstanceName, StringComparison.Ordinal);

        Assert.NotEqual(one.SingleInstanceName, two.SingleInstanceName);
        Assert.NotEqual(one.PipeName, two.PipeName);

        Assert.Equal(one.SingleInstanceName, oneAgain.SingleInstanceName);
        Assert.Equal(one.PipeName, oneAgain.PipeName);
    }

    [Fact]
    public void TheRealFolderIsRefused()
    {
        var choice = DataFolder.Choose(Env(Path.Combine(AppData, "Modbot")), AppData);

        Assert.Null(choice.Folder);
        Assert.Contains(DataFolder.Variable, choice.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealFolderIsRefusedHoweverItIsWritten()
    {
        var real = Path.Combine(AppData, "Modbot");
        var written = new List<string>
        {
            real + Path.DirectorySeparatorChar,
            Path.Combine(AppData, "Modbot", "..", "Modbot"),
            "\"" + real + "\"",
        };

        if (OperatingSystem.IsWindows())
            written.Add(real.ToUpperInvariant());

        foreach (var value in written)
            Assert.Null(DataFolder.Choose(Env(value), AppData).Folder);
    }

    [Fact]
    public void TheSettingsAndPairingsGoInTheChosenFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "modbot-data-folder-tests", "TestCopy");

        Assert.Equal(Path.Combine(folder, "settings.json"), CompanionSettings.DefaultPath(folder));
        Assert.Equal(Path.Combine(folder, "pairings.json"), DpapiPairingStore.DefaultPath(folder));
        Assert.Equal(Path.Combine(folder, "cloud-installs.json"), DpapiCloudInstallStore.DefaultPath(folder));
        Assert.Equal(folder, Path.GetDirectoryName(CloudCredits.DefaultPath(folder)));
    }
}
