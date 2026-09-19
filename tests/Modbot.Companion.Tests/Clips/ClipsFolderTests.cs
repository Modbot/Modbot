using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// Where clips go, and what happens when they cannot go there. A folder that cannot be written to
/// is a sentence on the settings screen, never an exception.
/// </summary>
public class ClipsFolderTests
{
    [Fact]
    public void TheUsualPlaceIsTheMachinesOwnVideosFolderPlusModbotClips()
    {
        var resolved = ClipsFolder.Resolve(null, videos: @"C:\Users\someone\Videos");

        Assert.Equal(Path.Combine(@"C:\Users\someone\Videos", "Modbot Clips"), resolved);
    }

    [Fact]
    public void AFolderTheModeratorNamedWins()
    {
        Assert.Equal(@"D:\Recordings", ClipsFolder.Resolve(@"  D:\Recordings  ", videos: @"C:\Videos"));
    }

    [Fact]
    public void BlankMeansTheUsualPlace()
    {
        Assert.Equal(
            Path.Combine(@"C:\Videos", "Modbot Clips"),
            ClipsFolder.Resolve("   ", videos: @"C:\Videos"));
    }

    [Fact]
    public void AMachineWithNoVideosFolderFallsBackToModbotsOwn()
    {
        // Ordinary on a server-style Windows install and on Linux. Refusing to record because
        // Windows has no Videos folder would be the wrong answer to an unimportant question.
        var resolved = ClipsFolder.Resolve(null, videos: "", fallback: @"C:\Users\someone\AppData\Roaming\Modbot");

        Assert.Equal(
            Path.Combine(@"C:\Users\someone\AppData\Roaming\Modbot", "Modbot Clips"),
            resolved);
    }

    [Fact]
    public void AFolderThatCanBeWrittenToIsUsable()
    {
        var folder = Scratch();

        var check = ClipsFolder.Check(folder);

        Assert.True(check.IsUsable);
        Assert.Null(check.Problem);
        Assert.True(Directory.Exists(folder), "The folder should have been made.");
    }

    [Fact]
    public void AFolderThatIsNotAFullPathIsRefusedRatherThanGuessedAt()
    {
        var check = ClipsFolder.Check("clips");

        Assert.False(check.IsUsable);
        Assert.NotNull(check.Problem);
    }

    [Fact]
    public void NothingIsAProblemRatherThanACrash()
    {
        Assert.False(ClipsFolder.Check("").IsUsable);
        Assert.False(ClipsFolder.Check("   ").IsUsable);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenToComesBackAsOneSentence()
    {
        // A file where the folder should be is the portable way to make a folder unusable: no
        // permissions to change, no drive to unplug, and the same answer on Windows and Linux.
        var path = Path.Combine(Scratch(), "in-the-way");
        File.WriteAllText(path, "not a folder");

        var check = ClipsFolder.Check(path);

        Assert.False(check.IsUsable);
        Assert.NotNull(check.Problem);
        Assert.NotEmpty(check.Problem);
    }

    [Fact]
    public void CheckingTwiceLeavesNothingBehind()
    {
        // The test file is written to find out whether writing works and is then removed; a folder
        // that slowly filled with Modbot's own leavings would be a poor advertisement.
        var folder = Scratch();

        ClipsFolder.Check(folder);
        ClipsFolder.Check(folder);

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    private static string Scratch()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "modbot-clips-folder-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(folder);
        return folder;
    }
}
