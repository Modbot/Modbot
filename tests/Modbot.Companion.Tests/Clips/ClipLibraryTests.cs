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

    /// <summary>The moment the worked example in the clips design spec is named after.</summary>
    private static ClipLibrary At(int hour = 18, int minute = 2, int second = 29)
        => new(new FakeClock(new DateTimeOffset(
            new DateTime(2026, 9, 19, hour, minute, second, DateTimeKind.Local).ToUniversalTime(),
            TimeSpan.Zero)));

    [Fact]
    public void AClipIsNamedAfterTheWorldTheInstanceAndTheMoment()
    {
        // The name a moderator asked for, and the one the clips design spec carries as its worked
        // example: world, instance number, local date and time, joined with underscores.
        var name = At().NameFor(
            worldName: "The Black Cat",
            worldId: "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b",
            instanceId: "98874~group(grp_x)~region(use)");

        Assert.Equal("The Black Cat_98874_2026-09-19 18-02-29.mp4", name);
    }

    [Fact]
    public void WithoutAWorldNameTheWorldIdStandsIn()
    {
        // VRChat says the world's readable name on its own line, and a moderator saving a clip
        // before that line has arrived should still get a clip that names the world somehow.
        var name = At().NameFor(
            worldId: "wrld_4cf554b4",
            instanceId: "98874");

        Assert.Equal("wrld_4cf554b4_98874_2026-09-19 18-02-29.mp4", name);
    }

    [Fact]
    public void WithNoWorldAndNoInstanceTheMomentIsTheWholeName()
    {
        // Outside an instance, or in one Modbot could not read. Still a clip.
        Assert.Equal("2026-09-19 18-02-29.mp4", At().NameFor());
    }

    [Theory]
    [InlineData("98874~group(grp_x)~region(use)", "98874")]
    [InlineData("98874", "98874")]
    [InlineData("  98874  ", "98874")]
    [InlineData("front desk~group(grp_x)", "front desk")]
    [InlineData("~group(grp_x)", "~group(grp_x)")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheInstanceNumberIsWhateverIsInFrontOfTheQualifiers(string? instanceId, string expected)
    {
        // Never checked for shape. VRChat's ids follow no structure (foundation 3.1.1) and a group
        // can set an instance id to any text it likes, so this takes what is in front of the first
        // tilde and falls back to the whole id rather than asserting anything about it.
        Assert.Equal(expected, ClipLibrary.InstanceNumber(instanceId));
    }

    [Fact]
    public void TheWholeNameIsMadeSafeRatherThanItsPieces()
    {
        // A world name is whatever a person typed and an instance id carries brackets and tildes.
        // The template is built first and made safe once, so nothing a world or an instance can
        // hold leaves a name Windows will not take.
        var name = At().NameFor(
            worldName: @"Bad/World:Name?",
            instanceId: @"12345~group(grp_x)/..\evil:name");

        foreach (var bad in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(bad, name);

        foreach (var bad in "<>:\"/\\|?*")
            Assert.DoesNotContain(bad, name);

        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.EndsWith("2026-09-19 18-02-29.mp4", name, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorldNameInSomebodyElsesAlphabetSurvives()
    {
        // Plenty of VRChat worlds are named in scripts that are not Latin. They are legal in a
        // Windows file name, so they are kept rather than turned into a row of underscores.
        var name = At().NameFor(worldName: "ΛƧƬΛ なまえ", instanceId: "7");

        Assert.Equal("ΛƧƬΛ なまえ_7_2026-09-19 18-02-29.mp4", name);
    }

    [Theory]
    [InlineData("///")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("‮‭")]
    public void ANameThatIsMadeSafeIntoNothingStillHasAName(string awkward)
    {
        // An empty name would make a file called ".mp4", which Windows hides and nobody finds.
        var made = ClipLibrary.AsFileName(awkward);

        Assert.NotEmpty(made);
        Assert.Equal("Clip", made);
    }

    [Fact]
    public void AClipNeverLandsOnOneThatIsAlreadyThere()
    {
        // Two saves in the same second, in the same instance. The one that would be lost is the
        // first, which is the one the moderator pressed Save for.
        var folder = Scratch();
        var library = At();

        var first = library.NameFor(worldName: "The Black Cat", instanceId: "98874", folder: folder);
        Write(folder, first, 10);

        var second = library.NameFor(worldName: "The Black Cat", instanceId: "98874", folder: folder);
        Write(folder, second, 10);

        var third = library.NameFor(worldName: "The Black Cat", instanceId: "98874", folder: folder);

        Assert.Equal("The Black Cat_98874_2026-09-19 18-02-29.mp4", first);
        Assert.Equal("The Black Cat_98874_2026-09-19 18-02-29 (2).mp4", second);
        Assert.Equal("The Black Cat_98874_2026-09-19 18-02-29 (3).mp4", third);
    }

    [Fact]
    public void AVeryLongWorldNameIsCutRatherThanCarried()
    {
        // A world name is somebody else's text and has no length anybody promised. The moment is
        // the part that must survive, because it is what a moderator sorts by.
        var name = At().NameFor(
            worldName: new string('w', 500),
            instanceId: new string('i', 500));

        Assert.True(
            name.Length - ClipLibrary.ClipExtension.Length <= ClipLibrary.MostNameCharacters,
            $"A clip's name came to {name.Length} characters.");

        Assert.EndsWith("2026-09-19 18-02-29.mp4", name, StringComparison.Ordinal);
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
