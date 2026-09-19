using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// The one download: it must be exactly the pinned file, unpacked exactly into the voice's
/// folder, or nothing at all.
/// </summary>
public class VoiceDownloadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-voice-download-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Voices => Path.Combine(_directory, "voices");

    /// <summary>Answers every GET with the same bytes, or a status, and counts the requests.</summary>
    private sealed class FakeServer(byte[]? body, HttpStatusCode status = HttpStatusCode.OK, bool lieAboutLength = false) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);

            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new ByteArrayContent(body);
                if (lieAboutLength)
                    response.Content.Headers.ContentLength = body.Length + 1;
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>A tar.bz2 shaped like the real one: a top folder holding the model, tokens and data.</summary>
    private static byte[] Archive(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var bzip2 = new BZip2OutputStream(output) { IsStreamOwner = false })
        using (var tar = new TarWriter(bzip2, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                if (name.EndsWith('/'))
                {
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, name));
                    continue;
                }

                var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                tar.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static readonly (string, string)[] GoodEntries =
    [
        ("kokoro-test/", ""),
        ("kokoro-test/model.onnx", "not really a model"),
        ("kokoro-test/voices.bin", "not really the voices"),
        ("kokoro-test/tokens.txt", "a 1\nb 2\n"),
        ("kokoro-test/README.md", "# test"),
        ("kokoro-test/espeak-ng-data/", ""),
        ("kokoro-test/espeak-ng-data/en_dict", "words"),
        ("kokoro-test/espeak-ng-data/lang/", ""),
        ("kokoro-test/espeak-ng-data/lang/en", "language en"),
    ];

    private static VoiceModel Model(byte[] archive, string? sha256 = null, long? size = null) => new(
        "kokoro-test",
        new Uri("https://example.test/voices/kokoro-test.tar.bz2"),
        sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive)),
        size ?? archive.Length,
        "kokoro-test",
        "model.onnx",
        "voices.bin",
        "tokens.txt",
        "espeak-ng-data",
        24_000,
        [new NamedVoice("Bella", 1), new NamedVoice("George", 9)]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownloadsChecksAndUnpacksThePinnedFile()
    {
        var archive = Archive(GoodEntries);
        var model = Model(archive);
        var server = new FakeServer(archive);
        var progress = new List<double>();

        var result = await new VoiceDownload(new HttpClient(server)).RunAsync(
            model, Voices, new SynchronousProgress(progress), Ct);

        Assert.Equal(VoiceDownloadOutcome.Done, result.Outcome);
        Assert.True(result.Ready);
        Assert.Equal([model.Url], server.Requests);

        Assert.True(model.IsPresent(Voices));
        Assert.Equal("not really a model", File.ReadAllText(model.ModelPath(Voices)));
        Assert.Equal("not really the voices", File.ReadAllText(model.VoicesPath(Voices)));
        Assert.Equal("a 1\nb 2\n", File.ReadAllText(model.TokensPath(Voices)));
        Assert.Equal("language en", File.ReadAllText(Path.Combine(model.DataPath(Voices), "lang", "en")));

        // The archive's own top folder is not kept, the temporary files are gone, and the marker
        // says what this is and where it came from.
        Assert.False(Directory.Exists(Path.Combine(model.Folder(Voices), "kokoro-test")));
        Assert.Equal([model.Name], Directory.GetFileSystemEntries(Voices).Select(Path.GetFileName));
        var marker = File.ReadAllText(model.MarkerPath(Voices));
        Assert.Contains(model.Sha256, marker, StringComparison.Ordinal);
        Assert.Contains(model.Url.ToString(), marker, StringComparison.Ordinal);

        Assert.Equal(1.0, progress[^1]);
        Assert.True(progress.Count >= 2);
    }

    [Fact]
    public async Task AFileWithTheWrongHashIsThrownAway()
    {
        var archive = Archive(GoodEntries);
        var model = Model(archive, sha256: new string('0', 64));

        var result = await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        Assert.Equal(VoiceDownloadOutcome.WrongFile, result.Outcome);
        Assert.Contains("SHA-256", result.Detail, StringComparison.Ordinal);
        Assert.False(model.IsPresent(Voices));
        Assert.Empty(Directory.GetFileSystemEntries(Voices));
    }

    [Fact]
    public async Task AFileOfTheWrongSizeIsThrownAway()
    {
        var archive = Archive(GoodEntries);

        var declared = await new VoiceDownload(new HttpClient(new FakeServer(archive, lieAboutLength: true)))
            .RunAsync(Model(archive), Voices, null, Ct);
        Assert.Equal(VoiceDownloadOutcome.WrongFile, declared.Outcome);

        var longer = await new VoiceDownload(new HttpClient(new FakeServer(archive)))
            .RunAsync(Model(archive, size: archive.Length - 10), Voices, null, Ct);
        Assert.Equal(VoiceDownloadOutcome.WrongFile, longer.Outcome);

        Assert.Empty(Directory.GetFileSystemEntries(Voices));
    }

    [Fact]
    public async Task AnAddressThatDoesNotAnswerIsUnreachable()
    {
        var model = Model(Archive(GoodEntries));

        var missing = await new VoiceDownload(new HttpClient(new FakeServer(null, HttpStatusCode.NotFound))).RunAsync(model, Voices, null, Ct);
        Assert.Equal(VoiceDownloadOutcome.Unreachable, missing.Outcome);

        var down = await new VoiceDownload(new HttpClient(new FailingServer())).RunAsync(model, Voices, null, Ct);
        Assert.Equal(VoiceDownloadOutcome.Unreachable, down.Outcome);

        Assert.False(model.IsPresent(Voices));
    }

    [Fact]
    public async Task AnArchiveMissingTheVoicesFilesIsNotAVoice()
    {
        var archive = Archive(("kokoro-test/", ""), ("kokoro-test/README.md", "nothing here"));
        var model = Model(archive);

        var result = await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        Assert.Equal(VoiceDownloadOutcome.WrongFile, result.Outcome);
        Assert.False(model.IsPresent(Voices));
        Assert.Empty(Directory.GetFileSystemEntries(Voices));
    }

    [Fact]
    public async Task AnEntryThatWouldLandOutsideTheFolderIsRefused()
    {
        var archive = Archive(
            ("kokoro-test/", ""),
            ("kokoro-test/model.onnx", "model"),
            ("kokoro-test/../../escaped.txt", "should never be written"));
        var model = Model(archive);

        var result = await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        Assert.Equal(VoiceDownloadOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_directory, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Voices, "escaped.txt")));
        Assert.False(model.IsPresent(Voices));
    }

    [Fact]
    public async Task AVoiceAlreadyThereIsNotFetchedAgain()
    {
        var archive = Archive(GoodEntries);
        var model = Model(archive);
        var server = new FakeServer(archive);
        var download = new VoiceDownload(new HttpClient(server));

        Assert.Equal(VoiceDownloadOutcome.Done, (await download.RunAsync(model, Voices, null, Ct)).Outcome);
        Assert.Equal(VoiceDownloadOutcome.AlreadyPresent, (await download.RunAsync(model, Voices, null, Ct)).Outcome);

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ADifferentPinnedHashMeansTheOldFolderDoesNotCount()
    {
        // A newer companion pinning a newer voice must fetch it, not trust a folder with the
        // right name left by the old one.
        var archive = Archive(GoodEntries);
        var model = Model(archive);
        await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        var newer = model with { Sha256 = new string('a', 64) };

        Assert.False(newer.IsPresent(Voices));
    }

    [Fact]
    public void TheRealVoiceIsPinnedToOneAddressAndOneHash()
    {
        var voice = VoiceModel.Default;

        Assert.Equal("https", voice.Url.Scheme);
        Assert.Equal("github.com", voice.Url.Host);
        Assert.Equal("/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2", voice.Url.AbsolutePath);
        Assert.Equal("912804855a04745fa77a30be545b3f9a5d15c4d66db00b88cbcd4921df605ac7", voice.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", voice.Sha256);
        Assert.Equal(319_625_534, voice.Size);
        Assert.Equal(24_000, voice.SampleRate);
        Assert.Equal("kokoro-en-v0_19", voice.Name);
        Assert.Equal("kokoro-en-v0_19", voice.ArchiveFolder);
        Assert.Equal("model.onnx", voice.ModelFile);
        Assert.Equal("voices.bin", voice.VoicesFile);
        Assert.Equal("tokens.txt", voice.TokensFile);
        Assert.Equal("espeak-ng-data", voice.DataFolder);
    }

    [Fact]
    public async Task AnArchiveWithNoVoicesFileIsNotAVoice()
    {
        // Kokoro keeps its voices in a file of their own beside the model; a folder without it
        // loads into an engine that cannot speak as anybody.
        var archive = Archive(GoodEntries.Where(e => !e.Item1.EndsWith("voices.bin", StringComparison.Ordinal)).ToArray());
        var model = Model(archive);

        var result = await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        Assert.Equal(VoiceDownloadOutcome.WrongFile, result.Outcome);
        Assert.False(model.IsPresent(Voices));
        Assert.Empty(Directory.GetFileSystemEntries(Voices));
    }

    [Fact]
    public async Task AnOlderVoiceIsTakenAwayOnceTheNewOneIsThere()
    {
        // A moderator upgrading from the Piper voice must not be left with both on the disk.
        var older = Path.Combine(Voices, "en_US-kristin-medium");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "en_US-kristin-medium.onnx"), "the old model");
        File.WriteAllText(Path.Combine(older, VoiceModel.MarkerFile), "{}");

        var archive = Archive(GoodEntries);
        var model = Model(archive);

        var result = await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        Assert.Equal(VoiceDownloadOutcome.Done, result.Outcome);
        Assert.True(model.IsPresent(Voices));
        Assert.False(Directory.Exists(older));
        Assert.Equal([model.Name], Directory.GetFileSystemEntries(Voices).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnOlderVoiceSurvivesADownloadThatFailed()
    {
        // Nothing is taken away until the new voice is actually there, so an abandoned or broken
        // download leaves the PC exactly as it was.
        var older = Path.Combine(Voices, "en_US-kristin-medium");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, VoiceModel.MarkerFile), "{}");

        var archive = Archive(GoodEntries);
        var model = Model(archive, sha256: new string('0', 64));

        var result = await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);

        Assert.Equal(VoiceDownloadOutcome.WrongFile, result.Outcome);
        Assert.True(Directory.Exists(older));
    }

    [Fact]
    public async Task AnOlderVoiceIsTakenAwayEvenWhenTheNewOneWasAlreadyThere()
    {
        // The sweep has to run on the turn that finds the voice already downloaded too: a client
        // that was stopped between unpacking and tidying gets it done next time.
        var archive = Archive(GoodEntries);
        var model = Model(archive);
        var server = new FakeServer(archive);
        var download = new VoiceDownload(new HttpClient(server));

        Assert.Equal(VoiceDownloadOutcome.Done, (await download.RunAsync(model, Voices, null, Ct)).Outcome);

        var older = Path.Combine(Voices, "en_US-kristin-medium");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, VoiceModel.MarkerFile), "{}");

        Assert.Equal(VoiceDownloadOutcome.AlreadyPresent, (await download.RunAsync(model, Voices, null, Ct)).Outcome);

        Assert.False(Directory.Exists(older));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AnOlderVoiceIsWhatMakesTheCardSayItIsReplacing()
    {
        var archive = Archive(GoodEntries);
        var model = Model(archive);

        Directory.CreateDirectory(Voices);
        Assert.False(model.AnotherIsPresent(Voices));

        var older = Path.Combine(Voices, "en_US-kristin-medium");
        Directory.CreateDirectory(older);
        Assert.False(model.AnotherIsPresent(Voices));

        // A folder only counts once it has a marker in it; a half-unpacked one is not a voice.
        File.WriteAllText(Path.Combine(older, VoiceModel.MarkerFile), "{}");
        Assert.True(model.AnotherIsPresent(Voices));

        // The voice's own folder never counts as another one.
        await new VoiceDownload(new HttpClient(new FakeServer(archive))).RunAsync(model, Voices, null, Ct);
        Assert.False(model.AnotherIsPresent(Voices));
    }

    [Fact]
    public void AVoicesFolderThatIsNotThereIsNotAnotherVoice()
    {
        Assert.False(VoiceModel.Default.AnotherIsPresent(Path.Combine(_directory, "nowhere")));
    }

    private sealed class FailingServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }

    /// <summary>Reports on the calling thread, so the test can read the list once the download returns.</summary>
    private sealed class SynchronousProgress(List<double> seen) : IProgress<double>
    {
        public void Report(double value) => seen.Add(value);
    }
}
