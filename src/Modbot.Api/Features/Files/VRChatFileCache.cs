using System.Security.Cryptography;
using System.Text;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Files;
using Serilog;

namespace Modbot.Api.Features.Files;

/// <summary>Where the cache of VRChat pictures lives.</summary>
/// <remarks>
/// The folder sits in the same data directory the evidence store uses -- <c>/app/data</c> in the
/// container, the mount point the evidence design documents -- because it is the same disk and an
/// operator who mounted one volume should not have to mount a second.
/// </remarks>
public sealed record VRChatFileCacheOptions
{
    /// <summary>The cache folder. Empty means the cache is off and every picture is fetched.</summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>The folder name under the data directory.</summary>
    public const string FolderName = "vrchat-files";

    /// <summary>The cache folder for a deployment whose content root is <paramref name="contentRoot"/>.</summary>
    public static VRChatFileCacheOptions Under(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        return new VRChatFileCacheOptions
        {
            Root = Path.Combine(Path.GetFullPath(contentRoot), "data", FolderName),
        };
    }
}

/// <summary>One cached file, as the endpoint serves it.</summary>
/// <param name="Content">
/// The bytes, already open, so they can be streamed rather than read into memory. <strong>The
/// caller owns this stream and must dispose it</strong> -- the result helper it is handed to
/// does. It is handed over open rather than as a path because a path is only a promise: the sweep
/// can delete the file, and a store of the same address can rename another file onto it, between
/// the moment the cache finds it and the moment the response reads it. A file that is already
/// open keeps the bytes it was opened on whatever happens to the name afterwards.
/// </param>
/// <param name="ContentType">The type VRChat gave the bytes when they were fetched.</param>
/// <param name="Tag">The cache key, which is also the ETag: a versioned address never changes.</param>
public sealed record CachedFile(Stream Content, string ContentType, string Tag);

/// <summary>
/// The pictures and videos Modbot has already fetched from VRChat, kept on disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A VRChat file address names a version, so its bytes never change.</strong> There is
/// therefore nothing to expire and no revalidation to do: a file that is here is the right file
/// for that address for ever, and the only reason to delete one is that the disk is finite. So
/// the cache has a size cap and no age at all, and when it is over the cap the files fetched
/// longest ago go first.
/// </para>
/// <para>
/// The key is the SHA-256 of the address exactly as it was asked for, which makes every path
/// under the folder hex -- there is no filename, no extension and no segment a caller chose, so
/// a traversal would need a new method before it could need a defence. The type VRChat gave the
/// bytes is written in a small file beside them, because a browser handed bytes with no type
/// guesses, and guessing is how an image becomes a script.
/// </para>
/// <para>
/// Times come from <see cref="IModbotClock"/> and are stamped onto the file, so the order the
/// sweep deletes in is Modbot's own clock rather than the machine's.
/// </para>
/// </remarks>
public sealed class VRChatFileCache
{
    /// <summary>What a content-type file is called, next to the bytes it describes.</summary>
    private const string TypeSuffix = ".type";

    /// <summary>A file still being written. Never served, always swept.</summary>
    private const string PartialSuffix = ".partial";

    private readonly string _root;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    /// <summary>
    /// The half-written files this process is writing right now, so the sweep does not delete one
    /// out from under the store that is filling it. Anything else ending in
    /// <see cref="PartialSuffix"/> was left by a run that died and is rubbish.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _writing =
        new(StringComparer.Ordinal);

    /// <summary>0 when no sweep is running, 1 when one is. See <see cref="Sweep"/>.</summary>
    private int _sweeping;

    public VRChatFileCache(VRChatFileCacheOptions options, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _root = string.IsNullOrWhiteSpace(options.Root) ? string.Empty : Path.GetFullPath(options.Root);
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Http);
    }

    /// <summary>False when no folder was configured: nothing is stored and nothing is found.</summary>
    public bool IsOn => _root.Length > 0;

    /// <summary>The folder, for a health read to name.</summary>
    public string Root => _root;

    /// <summary>The SHA-256 of an address, in lower-case hex. The cache key and the ETag.</summary>
    public static string KeyFor(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    }

    /// <summary>
    /// The cached file for an address, already open, or null when it has never been fetched.
    /// <strong>The caller disposes what it gets.</strong>
    /// </summary>
    /// <remarks>
    /// The bytes are opened here rather than looked for, because "it exists" and "I can read it"
    /// are different questions once more than one request is in the building: between an
    /// <c>Exists</c> and the response reading the path, the sweep can delete the file and a store
    /// of the same address can rename a new one onto it. Opening it first settles the question
    /// once -- the open file keeps its bytes however the name is reused -- and the share flags say
    /// that deleting and replacing the name while it is open is allowed, so a read in progress
    /// never blocks a sweep either.
    /// </remarks>
    public CachedFile? Find(string url)
    {
        if (!IsOn)
            return null;

        var key = KeyFor(url);
        var path = PathFor(key);
        Stream? content = null;

        try
        {
            content = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            // The type is written and put in place before the bytes are, so bytes that opened
            // always have a finished type file beside them.
            var contentType = File.ReadAllText(path + TypeSuffix).Trim();

            // A type file that was truncated, or one whose type Modbot would no longer serve,
            // is treated as a miss rather than as something to hand a browser.
            if (!VRChatFiles.IsShowable(contentType))
            {
                content.Dispose();
                return null;
            }

            return new CachedFile(content, contentType, key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            content?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Stores a fetched file and then, if the folder is over <paramref name="maxBytes"/>, deletes
    /// the files fetched longest ago until it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failing to cache is not failing to serve: the bytes are already in hand and the caller
    /// returns them either way, so every problem here is logged and swallowed.
    /// </para>
    /// <para>
    /// <strong>Every write goes to a name no other write can be using.</strong> A page of forty
    /// faces asks for some of the same pictures at once -- everyone without a picture of their own
    /// shares one address -- so two stores of one address happening together is ordinary, not
    /// exotic. Writing both to <c>&lt;key&gt;.partial</c> would have them filling one file at once,
    /// and the first rename would carry the second one's half-written bytes onto the key, where
    /// they would stay: nothing here ever expires, so a picture corrupted this way is corrupted
    /// for good. A name of its own per write means the rename is the only thing they share, and a
    /// rename is atomic.
    /// </para>
    /// <para>
    /// The type goes into place before the bytes do, so bytes a reader can open always have a
    /// finished type beside them. Between the two there is a type file with no picture, which
    /// reads as a miss, which is the right answer.
    /// </para>
    /// </remarks>
    public async Task StoreAsync(string url, VRChatFile file, long maxBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(file);

        if (!IsOn || maxBytes <= 0 || file.Bytes.LongLength > maxBytes)
            return;

        var key = KeyFor(url);
        var path = PathFor(key);

        // Modbot's clock, not the machine's: it is what the sweep orders by. Stamped on each file
        // before it is renamed into place, because a rename keeps the time and there is then no
        // moment at which the file is readable with the wrong one.
        var now = _clock.UtcNow.UtcDateTime;

        var bytesTemp = TempFor(path);
        var typeTemp = TempFor(path + TypeSuffix);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await WriteThenMoveAsync(typeTemp, path + TypeSuffix, Encoding.UTF8.GetBytes(file.ContentType), now, ct)
                .ConfigureAwait(false);

            await WriteThenMoveAsync(bytesTemp, path, file.Bytes, now, ct).ConfigureAwait(false);

            Sweep(maxBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning(exception, "Could not cache the VRChat file at {Url}", url);
        }
        finally
        {
            TryDelete(bytesTemp);
            TryDelete(typeTemp);
            _writing.TryRemove(bytesTemp, out _);
            _writing.TryRemove(typeTemp, out _);
        }
    }

    /// <summary>Fills a name nobody else is using, then renames it onto the real one.</summary>
    private async Task WriteThenMoveAsync(
        string temp, string destination, byte[] bytes, DateTime writtenAt, CancellationToken ct)
    {
        // Registered before the file exists, so the sweep cannot see it unclaimed at any point.
        _writing[temp] = 0;

        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(temp, writtenAt);
        File.Move(temp, destination, overwrite: true);

        _writing.TryRemove(temp, out _);
    }

    /// <summary>
    /// Deletes oldest-first until the folder is under the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Oldest by when it was fetched, not by when it was last looked at. Keeping the popular ones
    /// would need a write on every read, and a write on every read is how a cache becomes slower
    /// than the thing it is caching.
    /// </para>
    /// <para>
    /// <strong>One sweep at a time, and a store that arrives while one is running does not wait
    /// for it.</strong> A page of faces stores forty pictures at once; forty sweeps would walk the
    /// whole folder forty times over, delete into each other's enumerations, and -- because each
    /// one measures the folder for itself and then subtracts only what it deleted -- take it far
    /// below the cap between them. The next store sweeps, so skipping costs nothing: the cap is a
    /// limit on how large the folder gets, not a promise about any one moment.
    /// </para>
    /// </remarks>
    public void Sweep(long maxBytes)
    {
        if (!IsOn)
            return;

        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
            return;

        try
        {
            if (!Directory.Exists(_root))
                return;

            var files = new List<FileInfo>();
            long total = 0;

            foreach (var found in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (found.Name.EndsWith(PartialSuffix, StringComparison.Ordinal))
                {
                    // Anything half-written that this process is not writing was left by a run
                    // that died. Deleting one a store is still filling would make that store
                    // fail for no reason.
                    if (!_writing.ContainsKey(found.FullName))
                        TryDelete(found.FullName);

                    continue;
                }

                if (found.Name.EndsWith(TypeSuffix, StringComparison.Ordinal))
                    continue;

                files.Add(found);
                total += found.Length;
            }

            if (total <= maxBytes)
                return;

            foreach (var oldest in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= maxBytes)
                    break;

                total -= oldest.Length;
                TryDelete(oldest.FullName);
                TryDelete(oldest.FullName + TypeSuffix);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning(exception, "Could not sweep the VRChat file cache at {Root}", _root);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    /// <summary>What the folder currently holds, in bytes. For a settings screen to show.</summary>
    public long BytesHeld()
    {
        if (!IsOn || !Directory.Exists(_root))
            return 0;

        try
        {
            return new DirectoryInfo(_root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary><c>&lt;root&gt;/ab/cd/abcdef…</c> — two levels of shard, then the whole key.</summary>
    private string PathFor(string key) =>
        Path.Combine(_root, key[..2], key[2..4], key);

    /// <summary>
    /// A name for one write and one write only, beside the file it will become.
    /// </summary>
    /// <remarks>
    /// Still ending in <see cref="PartialSuffix"/>, so the sweep recognises anything left behind
    /// by a run that died and clears it.
    /// </remarks>
    private static string TempFor(string path) =>
        $"{path}.{Guid.NewGuid():n}{PartialSuffix}";

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Debug(exception, "Could not delete {Path} from the VRChat file cache", path);
        }
    }
}
