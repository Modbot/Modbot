using Modbot.Companion.Clips;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// The clips folder: what is counted, what is deleted to stop it growing forever, and what a saved
/// clip is called.
/// </summary>
public class ClipLibraryTests
{
    [Fact]
    public void AMissingFolderIsEmptyRatherThanAnError()
    {
        // A moderator may delete or move the folder while the client is running. That is their
        // folder to move.
        var library = new ClipLibrary(new FakeClock());

        Assert.Empty(library.List(Path.Combine(Path.GetTempPath(), "modbot-clips-not-here", Guid.NewGuid().ToString("n"))));
        Assert.Equal(0, library.Bytes("   "));
    }

    [Fact]
    public void OnlyModbotsOwnClipsAreCounted()
    {
        // Somebody's own videos may be sitting in the same folder. Modbot counts what it wrote and
        // would only ever delete what it wrote.
        var folder = Scratch();
        Write(folder, "a" + ClipLibrary.ClipExtension, 100);
        Write(folder, "holiday.mov", 5000);
        Write(folder, "notes.txt", 10);

        var library = new ClipLibrary(new FakeClock());

        Assert.Single(library.List(folder));
        Assert.Equal(100, library.Bytes(folder));
    }

    [Fact]
    public void ClipsComeBackOldestFirst()
    {
        var folder = Scratch();
        var first = Write(folder, "first" + ClipLibrary.ClipExtension, 10);
        var second = Write(folder, "second" + ClipLibrary.ClipExtension, 10);

        File.SetLastWriteTimeUtc(first, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var listed = new ClipLibrary(new FakeClock()).List(folder);

        Assert.Equal(["first" + ClipLibrary.ClipExtension, "second" + ClipLibrary.ClipExtension],
            listed.Select(c => Path.GetFileName(c.Path)));
    }

    [Fact]
    public void NothingIsDeletedWhileTheFolderIsInsideItsLimit()
    {
        var folder = Scratch();
        Write(folder, "a" + ClipLibrary.ClipExtension, 100);
        Write(folder, "b" + ClipLibrary.ClipExtension, 100);

        Assert.Equal(0, new ClipLibrary(new FakeClock()).MakeRoom(folder, limitBytes: 1000));
        Assert.Equal(2, Directory.EnumerateFiles(folder).Count());
    }

    [Fact]
    public void TheOldestGoFirstWhenTheFolderIsOverItsLimit()
    {
        // Video is large, and a recorder that only ever adds would eventually fill the disk of the
        // machine it was installed to help.
        var folder = Scratch();
        var oldest = Write(folder, "oldest" + ClipLibrary.ClipExtension, 400);
        var middle = Write(folder, "middle" + ClipLibrary.ClipExtension, 400);
        var newest = Write(folder, "newest" + ClipLibrary.ClipExtension, 400);

        File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(middle, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var deleted = new ClipLibrary(new FakeClock()).MakeRoom(folder, limitBytes: 900);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void TheNewestClipIsNeverDeletedToMakeRoom()
    {
        // A limit smaller than one clip would otherwise delete the thing that was just saved, which
        // is the one outcome somebody who pressed Save must not get.
        var folder = Scratch();
        Write(folder, "only" + ClipLibrary.ClipExtension, 5000);

        Assert.Equal(0, new ClipLibrary(new FakeClock()).MakeRoom(folder, limitBytes: 10));
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public void RoomIsMadeForTheClipThatIsAboutToBeWritten()
    {
        var folder = Scratch();
        var oldest = Write(folder, "oldest" + ClipLibrary.ClipExtension, 400);
        var newest = Write(folder, "newest" + ClipLibrary.ClipExtension, 400);

        File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // Inside the limit as it stands; over it once another 400 bytes arrive.
        Assert.Equal(1, new ClipLibrary(new FakeClock()).MakeRoom(folder, limitBytes: 1000, aboutToAdd: 400));
        Assert.False(File.Exists(oldest));
    }

    [Fact]
    public void AClipIsNamedAfterTheMomentItWasSaved()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 19, 20, 27, 14, TimeSpan.Zero));

        var name = new ClipLibrary(clock).NameFor();

        Assert.EndsWith(ClipLibrary.ClipExtension, name, StringComparison.Ordinal);
        Assert.StartsWith("2026-09-", name, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceIdIsFilteredRatherThanTrustedAsAFileName()
    {
        // VRChat's ids follow no structure and are never validated for shape (foundation 3.1.1), so
        // anything that is not plainly safe in a file name becomes an underscore.
        var name = new ClipLibrary(new FakeClock()).NameFor(@"12345~group(grp_x)/..\evil:name");

        foreach (var bad in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(bad, name);

        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceMadeOnlyOfAwkwardCharactersJustLeavesTheMoment()
    {
        var name = new ClipLibrary(new FakeClock()).NameFor("///");

        Assert.EndsWith(ClipLibrary.ClipExtension, name, StringComparison.Ordinal);
    }

    private static string Scratch()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "modbot-clip-library-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Write(string folder, string name, int bytes)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
