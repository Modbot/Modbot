using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace Modbot.Companion.Listening;

/// <summary>How a download ended.</summary>
public enum PhraseDownloadOutcome
{
    /// <summary>Downloaded, checked, unpacked, ready.</summary>
    Done,

    /// <summary>It was already there and nothing was fetched.</summary>
    AlreadyPresent,

    /// <summary>The file arrived but was not the pinned file. Thrown away.</summary>
    WrongFile,

    /// <summary>The address could not be reached, or did not answer with the file.</summary>
    Unreachable,

    /// <summary>Something went wrong writing or unpacking on this PC.</summary>
    Failed,
}

public sealed record PhraseDownloadResult(PhraseDownloadOutcome Outcome, string? Detail = null)
{
    public bool Ready => Outcome is PhraseDownloadOutcome.Done or PhraseDownloadOutcome.AlreadyPresent;
}

/// <summary>
/// Fetches the phrase model, once, when the moderator first turns listening on.
/// </summary>
/// <remarks>
/// <para><strong>What leaves the machine.</strong> One HTTP GET, to the one address pinned in
/// <see cref="PhraseModel.Default"/> on GitHub, with nothing attached: no token, no id, nothing
/// about you or your pairings. GitHub sees your IP address, as it does for any download. This is
/// the only thing listening for a phrase ever sends, and it is sent only when the model is not yet
/// on the disk. Nothing the microphone hears is sent anywhere, ever, by this file or by any
/// other.</para>
/// <para><strong>What is written to your disk.</strong> The download, into the companion's own
/// <c>phrases</c> folder, first as a temporary file and then, once its SHA-256 matches the pinned
/// hash exactly, unpacked into a folder named for the model. Only five files are kept: the three
/// parts of the model, its vocabulary, and the list of phrases it listens for, which is the only
/// form the engine takes them in. The archive's spare 8-bit copies, its sample recordings and its
/// readme are not written at all. Beside them goes a small marker saying what it is and where it came
/// from. A file whose hash or size does not match is deleted, not used. An archive entry that
/// would land outside that folder is refused.</para>
/// <para><strong>What is removed from your disk.</strong> Once the pinned model is in place, any
/// other model folder beside it — an older one from a client before this one. Never before: a
/// download that fails or is abandoned leaves the PC exactly as it was.</para>
/// <para><strong>What this reads.</strong> The temporary file it just wrote, to hash and unpack it.
/// Nothing else, and no microphone: this file cannot open one and the source guard fails the build
/// if it learns how.</para>
/// </remarks>
public sealed class PhraseDownload
{
    private readonly HttpClient _http;

    public PhraseDownload(HttpClient http)
    {
        _http = http;
    }

    /// <param name="progress">0.0 to 1.0 as bytes arrive, then 1.0 once unpacked.</param>
    public async Task<PhraseDownloadResult> RunAsync(
        PhraseModel model,
        string phrasesFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(phrasesFolder);

        if (model.IsPresent(phrasesFolder))
        {
            RemoveOtherModels(model, phrasesFolder);
            return new PhraseDownloadResult(PhraseDownloadOutcome.AlreadyPresent);
        }

        var archive = Path.Combine(phrasesFolder, $"{model.Name}.download");
        var unpacking = Path.Combine(phrasesFolder, $"{model.Name}.unpacking");

        try
        {
            Directory.CreateDirectory(phrasesFolder);

            var fetched = await FetchAsync(model, archive, progress, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
                return fetched;

            if (Directory.Exists(unpacking))
                Directory.Delete(unpacking, recursive: true);

            Unpack(model, archive, unpacking);

            if (!File.Exists(Path.Combine(unpacking, model.EncoderFile))
                || !File.Exists(Path.Combine(unpacking, model.DecoderFile))
                || !File.Exists(Path.Combine(unpacking, model.JoinerFile))
                || !File.Exists(Path.Combine(unpacking, model.TokensFile)))
            {
                Directory.Delete(unpacking, recursive: true);
                return new PhraseDownloadResult(PhraseDownloadOutcome.WrongFile, "The archive did not hold the model's files.");
            }

            // Written rather than downloaded: the phrases are Modbot's own and come from
            // PhraseModel. They go in a file beside the model because that is the only way the
            // engine takes them, and because a folder whose every file has a reason to be there is
            // one somebody can check.
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(unpacking, PhraseModel.PhrasesFile), model.PhrasesText(), utf8);
            File.WriteAllText(Path.Combine(unpacking, PhraseModel.MarkerFile), model.MarkerText(), utf8);

            var folder = model.Folder(phrasesFolder);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            Directory.Move(unpacking, folder);
            RemoveOtherModels(model, phrasesFolder);
            progress?.Report(1.0);

            return new PhraseDownloadResult(PhraseDownloadOutcome.Done);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PhraseDownloadResult(PhraseDownloadOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(archive);
        }
    }

    /// <summary>
    /// Takes away every model folder that is not the pinned one, now that the pinned one is there.
    /// </summary>
    /// <remarks>
    /// Only ever called once the pinned model is on the disk, so nothing can leave a PC unable to
    /// listen. A folder that will not delete — open in a window, held by a virus scanner — is left
    /// alone and tried again next time.
    /// </remarks>
    private static void RemoveOtherModels(PhraseModel model, string phrasesFolder)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(phrasesFolder))
            {
                if (string.Equals(Path.GetFileName(folder), model.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for next time. An old model taking up room is not a fault worth reporting.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // The folder is unreadable; there is nothing to tidy and nothing to say.
        }
    }

    /// <summary>
    /// Streams the file to disk while hashing it. Returns null when it arrived whole and matched,
    /// otherwise the result to hand back.
    /// </summary>
    private async Task<PhraseDownloadResult?> FetchAsync(
        PhraseModel model, string archive, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new PhraseDownloadResult(PhraseDownloadOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PhraseDownloadResult(PhraseDownloadOutcome.Unreachable, "The download timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new PhraseDownloadResult(PhraseDownloadOutcome.Unreachable, $"The address answered {(int)response.StatusCode}.");

            if (response.Content.Headers.ContentLength is { } length && length != model.Size)
                return new PhraseDownloadResult(PhraseDownloadOutcome.WrongFile, $"The file is {length:N0} bytes; expected {model.Size:N0}.");

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
                        return new PhraseDownloadResult(PhraseDownloadOutcome.WrongFile, $"The file is longer than the expected {model.Size:N0} bytes.");

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(Math.Min(0.99, total / (double)model.Size));
                }
            }

            if (total != model.Size)
                return new PhraseDownloadResult(PhraseDownloadOutcome.WrongFile, $"The file is {total:N0} bytes; expected {model.Size:N0}.");

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                return new PhraseDownloadResult(PhraseDownloadOutcome.WrongFile, $"The file's SHA-256 is {actual}; expected {model.Sha256}.");
        }

        return null;
    }

    /// <summary>
    /// Unpacks the four files the engine needs out of the tar.bz2, and nothing else.
    /// </summary>
    /// <remarks>
    /// The archive also carries 8-bit copies of the three model parts, two sample recordings, a
    /// readme and the vocabulary builder the phrases in <see cref="PhraseModel"/> were made with.
    /// None of it is used, so none of it is written: about 7 MB that never lands, and a folder
    /// whose every file has a reason to be there.
    /// </remarks>
    private static void Unpack(PhraseModel model, string archive, string into)
    {
        Directory.CreateDirectory(into);
        var root = Path.GetFullPath(into + Path.DirectorySeparatorChar);

        var wanted = new HashSet<string>(
            [model.EncoderFile, model.DecoderFile, model.JoinerFile, model.TokensFile],
            StringComparer.Ordinal);

        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bzip2 = new BZip2InputStream(file);
        using var tar = new TarReader(bzip2, leaveOpen: true);

        while (tar.GetNextEntry() is { } entry)
        {
            var relative = Relative(model, entry.Name);
            if (relative is null || !wanted.Contains(relative))
                continue;

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                continue;

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"The archive tried to write outside its folder: {entry.Name}");

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>The path inside the model's folder, or null for the top-level folder entry itself.</summary>
    private static string? Relative(PhraseModel model, string entryName)
    {
        var name = entryName.Replace('\\', '/').TrimStart('.', '/');
        var prefix = model.ArchiveFolder + "/";

        if (name.StartsWith(prefix, StringComparison.Ordinal))
            name = name[prefix.Length..];
        else if (string.Equals(name, model.ArchiveFolder, StringComparison.Ordinal))
            return null;

        name = name.TrimEnd('/');
        return name.Length == 0 ? null : name;
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
