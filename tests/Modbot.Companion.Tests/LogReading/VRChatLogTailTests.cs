using System.Text;
using Modbot.Companion.LogReading;

namespace Modbot.Companion.Tests.LogReading;

public sealed class VRChatLogTailTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("modbot-tail-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents, Encoding.UTF8);
        return path;
    }

    private void Append(string name, string contents)
        => File.AppendAllText(Path.Combine(_directory, name), contents, Encoding.UTF8);

    private VRChatLogTail Tail() => new(_directory);

    [Fact]
    public void ReadsNothingWhenVRChatHasNeverRun()
    {
        // A missing folder is the normal state on a machine where VRChat is not installed yet. It
        // is not an error and must not throw.
        var tail = new VRChatLogTail(Path.Combine(_directory, "not-created"));

        Assert.Empty(tail.ReadPending());
        Assert.Null(tail.CurrentFile);
    }

    [Fact]
    public void ReadsNothingWhenTheFolderHasNoLogs()
    {
        Write("something-else.txt", "not a vrchat log\n");

        Assert.Empty(Tail().ReadPending());
    }

    [Fact]
    public void TheFirstPassOverAnExistingLogIsMarkedAsReplay()
    {
        // The client may start long after VRChat did. The history is read so the tracker knows
        // which instance the moderator is in -- but it is flagged, because reporting hours of old
        // events as fresh observations on every restart would be wrong.
        Write("output_log_2026-09-03_20-26-45.txt", "one\ntwo\n");

        var read = Tail().ReadPending();

        Assert.Equal(["one", "two"], read.Select(l => l.Text));
        Assert.All(read, l => Assert.True(l.IsReplay));
    }

    [Fact]
    public void ABigLogIsReadInSlicesAndEverySliceOfItIsReplay()
    {
        // The first pass over a large existing log must not read it all at once, and every slice
        // of that history is still history: only what arrives after the reader reaches the end
        // is live. Twenty-five lines of ten bytes against a 64-byte slice is four passes.
        var lines = Enumerable.Range(0, 25).Select(i => $"line-{i:D4}").ToList();
        Write("output_log_2026-09-13_10-00-00.txt", string.Join("\n", lines) + "\n");

        var tail = new VRChatLogTail(_directory, maxBytesPerPass: 64);
        var seen = new List<TailedLine>();
        var passes = 0;

        while (true)
        {
            var batch = tail.ReadPending();
            if (batch.Count == 0)
                break;

            passes++;
            seen.AddRange(batch);
        }

        Assert.True(passes >= 3, $"expected several passes, got {passes}");
        Assert.Equal(lines, seen.Select(l => l.Text));
        Assert.All(seen, l => Assert.True(l.IsReplay, $"{l.Text} should be replay"));

        Append("output_log_2026-09-13_10-00-00.txt", "line-live\n");
        var live = Assert.Single(tail.ReadPending());
        Assert.Equal("line-live", live.Text);
        Assert.False(live.IsReplay);
    }

    [Fact]
    public void LinesAppendedAfterwardsAreLive()
    {
        Write("output_log_2026-09-03_20-26-45.txt", "one\n");
        var tail = Tail();
        tail.ReadPending();

        Append("output_log_2026-09-03_20-26-45.txt", "two\n");

        var read = tail.ReadPending();
        Assert.Equal(["two"], read.Select(l => l.Text));
        Assert.All(read, l => Assert.False(l.IsReplay));
    }

    [Fact]
    public void APartialLineIsHeldBackUntilItIsFinished()
    {
        // VRChat is writing to this file while it is being read. Half a line is not a line.
        Write("output_log_a.txt", "complete\n");
        var tail = Tail();
        tail.ReadPending();

        Append("output_log_a.txt", "half a li");
        Assert.Empty(tail.ReadPending());

        Append("output_log_a.txt", "ne\n");
        Assert.Equal(["half a line"], tail.ReadPending().Select(l => l.Text));
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossTwoWritesIsNotCorrupted()
    {
        Write("output_log_a.txt", "");
        var tail = Tail();
        tail.ReadPending();

        var bytes = Encoding.UTF8.GetBytes("ΛƧƬΛ\n");
        var path = Path.Combine(_directory, "output_log_a.txt");
        File.WriteAllBytes(path, bytes[..3]);
        Assert.Empty(tail.ReadPending());

        using (var stream = new FileStream(path, FileMode.Append))
            stream.Write(bytes, 3, bytes.Length - 3);

        Assert.Equal(["ΛƧƬΛ"], tail.ReadPending().Select(l => l.Text));
    }

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        Write("output_log_a.txt", "one\r\ntwo\r\n");

        Assert.Equal(["one", "two"], Tail().ReadPending().Select(l => l.Text));
    }

    [Fact]
    public void FollowsTheLogIntoANewFileWhenVRChatRestarts()
    {
        Write("output_log_2026-09-03_20-26-45.txt", "old session\n");
        var tail = Tail();
        tail.ReadPending();

        var newer = Write("output_log_2026-09-04_09-00-00.txt", "new session\n");
        File.SetLastWriteTimeUtc(newer, File.GetLastWriteTimeUtc(newer).AddMinutes(5));

        var read = tail.ReadPending();

        // A fresh file is a fresh VRChat launch, so its contents are live rather than replay.
        Assert.Equal(["new session"], read.Select(l => l.Text));
        Assert.All(read, l => Assert.False(l.IsReplay));
        Assert.EndsWith("output_log_2026-09-04_09-00-00.txt", tail.CurrentFile);
    }

    [Fact]
    public void ALogThatAppearsAfterStartupIsLiveRatherThanHistory()
    {
        // The common case: Modbot is in the tray, VRChat is not running, and then it launches.
        // Everything that log says is happening now, and treating it as history already reported
        // would silently discard a whole session.
        var tail = Tail();
        Assert.Empty(tail.ReadPending());

        Write("output_log_2026-09-03_20-26-45.txt", "the session starts here\n");

        var read = tail.ReadPending();
        Assert.Equal(["the session starts here"], read.Select(l => l.Text));
        Assert.All(read, l => Assert.False(l.IsReplay));
    }

    [Fact]
    public void RereadsFromTheStartIfTheFileIsTruncated()
    {
        // A rotation that reuses the name, or a log cleared in place. Keeping the old offset would
        // silently skip everything written afterwards.
        Write("output_log_a.txt", "line one\nline two\nline three\n");
        var tail = Tail();
        tail.ReadPending();

        Write("output_log_a.txt", "fresh\n");

        Assert.Equal(["fresh"], tail.ReadPending().Select(l => l.Text));
    }

    [Fact]
    public void ReadsAFileVRChatStillHasOpenForWriting()
    {
        var path = Path.Combine(_directory, "output_log_a.txt");
        using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(held, Encoding.UTF8) { AutoFlush = true };
        writer.Write("while open\n");

        Assert.Equal(["while open"], Tail().ReadPending().Select(l => l.Text));
    }

    [Fact]
    public void CountsWhatItHasRead()
    {
        Write("output_log_a.txt", "one\ntwo\nthree\n");
        var tail = Tail();
        tail.ReadPending();

        Assert.Equal(3, tail.LinesRead);
    }

    [Fact]
    public void ReadsTheRealFixtureEndToEnd()
    {
        File.Copy(LogFixture.Path, Path.Combine(_directory, "output_log_2026-09-03_20-26-45.txt"));

        Assert.Equal(754, Tail().ReadPending().Count);
    }
}
