using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace Modbot.Companion.Voice;

/// <summary>How a download ended.</summary>
public enum VoiceDownloadOutcome
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

public sealed record VoiceDownloadResult(VoiceDownloadOutcome Outcome, string? Detail = null)
{
    public bool Ready => Outcome is VoiceDownloadOutcome.Done or VoiceDownloadOutcome.AlreadyPresent;
}

/// <summary>
/// Fetches the voice, once, when the moderator first turns the voice on.
/// </summary>
/// <remarks>
/// <para><strong>What leaves the machine.</strong> One HTTP GET, to the one address pinned in
/// <see cref="VoiceModel.Default"/> on GitHub, with nothing attached: no token, no id, nothing
/// about you or your pairings. GitHub sees your IP address, as it does for any download. This is
/// the only thing the voice ever sends, and it is sent only when the voice is not yet on the disk.
/// The voice is never bundled into the installer, so the installer stays small and a moderator
/// who never turns the voice on never fetches it.</para>
/// <para><strong>What is written to your disk.</strong> The download, into the companion's own
/// <c>voices</c> folder, first as a temporary file and then, once its SHA-256 matches the pinned
/// hash exactly, unpacked into a folder named for the voice: the model, the voices it can speak
/// as, its token list, the espeak-ng data, and a small marker saying what it is and where it came
/// from. A file whose hash or size does not match is deleted, not used. An archive entry that
/// would land outside that folder is refused.</para>
/// <para><strong>What is removed from your disk.</strong> Once the pinned voice is in place, any
/// other voice folder beside it — an older voice from a client before this one. Never before: a
/// download that fails or is abandoned leaves the PC exactly as it was.</para>
/// <para><strong>What this reads.</strong> The temporary file it just wrote, to hash and unpack it.
/// Nothing else.</para>
/// </remarks>
public sealed class VoiceDownload
{
    private readonly HttpClient _http;

    public VoiceDownload(HttpClient http)
    {
        _http = http;
    }

    /// <param name="progress">0.0 to 1.0 as bytes arrive, then 1.0 once unpacked.</param>
    public async Task<VoiceDownloadResult> RunAsync(
        VoiceModel model,
        string voicesFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(voicesFolder);

        if (model.IsPresent(voicesFolder))
        {
            RemoveOtherVoices(model, voicesFolder);
            return new VoiceDownloadResult(VoiceDownloadOutcome.AlreadyPresent);
        }

        var archive = Path.Combine(voicesFolder, $"{model.Name}.download");
        var unpacking = Path.Combine(voicesFolder, $"{model.Name}.unpacking");

        try
        {
            Directory.CreateDirectory(voicesFolder);

            var fetched = await FetchAsync(model, archive, progress, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
                return fetched;

            if (Directory.Exists(unpacking))
                Directory.Delete(unpacking, recursive: true);

            Unpack(model, archive, unpacking);

            if (!File.Exists(Path.Combine(unpacking, model.ModelFile))
                || !File.Exists(Path.Combine(unpacking, model.VoicesFile))
                || !File.Exists(Path.Combine(unpacking, model.TokensFile))
                || !Directory.Exists(Path.Combine(unpacking, model.DataFolder)))
            {
                Directory.Delete(unpacking, recursive: true);
                return new VoiceDownloadResult(VoiceDownloadOutcome.WrongFile, "The archive did not hold the voice's files.");
            }

            File.WriteAllText(Path.Combine(unpacking, VoiceModel.MarkerFile), model.MarkerText(), new UTF8Encoding(false));

            var folder = model.Folder(voicesFolder);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            Directory.Move(unpacking, folder);
            RemoveOtherVoices(model, voicesFolder);
            progress?.Report(1.0);

            return new VoiceDownloadResult(VoiceDownloadOutcome.Done);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new VoiceDownloadResult(VoiceDownloadOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(archive);
        }
    }

    /// <summary>
    /// Takes away every voice folder that is not the pinned one, now that the pinned one is there.
    /// </summary>
    /// <remarks>
    /// <para>A client that changes which voice it speaks with leaves the old one behind, and the
    /// old one is dead weight: the client can only load the voice it has pinned, so keeping it as
    /// a fallback would be hundreds of megabytes nothing is able to speak with.</para>
    /// <para>Only ever called once the pinned voice is on the disk, so nothing can leave a PC with
    /// no voice at all. A folder that will not delete — open in a window, held by a virus scanner —
    /// is left alone and tried again next time; the voice works either way.</para>
    /// </remarks>
    private static void RemoveOtherVoices(VoiceModel model, string voicesFolder)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(voicesFolder))
            {
                if (string.Equals(Path.GetFileName(folder), model.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for next time. An old voice taking up room is not a fault worth reporting.
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
    private async Task<VoiceDownloadResult?> FetchAsync(
        VoiceModel model, string archive, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new VoiceDownloadResult(VoiceDownloadOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new VoiceDownloadResult(VoiceDownloadOutcome.Unreachable, "The download timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new VoiceDownloadResult(VoiceDownloadOutcome.Unreachable, $"The address answered {(int)response.StatusCode}.");

            if (response.Content.Headers.ContentLength is { } length && length != model.Size)
                return new VoiceDownloadResult(VoiceDownloadOutcome.WrongFile, $"The file is {length:N0} bytes; expected {model.Size:N0}.");

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
                        return new VoiceDownloadResult(VoiceDownloadOutcome.WrongFile, $"The file is longer than the expected {model.Size:N0} bytes.");

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(Math.Min(0.99, total / (double)model.Size));
                }
            }

            if (total != model.Size)
                return new VoiceDownloadResult(VoiceDownloadOutcome.WrongFile, $"The file is {total:N0} bytes; expected {model.Size:N0}.");

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                return new VoiceDownloadResult(VoiceDownloadOutcome.WrongFile, $"The file's SHA-256 is {actual}; expected {model.Sha256}.");
        }

        return null;
    }

    /// <summary>
    /// Unpacks the tar.bz2 into a folder, dropping the archive's own top-level folder and refusing
    /// any entry that would land anywhere else.
    /// </summary>
    private static void Unpack(VoiceModel model, string archive, string into)
    {
        Directory.CreateDirectory(into);
        var root = Path.GetFullPath(into + Path.DirectorySeparatorChar);

        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bzip2 = new BZip2InputStream(file);
        using var tar = new TarReader(bzip2, leaveOpen: true);

        while (tar.GetNextEntry() is { } entry)
        {
            var relative = Relative(model, entry.Name);
            if (relative is null)
                continue;

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"The archive tried to write outside its folder: {entry.Name}");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    break;

                // Links, devices and the rest have no business in a voice.
                default:
                    break;
            }
        }
    }

    /// <summary>The path inside the voice's folder, or null for the top-level folder entry itself.</summary>
    private static string? Relative(VoiceModel model, string entryName)
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
