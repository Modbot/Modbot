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
        ("vits-piper-en_US-test/", ""),
        ("vits-piper-en_US-test/en_US-test.onnx", "not really a model"),
        ("vits-piper-en_US-test/tokens.txt", "a 1\nb 2\n"),
        ("vits-piper-en_US-test/MODEL_CARD", "# test"),
        ("vits-piper-en_US-test/espeak-ng-data/", ""),
        ("vits-piper-en_US-test/espeak-ng-data/en_dict", "words"),
        ("vits-piper-en_US-test/espeak-ng-data/lang/", ""),
        ("vits-piper-en_US-test/espeak-ng-data/lang/en", "language en"),
    ];

    private static VoiceModel Model(byte[] archive, string? sha256 = null, long? size = null) => new(
        "en_US-test",
        new Uri("https://example.test/voices/vits-piper-en_US-test.tar.bz2"),
        sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive)),
        size ?? archive.Length,
        "vits-piper-en_US-test",
        "en_US-test.onnx",
        "tokens.txt",
        "espeak-ng-data",
        22_050);

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
        Assert.Equal("a 1\nb 2\n", File.ReadAllText(model.TokensPath(Voices)));
        Assert.Equal("language en", File.ReadAllText(Path.Combine(model.DataPath(Voices), "lang", "en")));

        // The archive's own top folder is not kept, the temporary files are gone, and the marker
        // says what this is and where it came from.
        Assert.False(Directory.Exists(Path.Combine(model.Folder(Voices), "vits-piper-en_US-test")));
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
        var archive = Archive(("vits-piper-en_US-test/", ""), ("vits-piper-en_US-test/README", "nothing here"));
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
            ("vits-piper-en_US-test/", ""),
            ("vits-piper-en_US-test/en_US-test.onnx", "model"),
            ("vits-piper-en_US-test/../../escaped.txt", "should never be written"));
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
        Assert.Equal("/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-kristin-medium.tar.bz2", voice.Url.AbsolutePath);
        Assert.Matches("^[0-9a-f]{64}$", voice.Sha256);
        Assert.Equal(67_259_230, voice.Size);
        Assert.Equal(22_050, voice.SampleRate);
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
