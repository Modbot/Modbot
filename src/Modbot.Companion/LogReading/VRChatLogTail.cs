using System.Text;

namespace Modbot.Companion.LogReading;

/// <summary>One line read from the log, and whether it is history or news.</summary>
/// <param name="IsReplay">
/// True for lines that were already in the file when Modbot started. They are read so the client
/// can work out which instance the moderator is currently in, and then <em>not</em> reported —
/// re-sending hours of old observations on every restart would be wrong.
/// </param>
public readonly record struct TailedLine(string Text, bool IsReplay);

/// <summary>
/// Follows VRChat's output log as it is written.
/// </summary>
/// <remarks>
/// <para><strong>What this reads, exactly.</strong> One folder — VRChat's own log folder, which on
/// Windows is <c>%USERPROFILE%\AppData\LocalLow\VRChat\VRChat</c> — and inside it, only files named
/// <c>output_log_*.txt</c>, which VRChat writes for its own diagnostics. You can open the same file
/// in Notepad and read every byte Modbot reads. Nothing else on the disk is touched: not Steam's
/// configuration, not VRChat's cache, not your documents.</para>
/// <para><strong>What it does with them.</strong> Hands each completed line to the parser. It keeps
/// a byte offset so it does not re-read what it has already seen, and that offset is all the state
/// there is. No copy of the log is made, and no line is written anywhere.</para>
/// <para><strong>What leaves the machine.</strong> Nothing, from here. Transmission happens much
/// later and only for the handful of recognised event types; see <c>ClientEvent</c>.</para>
/// <para><strong>Tolerating reality.</strong> VRChat may not be installed, may not be running, may
/// be mid-write, or may have restarted into a new log file. All four are ordinary states and none
/// of them is an error.</para>
/// </remarks>
public sealed class VRChatLogTail
{
    /// <summary>VRChat's own naming. Anything else in that folder is not read.</summary>
    public const string LogFilePattern = "output_log_*.txt";

    /// <summary>
    /// How much of a log is read in one pass. VRChat's log grows to hundreds of megabytes in a long
    /// session, and the first pass over one used to read all of it into a single array, a single
    /// string and a single split -- three copies of the file in memory at once. Now a pass takes
    /// this much and the next pass takes the next slice, from the offset the last one left behind.
    /// </summary>
    public const int DefaultMaxBytesPerPass = 8 * 1024 * 1024;

    private readonly string _directory;
    private readonly int _maxBytesPerPass;

    private string? _currentFile;
    private long _position;
    private bool _primed;
    private bool _replaying;

    public VRChatLogTail(string directory, int maxBytesPerPass = DefaultMaxBytesPerPass)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytesPerPass, 1);
        _directory = directory;
        _maxBytesPerPass = maxBytesPerPass;
    }

    /// <summary>The log file currently being followed, or <c>null</c> when there is none.</summary>
    public string? CurrentFile => _currentFile;

    /// <summary>How many lines have been handed out. Feeds the "have I stopped understanding the
    /// log" health check, which is the only thing standing between a format change and weeks of
    /// silently missing history.</summary>
    public long LinesRead { get; private set; }

    /// <summary>
    /// Where VRChat writes its logs on this machine.
    /// </summary>
    /// <remarks>
    /// Built from the user profile path rather than read from Steam's configuration. Reading
    /// another application's config would be both unnecessary and exactly the behaviour that makes
    /// a tool like this look like something worse than it is — M3 2.3.1.
    /// </remarks>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData",
        "LocalLow",
        "VRChat",
        "VRChat");

    /// <summary>
    /// Returns every complete line written since the last call. Never throws for the ordinary
    /// failures — a missing folder, a file being replaced underneath it, a locked handle — because
    /// a client that dies when VRChat restarts reports nothing for the rest of the evening.
    /// </summary>
    public IReadOnlyList<TailedLine> ReadPending()
    {
        // Whether this is the first look at the folder at all. It flips even when the folder is
        // empty: if VRChat was not running when Modbot started, the log it writes when it does
        // start is current events, not history, and must not be discarded as replay.
        var firstPass = !_primed;
        _primed = true;

        var newest = NewestLog();
        if (newest is null)
            return [];

        if (!string.Equals(newest, _currentFile, StringComparison.OrdinalIgnoreCase))
        {
            // A new log file means VRChat restarted, and its contents are current. Only a file
            // that was already sitting there when Modbot started is history -- the moderator
            // launched the client mid-session, and everything before this moment has either been
            // reported already or was never going to be. The flag stays up until the reader has
            // reached the end of that file, however many passes that takes.
            _replaying = firstPass;
            _currentFile = newest;
            _position = 0;
        }

        var replay = _replaying;
        var reachedEnd = false;
        byte[] chunk;
        try
        {
            // FileShare.ReadWrite | Delete: VRChat holds this file open for writing and may delete
            // or replace it at any moment. Anything stricter would fail on a running game.
            using var stream = new FileStream(
                newest,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < _position)
            {
                // Truncated or rotated in place. Keeping the old offset would skip everything
                // written afterwards, silently, which is the failure mode this whole subsystem is
                // built to avoid.
                _position = 0;
            }

            stream.Position = _position;
            var available = stream.Length - _position;
            if (available <= 0)
            {
                _replaying = false;
                return [];
            }

            var pending = (int)Math.Min(available, _maxBytesPerPass);
            reachedEnd = pending == available;

            chunk = new byte[pending];
            var filled = stream.ReadAtLeast(chunk, pending, throwOnEndOfStream: false);
            if (filled < pending)
            {
                chunk = chunk[..filled];
                reachedEnd = false;
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        // Stop at the last newline. What follows it is either a half-written line or a multi-byte
        // character VRChat has not finished writing; either way it is not ours yet, and the offset
        // is left behind it so the next pass picks it up whole.
        var lastNewline = Array.LastIndexOf(chunk, (byte)'\n');
        if (lastNewline < 0)
            return [];

        var text = Encoding.UTF8.GetString(chunk, 0, lastNewline + 1);
        _position += lastNewline + 1;

        // Everything that was in the file when the reader got to its end is history; whatever is
        // appended after this point is live. A slice that stopped short of the end -- or short of
        // a newline -- leaves the flag up for the next pass.
        if (reachedEnd && lastNewline == chunk.Length - 1)
            _replaying = false;

        // A byte-order mark, if the file has one, would otherwise ride along on the first line and
        // stop it matching any known shape.
        text = text.TrimStart('﻿');

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<TailedLine>(lines.Length);
        foreach (var line in lines)
            result.Add(new TailedLine(line.TrimEnd('\r'), replay));

        LinesRead += result.Count;
        return result;
    }

    /// <summary>
    /// The log VRChat is writing to now — the most recently modified one, because a moderator may
    /// have old logs sitting in the folder and only one of them is live.
    /// </summary>
    private string? NewestLog()
    {
        try
        {
            string? newest = null;
            var newestAt = DateTime.MinValue;

            foreach (var path in Directory.EnumerateFiles(_directory, LogFilePattern))
            {
                var writtenAt = File.GetLastWriteTimeUtc(path);
                if (newest is null || writtenAt > newestAt)
                {
                    newest = path;
                    newestAt = writtenAt;
                }
            }

            return newest;
        }
        catch (DirectoryNotFoundException)
        {
            // VRChat has never run on this machine, or is not installed. Ordinary, not an error.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
