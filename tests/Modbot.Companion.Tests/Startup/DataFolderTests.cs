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
    public void AFolderInsideTheRealOneOrHoldingItIsRefused()
    {
        Assert.Null(DataFolder.Choose(Env(Path.Combine(AppData, "Modbot", "test")), AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(AppData), AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(Path.GetPathRoot(AppData)), AppData).Folder);

        // A neighbour whose name only starts the same is fine.
        Assert.NotNull(DataFolder.Choose(Env(Path.Combine(AppData, "ModbotTest")), AppData).Folder);
    }

    [Fact]
    public void TheRealFolderIsRefusedWithALongPathPrefix()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), @"\\?\ paths are a Windows thing.");

        Assert.Null(DataFolder.Choose(Env(@"\\?\" + Path.Combine(AppData, "Modbot")), AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(@"\\?\" + Path.Combine(AppData, "Modbot", "inner")), AppData).Folder);
    }

    [Fact]
    public void TheRealFolderIsRefusedByItsShortName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Short names are a Windows thing.");

        using var place = new Place("Roaming Folder With A Long Name");
        var real = Directory.CreateDirectory(DataFolder.Real(place.AppData)).FullName;

        var buffer = new System.Text.StringBuilder(1024);
        var length = GetShortPathNameW(real, buffer, (uint)buffer.Capacity);
        var shortName = buffer.ToString();
        Assert.SkipWhen(
            length == 0 || string.Equals(shortName, real, StringComparison.OrdinalIgnoreCase),
            "This drive makes no short names.");

        Assert.Null(DataFolder.Choose(Env(shortName), place.AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(Path.Combine(shortName, "inner")), place.AppData).Folder);
    }

    [Fact]
    public void TheRealFolderIsRefusedThroughALink()
    {
        using var place = new Place("Roaming");
        var real = Directory.CreateDirectory(DataFolder.Real(place.AppData)).FullName;
        var link = Path.Combine(place.Root, "LinkToModbot");
        var linkAbove = Path.Combine(place.Root, "LinkToRoaming");

        try
        {
            Directory.CreateSymbolicLink(link, real);
            Directory.CreateSymbolicLink(linkAbove, place.AppData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account cannot make links here (Windows needs Developer Mode or an administrator).");
        }

        Assert.Null(DataFolder.Choose(Env(link), place.AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(Path.Combine(link, "inner")), place.AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(Path.Combine(linkAbove, "Modbot")), place.AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(linkAbove), place.AppData).Folder);
    }

    [Fact]
    public void TheRealFolderIsRefusedThroughAJunction()
    {
        // A junction needs no special rights, unlike a symbolic link, so this one runs on any
        // Windows account. Made with mklink, because .NET has no call that makes one.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        using var place = new Place("Roaming");
        var real = Directory.CreateDirectory(DataFolder.Real(place.AppData)).FullName;
        var junction = Path.Combine(place.Root, "JunctionToModbot");

        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", junction, real])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            mklink!.WaitForExit(10_000);
        }

        Assert.SkipUnless(new DirectoryInfo(junction).LinkTarget is not null, "mklink could not make a junction here.");

        Assert.Null(DataFolder.Choose(Env(junction), place.AppData).Folder);
        Assert.Null(DataFolder.Choose(Env(Path.Combine(junction, "inner")), place.AppData).Folder);
    }

    [Fact]
    public void AnOrdinaryFolderBesideItIsFine()
    {
        using var place = new Place("Roaming");
        Directory.CreateDirectory(DataFolder.Real(place.AppData));
        var test = Directory.CreateDirectory(Path.Combine(place.Root, "TestCopy")).FullName;

        var choice = DataFolder.Choose(Env(test), place.AppData);

        Assert.True(choice.Folder!.IsTestCopy);
    }

    /// <summary>A real folder tree under the temp folder, deleted afterwards (links first, never followed).</summary>
    private sealed class Place : IDisposable
    {
        public Place(string roamingName)
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "modbot-data-folder-" + Guid.NewGuid().ToString("N"))).FullName;
            AppData = Directory.CreateDirectory(Path.Combine(Root, roamingName)).FullName;
        }

        public string Root { get; }

        public string AppData { get; }

        public void Dispose()
        {
            foreach (var entry in new DirectoryInfo(Root).EnumerateDirectories())
            {
                if (entry.LinkTarget is not null)
                    entry.Delete();
            }

            Directory.Delete(Root, recursive: true);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint size);

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
