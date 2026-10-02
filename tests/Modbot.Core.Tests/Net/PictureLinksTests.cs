using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Modbot.Core.Net;

namespace Modbot.Core.Tests.Net;

/// <summary>
/// Fetching the picture behind a link somebody typed (calendar design §15.2): https on 443 only,
/// never a private address on any hop, redirects checked one by one, a page read only for its own
/// picture, size limits, and the answer a picture by its bytes. Nothing here reaches the network:
/// the handler answers from a table, and every refusal is checked to have sent nothing past it.
/// </summary>
public class PictureLinksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

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

        public Table Picture(string url, byte[] bytes, string type = "image/png") =>
            Answer(url, () => Bytes(bytes, type));

        public Table Page(string url, string html) =>
            Answer(url, () => Bytes(Encoding.UTF8.GetBytes(html), "text/html"));

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

    private static HttpResponseMessage Bytes(byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // ── Pictures ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APictureLinkGivesThePicture()
    {
        var table = new Table().Picture("https://pictures.example/movie.png", Png);

        var result = await new PictureLinks(table).FetchAsync("https://pictures.example/movie.png", Ct);

        Assert.Null(result.Problem);
        Assert.Equal("image/png", result.Picture!.ContentType);
        Assert.Equal(Png, result.Picture.Bytes);
    }

    /// <summary>A signed CDN link that calls its picture something else still gives the picture, typed by its bytes.</summary>
    [Fact]
    public async Task ThePictureIsTypedByItsBytes_NotByWhatTheHostSaid()
    {
        const string Signed = "https://cdn.example/attachments/1/2/movie?ex=6700&is=6600&hm=abc";
        var table = new Table().Picture(Signed, Png, "application/octet-stream");

        var result = await new PictureLinks(table).FetchAsync(Signed, Ct);

        Assert.Equal("image/png", result.Picture!.ContentType);
    }

    [Fact]
    public async Task AnSvgIsNotAPicture_WhateverItIsCalled()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var table = new Table().Picture("https://pictures.example/a.svg", svg, "image/svg+xml");

        var result = await new PictureLinks(table).FetchAsync("https://pictures.example/a.svg", Ct);

        Assert.Null(result.Picture);
        Assert.Equal(PictureLinks.NotAPicture, result.Problem);
    }

    // ── Refused before anything is sent ─────────────────────────────────────────────────

    [Theory]
    [InlineData("http://pictures.example/movie.png", PictureLinks.NotHttps)]
    [InlineData("ftp://pictures.example/movie.png", PictureLinks.NotHttps)]
    [InlineData("https://pictures.example:8443/movie.png", PictureLinks.NotHttps)]
    [InlineData("https://user:secret@pictures.example/movie.png", PictureLinks.NotHttps)]
    [InlineData("not a link", PictureLinks.NotHttps)]
    [InlineData("", PictureLinks.NotHttps)]
    [InlineData("https://localhost/movie.png", PictureLinks.Private)]
    [InlineData("https://modbot.localhost/movie.png", PictureLinks.Private)]
    [InlineData("https://127.0.0.1/movie.png", PictureLinks.Private)]
    [InlineData("https://10.0.0.5/movie.png", PictureLinks.Private)]
    [InlineData("https://192.168.1.1/movie.png", PictureLinks.Private)]
    [InlineData("https://169.254.169.254/latest/meta-data/", PictureLinks.Private)]
    [InlineData("https://[::1]/movie.png", PictureLinks.Private)]
    [InlineData("https://[fd00::1]/movie.png", PictureLinks.Private)]
    [InlineData("https://[::ffff:127.0.0.1]/movie.png", PictureLinks.Private)]
    public async Task ALinkThatMayNotBeFetchedIsRefusedBeforeAnythingIsSent(string link, string problem)
    {
        var table = new Table();

        var result = await new PictureLinks(table).FetchAsync(link, Ct);

        Assert.Null(result.Picture);
        Assert.Equal(problem, result.Problem);
        Assert.Empty(table.Asked);
    }

    [Fact]
    public async Task ALinkLongerThanASaveTakesIsRefused()
    {
        var table = new Table();
        var link = "https://pictures.example/" + new string('a', PictureLinks.MaxLinkLength);

        var result = await new PictureLinks(table).FetchAsync(link, Ct);

        Assert.Equal(PictureLinks.NotHttps, result.Problem);
        Assert.Empty(table.Asked);
    }

    // ── Redirects ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARedirectIsFollowed_RelativeOrNot()
    {
        var table = new Table()
            .Redirect("https://short.example/x", "https://pictures.example/a")
            .Redirect("https://pictures.example/a", "/b.png")
            .Picture("https://pictures.example/b.png", Png);

        var result = await new PictureLinks(table).FetchAsync("https://short.example/x", Ct);

        Assert.Equal("image/png", result.Picture!.ContentType);
        Assert.Equal(
            ["https://short.example/x", "https://pictures.example/a", "https://pictures.example/b.png"],
            table.Asked);
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/", PictureLinks.Private)]
    [InlineData("https://127.0.0.1/admin", PictureLinks.Private)]
    [InlineData("https://localhost/admin", PictureLinks.Private)]
    [InlineData("https://[::1]/admin", PictureLinks.Private)]
    [InlineData("http://pictures.example/b.png", PictureLinks.NotHttps)]
    [InlineData("https://pictures.example:8080/b.png", PictureLinks.NotHttps)]
    [InlineData("file:///etc/passwd", PictureLinks.NotHttps)]
    public async Task ARedirectSomewhereNotAllowedIsNeverFollowed(string to, string problem)
    {
        var table = new Table().Redirect("https://short.example/x", to);

        var result = await new PictureLinks(table).FetchAsync("https://short.example/x", Ct);

        Assert.Null(result.Picture);
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
            .Picture("https://e.example/", Png);

        var result = await new PictureLinks(table).FetchAsync("https://a.example/", Ct);

        Assert.Equal(PictureLinks.TooManyRedirects, result.Problem);
        Assert.Equal(PictureLinks.MaxRedirects + 1, table.Asked.Count);
    }

    // ── Pages ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APageGivesThePictureItNamesForPreviews()
    {
        var table = new Table()
            .Page("https://gallery.example/p/1", """
                <!doctype html><html><head>
                <meta property="og:title" content="Movie night">
                <meta content="/media/1.png?size=large&amp;v=2" property="og:image">
                </head><body></body></html>
                """)
            .Picture("https://gallery.example/media/1.png?size=large&v=2", Png);

        var result = await new PictureLinks(table).FetchAsync("https://gallery.example/p/1", Ct);

        Assert.Equal("image/png", result.Picture!.ContentType);
        Assert.Equal("https://gallery.example/media/1.png?size=large&v=2", table.Asked[^1]);
    }

    [Fact]
    public async Task APagesPictureOnAPrivateAddressIsRefused()
    {
        var table = new Table().Page(
            "https://gallery.example/p/1",
            "<html><head><meta property=\"og:image\" content=\"https://169.254.169.254/latest/meta-data/\"></head></html>");

        var result = await new PictureLinks(table).FetchAsync("https://gallery.example/p/1", Ct);

        Assert.Equal(PictureLinks.Private, result.Problem);
        Assert.Equal(["https://gallery.example/p/1"], table.Asked);
    }

    [Fact]
    public async Task APageWhosePictureIsAnotherPageGivesNothing()
    {
        var table = new Table()
            .Page("https://gallery.example/p/1", "<html><meta property=\"og:image\" content=\"https://gallery.example/p/2\"></html>")
            .Page("https://gallery.example/p/2", "<html><meta property=\"og:image\" content=\"https://gallery.example/1.png\"></html>")
            .Picture("https://gallery.example/1.png", Png);

        var result = await new PictureLinks(table).FetchAsync("https://gallery.example/p/1", Ct);

        Assert.Equal(PictureLinks.NotAPicture, result.Problem);
        Assert.DoesNotContain("https://gallery.example/1.png", table.Asked);
    }

    [Fact]
    public async Task APageWithNoPictureGivesNothing()
    {
        var table = new Table().Page("https://gallery.example/p/1", "<html><head><title>Hi</title></head></html>");

        var result = await new PictureLinks(table).FetchAsync("https://gallery.example/p/1", Ct);

        Assert.Equal(PictureLinks.NotAPicture, result.Problem);
    }

    [Fact]
    public async Task SomethingThatIsNeitherAPictureNorAPageGivesNothing()
    {
        var table = new Table().Answer(
            "https://api.example/x", () => Bytes(Encoding.UTF8.GetBytes("{\"secret\":1}"), "application/json"));

        var result = await new PictureLinks(table).FetchAsync("https://api.example/x", Ct);

        Assert.Null(result.Picture);
        Assert.Equal(PictureLinks.NotAPicture, result.Problem);
    }

    [Theory]
    [InlineData("<meta property='og:image' content='https://a.example/1.png'>", "https://a.example/1.png")]
    [InlineData("<meta name=\"twitter:image\" content=\"https://a.example/t.png\">", "https://a.example/t.png")]
    [InlineData(
        "<meta property=\"og:image\" content=\"http://a.example/1.png\"><meta property=\"og:image:secure_url\" content=\"https://a.example/1.png\">",
        "https://a.example/1.png")]
    [InlineData(
        "<meta name=\"twitter:image\" content=\"https://a.example/t.png\"><meta property=\"og:image\" content=\"https://a.example/o.png\">",
        "https://a.example/o.png")]
    [InlineData("<META PROPERTY=og:image CONTENT=https://a.example/bare.png>", "https://a.example/bare.png")]
    [InlineData("<meta property=\"og:image\" content=\"\">", null)]
    [InlineData("<p>og:image</p>", null)]
    public void APagesPictureIsFoundInItsPreviewTags(string html, string? expected)
    {
        Assert.Equal(expected, PictureLinks.PagePicture(html));
    }

    // ── Sizes and answers ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APictureThatSaysItIsTooBigIsNotRead()
    {
        var table = new Table().Answer("https://pictures.example/big.png", () =>
        {
            var response = Bytes(Png, "image/png");
            response.Content.Headers.ContentLength = PictureLinks.MaxBytes + 1L;
            return response;
        });

        var result = await new PictureLinks(table).FetchAsync("https://pictures.example/big.png", Ct);

        Assert.Equal(PictureLinks.TooBig, result.Problem);
    }

    [Fact]
    public async Task APictureLargerThanTheLimitIsRefused_EvenWithoutALength()
    {
        var big = new byte[PictureLinks.MaxBytes + 10];
        Png.CopyTo(big, 0);

        var table = new Table().Answer("https://pictures.example/big.png", () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(big)) });

        var result = await new PictureLinks(table).FetchAsync("https://pictures.example/big.png", Ct);

        Assert.Equal(PictureLinks.TooBig, result.Problem);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusalGivesNothing(HttpStatusCode status)
    {
        var table = new Table().Answer("https://pictures.example/x.png", () => new HttpResponseMessage(status));

        var result = await new PictureLinks(table).FetchAsync("https://pictures.example/x.png", Ct);

        Assert.Equal(PictureLinks.Unreachable, result.Problem);
    }

    // ── The real handler ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The handler every real fetch uses connects only through the public-address check, follows
    /// nothing by itself, and uses no proxy or cookies.
    /// </summary>
    [Fact]
    public void TheRealHandlerIsGuarded()
    {
        using var handler = PictureLinks.GuardedHandler();

        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }
}
