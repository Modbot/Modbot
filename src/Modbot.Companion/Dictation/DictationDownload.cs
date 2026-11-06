using System.Formats.Tar;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;

namespace Modbot.Companion.Dictation;

/// <summary>How a download ended: ready, or a sentence saying why not.</summary>
public sealed record DictationDownloadResult(bool Ready, string? Problem = null)
{
    public static DictationDownloadResult Done { get; } = new(true);
}

/// <summary>
/// Fetches the speech model, once, the first time somebody taps Dictate.
/// </summary>
/// <remarks>
/// <para><strong>What leaves the machine.</strong> One HTTP GET, to the one address pinned in
/// <see cref="DictationModel.Default"/> on GitHub, with nothing attached: no token, no id, nothing
/// about you or your pairings. GitHub sees your IP address, as it does for any download. It is sent
/// only when the model is not yet on the disk. Nothing a microphone hears is sent anywhere, ever,
/// by this file or by any other.</para>
/// <para><strong>What is written to your disk.</strong> The download, into the companion's own
/// <c>dictation</c> folder, first as a temporary file and then, once its SHA-256 and size match the
/// pinned ones exactly, unpacked into a folder named for the model. Only the five files the engine
/// needs are kept; the archive's sample recordings, licence and readme are not written at all. A
/// file whose hash or size does not match is deleted, not used. An archive entry that would land
/// outside that folder is refused.</para>
/// <para><strong>What this reads.</strong> The temporary file it just wrote, to hash and unpack it.
/// Nothing else, and no microphone: this file cannot open one and the source guard fails the build
/// if it learns how.</para>
/// </remarks>
public sealed class DictationDownload
{
    private readonly HttpClient _http;

    public DictationDownload(HttpClient http)
    {
        _http = http;
    }

    /// <param name="progress">0.0 to 1.0 as bytes arrive, then 1.0 once unpacked.</param>
    public async Task<DictationDownloadResult> RunAsync(
        DictationModel model,
        string dictationFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictationFolder);

        if (model.IsPresent(dictationFolder))
            return DictationDownloadResult.Done;

        var archive = Path.Combine(dictationFolder, $"{model.Name}.download");
        var unpacking = Path.Combine(dictationFolder, $"{model.Name}.unpacking");

        try
        {
            Directory.CreateDirectory(dictationFolder);

            var fetched = await FetchAsync(model, archive, progress, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
                return fetched;

            if (Directory.Exists(unpacking))
                Directory.Delete(unpacking, recursive: true);

            Unpack(model, archive, unpacking);

            if (DictationModel.Files.Any(f => !File.Exists(Path.Combine(unpacking, f))))
            {
                Directory.Delete(unpacking, recursive: true);
                return new DictationDownloadResult(false, "The archive did not hold the model's files.");
            }

            File.WriteAllText(Path.Combine(unpacking, DictationModel.MarkerFile), model.MarkerText());

            var folder = model.Folder(dictationFolder);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            Directory.Move(unpacking, folder);
            progress?.Report(1.0);

            return DictationDownloadResult.Done;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new DictationDownloadResult(false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(archive);
        }
    }

    /// <summary>
    /// Streams the file to disk while hashing it. Returns null when it arrived whole and matched,
    /// otherwise the result to hand back.
    /// </summary>
    private async Task<DictationDownloadResult?> FetchAsync(
        DictationModel model, string archive, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new DictationDownloadResult(false, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DictationDownloadResult(false, "The download timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new DictationDownloadResult(false, $"The address answered {(int)response.StatusCode}.");

            if (response.Content.Headers.ContentLength is { } length && length != model.Size)
                return new DictationDownloadResult(false, $"The file is {length:N0} bytes; expected {model.Size:N0}.");

            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > model.Size)
                        return new DictationDownloadResult(false, $"The file is longer than the expected {model.Size:N0} bytes.");

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(Math.Min(0.99, total / (double)model.Size));
                }
            }

            if (total != model.Size)
                return new DictationDownloadResult(false, $"The file is {total:N0} bytes; expected {model.Size:N0}.");

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                return new DictationDownloadResult(false, $"The file's SHA-256 is {actual}; expected {model.Sha256}.");
        }

        return null;
    }

    /// <summary>Unpacks the five files the engine needs out of the tar.bz2, and nothing else.</summary>
    private static void Unpack(DictationModel model, string archive, string into)
    {
        Directory.CreateDirectory(into);
        var root = Path.GetFullPath(into + Path.DirectorySeparatorChar);
        var wanted = new HashSet<string>(DictationModel.Files, StringComparer.Ordinal);
        var prefix = model.ArchiveFolder + "/";

        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bzip2 = new BZip2InputStream(file);
        using var tar = new TarReader(bzip2, leaveOpen: true);

        while (tar.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/').TrimStart('.', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var relative = name[prefix.Length..];
            if (!wanted.Contains(relative))
                continue;

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                continue;

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"The archive tried to write outside its folder: {entry.Name}");

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is replaced by the next attempt.
        }
    }
}
