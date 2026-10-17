using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Modbot.Core.Files;
using Modbot.Core.Net;

namespace Modbot.Core.Tests.Net;

/// <summary>
/// Fetching the video behind a link somebody typed: the picture links' guard (https on 443, never a private
/// address on any hop, redirects checked one by one), a size cap read no further than it needs, and the answer
/// a video by its bytes. Nothing here reaches the network: the handler answers from a table.
/// </summary>
public class VideoLinksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // An MP4: a box size, "ftyp", the brand "isom", then filler.
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 1, 2, 3, 4];

    /// <summary>Answers from a table of links and keeps every link it was asked for.</summary>
    private sealed class Table : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public Table Answer(string url, Func<HttpResponseMessage> answer)
        {
            _answers[url] = answer;
            return this;
        }

        public Table Video(string url, byte[] bytes, string type = "video/mp4") =>
            Answer(url, () => Bytes(bytes, type));

        public Table Redirect(string url, string to) => Answer(url, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
            return response;
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);

            return Task.FromResult(_answers.TryGetValue(url, out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>A body that never says how long it is and gives <c>Length</c> bytes, so only the reader's own limit can stop it.</summary>
    private sealed class EndlessContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[81920];
            chunk[4] = 0x66;
            chunk[5] = 0x74;
            chunk[6] = 0x79;
            chunk[7] = 0x70;

            for (var sent = 0L; sent < length; sent += chunk.Length)
                await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, length - sent))).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static HttpResponseMessage Bytes(byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // ── Videos ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AVideoLinkGivesTheVideoTypedByItsBytes()
    {
        var table = new Table().Video("https://videos.example/trailer", Mp4, "application/octet-stream");

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/trailer", Ct);

        Assert.Null(result.Problem);
        Assert.Equal("video/mp4", result.Type);
        Assert.Equal(Mp4, result.Bytes);
    }

    // ── Refused before anything is sent ─────────────────────────────────────────────────

    [Theory]
    [InlineData("http://videos.example/a.mp4", VideoLinks.NotHttps)]
    [InlineData("https://videos.example:8443/a.mp4", VideoLinks.NotHttps)]
    [InlineData("https://user:secret@videos.example/a.mp4", VideoLinks.NotHttps)]
    [InlineData("not a link", VideoLinks.NotHttps)]
    [InlineData("", VideoLinks.NotHttps)]
    [InlineData(null, VideoLinks.NotHttps)]
    [InlineData("https://localhost/a.mp4", VideoLinks.Private)]
    [InlineData("https://127.0.0.1/a.mp4", VideoLinks.Private)]
    [InlineData("https://10.0.0.5/a.mp4", VideoLinks.Private)]
    [InlineData("https://192.168.1.1/a.mp4", VideoLinks.Private)]
    [InlineData("https://169.254.169.254/latest/meta-data/", VideoLinks.Private)]
    [InlineData("https://[::1]/a.mp4", VideoLinks.Private)]
    public async Task ALinkThatMayNotBeFetchedIsRefusedBeforeAnythingIsSent(string? link, string problem)
    {
        var table = new Table();

        var result = await new VideoLinks(table).FetchAsync(link, Ct);

        Assert.Null(result.Bytes);
        Assert.Equal(problem, result.Problem);
        Assert.Equal(problem, VideoLinks.Problem(link));
        Assert.Empty(table.Asked);
    }

    [Fact]
    public void AnAllowedLinkHasNoProblem()
    {
        Assert.Null(VideoLinks.Problem("https://videos.example/a.mp4"));
    }

    // ── Redirects ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARedirectIsFollowed_RelativeOrNot()
    {
        var table = new Table()
            .Redirect("https://short.example/x", "https://videos.example/a")
            .Redirect("https://videos.example/a", "/b.mp4")
            .Video("https://videos.example/b.mp4", Mp4);

        var result = await new VideoLinks(table).FetchAsync("https://short.example/x", Ct);

        Assert.Equal("video/mp4", result.Type);
        Assert.Equal(
            ["https://short.example/x", "https://videos.example/a", "https://videos.example/b.mp4"],
            table.Asked);
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/", VideoLinks.Private)]
    [InlineData("https://127.0.0.1/admin", VideoLinks.Private)]
    [InlineData("https://localhost/admin", VideoLinks.Private)]
    [InlineData("http://videos.example/b.mp4", VideoLinks.NotHttps)]
    [InlineData("https://videos.example:8080/b.mp4", VideoLinks.NotHttps)]
    public async Task ARedirectSomewhereNotAllowedIsNeverFollowed(string to, string problem)
    {
        var table = new Table().Redirect("https://short.example/x", to);

        var result = await new VideoLinks(table).FetchAsync("https://short.example/x", Ct);

        Assert.Null(result.Bytes);
        Assert.Equal(problem, result.Problem);
        Assert.Equal(["https://short.example/x"], table.Asked);
    }

    [Fact]
    public async Task AtMostThreeRedirectsAreFollowed()
    {
        var table = new Table()
            .Redirect("https://a.example/", "https://b.example/")
            .Redirect("https://b.example/", "https://c.example/")
            .Redirect("https://c.example/", "https://d.example/")
            .Redirect("https://d.example/", "https://e.example/")
            .Video("https://e.example/", Mp4);

        var result = await new VideoLinks(table).FetchAsync("https://a.example/", Ct);

        Assert.Equal(VideoLinks.TooManyRedirects, result.Problem);
        Assert.Equal(PictureLinks.MaxRedirects + 1, table.Asked.Count);
    }

    // ── What the link gave ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task APageIsNotAVideo_WhateverTheHostSaysItIs()
    {
        var table = new Table().Video("https://videos.example/a", Encoding.UTF8.GetBytes("<html>hello</html>"), "video/mp4");

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/a", Ct);

        Assert.Null(result.Bytes);
        Assert.Equal(VideoLinks.NotAVideo, result.Problem);
    }

    [Fact]
    public async Task APictureIsNotAVideo()
    {
        byte[] heic = [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 1, 2, 3, 4];
        var table = new Table().Video("https://videos.example/a", heic);

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/a", Ct);

        Assert.Equal(VideoLinks.NotAVideo, result.Problem);
    }

    [Fact]
    public async Task AVideoThatSaysItIsTooBigIsNotRead()
    {
        var table = new Table().Answer("https://videos.example/a", () =>
        {
            var response = Bytes(Mp4, "video/mp4");
            response.Content.Headers.ContentLength = VideoFormats.MaxBytes + 1L;
            return response;
        });

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/a", Ct);

        Assert.Null(result.Bytes);
        Assert.Equal(VideoLinks.TooBig, result.Problem);
    }

    [Fact]
    public async Task AVideoThatDoesNotSayHowBigItIsIsStoppedAtTheCap()
    {
        var table = new Table().Answer("https://videos.example/a", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new EndlessContent(VideoFormats.MaxBytes + 100_000L),
        });

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/a", Ct);

        Assert.Null(result.Bytes);
        Assert.Equal(VideoLinks.TooBig, result.Problem);
    }

    [Fact]
    public async Task AVideoAtTheCapIsTaken()
    {
        var bytes = new byte[VideoFormats.MaxBytes];
        Mp4.CopyTo(bytes, 0);
        var table = new Table().Video("https://videos.example/a", bytes);

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/a", Ct);

        Assert.Null(result.Problem);
        Assert.Equal(VideoFormats.MaxBytes, result.Bytes!.Length);
    }

    [Fact]
    public async Task AnErrorAnswerIsUnreachable()
    {
        var table = new Table();

        var result = await new VideoLinks(table).FetchAsync("https://videos.example/missing", Ct);

        Assert.Equal(VideoLinks.Unreachable, result.Problem);
    }
}
