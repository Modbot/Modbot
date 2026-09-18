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
/// <param name="Path">Where the bytes are, so they can be streamed rather than read into memory.</param>
/// <param name="ContentType">The type VRChat gave the bytes when they were fetched.</param>
/// <param name="Tag">The cache key, which is also the ETag: a versioned address never changes.</param>
public sealed record CachedFile(string Path, string ContentType, string Tag);

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

    /// <summary>The cached file for an address, or null when it has never been fetched.</summary>
    public CachedFile? Find(string url)
    {
        if (!IsOn)
            return null;

        var key = KeyFor(url);
        var path = PathFor(key);

        try
        {
            if (!File.Exists(path) || !File.Exists(path + TypeSuffix))
                return null;

            var contentType = File.ReadAllText(path + TypeSuffix).Trim();

            // A type file that was truncated, or one whose type Modbot would no longer serve,
            // is treated as a miss rather than as something to hand a browser.
            return VRChatFiles.IsShowable(contentType) ? new CachedFile(path, contentType, key) : null;
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

    /// <summary>
    /// Stores a fetched file and then, if the folder is over <paramref name="maxBytes"/>, deletes
    /// the files fetched longest ago until it is not.
    /// </summary>
    /// <remarks>
    /// Failing to cache is not failing to serve: the bytes are already in hand and the caller
    /// returns them either way, so every problem here is logged and swallowed.
    /// </remarks>
    public async Task StoreAsync(string url, VRChatFile file, long maxBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(file);

        if (!IsOn || maxBytes <= 0 || file.Bytes.LongLength > maxBytes)
            return;

        var key = KeyFor(url);
        var path = PathFor(key);
        var partial = path + PartialSuffix;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written beside the key and renamed onto it, so a crashed fetch never leaves half a
            // picture at an address the next request would believe.
            await File.WriteAllBytesAsync(partial, file.Bytes, ct).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);

            await File.WriteAllTextAsync(path + TypeSuffix, file.ContentType, ct).ConfigureAwait(false);

            // Modbot's clock, not the machine's: it is what the sweep orders by.
            var now = _clock.UtcNow.UtcDateTime;
            File.SetLastWriteTimeUtc(path, now);
            File.SetLastWriteTimeUtc(path + TypeSuffix, now);

            Sweep(maxBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning(exception, "Could not cache the VRChat file at {Url}", url);
            TryDelete(partial);
        }
    }

    /// <summary>
    /// Deletes oldest-first until the folder is under the cap.
    /// </summary>
    /// <remarks>
    /// Oldest by when it was fetched, not by when it was last looked at. Keeping the popular ones
    /// would need a write on every read, and a write on every read is how a cache becomes slower
    /// than the thing it is caching.
    /// </remarks>
    public void Sweep(long maxBytes)
    {
        if (!IsOn)
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
