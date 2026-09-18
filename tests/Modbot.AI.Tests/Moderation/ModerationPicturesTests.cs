using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI.Moderation;
using Modbot.Moderation;

namespace Modbot.AI.Tests.Moderation;

/// <summary>
/// Which pictures a rule sends and how (AI moderation design §17): what counts as a picture, what
/// is refused before anything is fetched, and the caps.
/// </summary>
public class ModerationPicturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void OnlyTheAttachmentsDiscordCallsPicturesAreTaken()
    {
        const string attachments = """
            [
              {"name":"cat.png","type":"image/png","size":10,"url":"https://cdn.example/cat.png"},
              {"name":"clip.mp4","type":"video/mp4","size":10,"url":"https://cdn.example/clip.mp4"},
              {"name":"notes.txt","type":"text/plain","size":10,"url":"https://cdn.example/notes.txt"},
              {"name":"shot.jpg","type":"image/jpeg; charset=binary","size":10,"url":"https://cdn.example/shot.jpg"},
              {"name":"broken.png","type":"image/png"}
            ]
            """;

        var found = PictureAttachments.Of(attachments);

        Assert.Equal(2, found.Count);
        Assert.Equal("Attachment cat.png", found[0].Label);
        Assert.Equal("Attachment shot.jpg", found[1].Label);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"name":"cat.png"}""")]
    public void AnAttachmentListThatIsNotOneIsNoPictures(string json)
        => Assert.Empty(PictureAttachments.Of(json));

    [Theory]
    // Private, local and metadata addresses, by name and by literal.
    [InlineData("https://localhost/cat.png")]
    [InlineData("https://127.0.0.1/cat.png")]
    [InlineData("https://10.0.0.5/cat.png")]
    [InlineData("https://192.168.1.4/cat.png")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/cat.png")]
    // Plain http, because a picture link is text somebody else wrote.
    [InlineData("http://cdn.example/cat.png")]
    [InlineData("ftp://cdn.example/cat.png")]
    [InlineData("not a url")]
    [InlineData("")]
    public void APrivateOrLocalPictureIsRefused(string url)
        => Assert.False(ModerationPictures.Allowed(url));

    [Fact]
    public void APublicHttpsPictureIsAllowed()
        => Assert.True(ModerationPictures.Allowed("https://cdn.discordapp.com/attachments/1/2/cat.png"));

    [Fact]
    public async Task APrivateAddressIsNeverFetched_AndNeverSentAsALink()
    {
        var asked = new List<string>();
        var pictures = Pictures(request =>
        {
            asked.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } },
            };
        });

        var sources = new[]
        {
            new PictureSource("Attachment inside.png", "http://169.254.169.254/latest/meta-data"),
            new PictureSource("Attachment home.png", "https://192.168.1.4/cat.png"),
            new PictureSource("Attachment cat.png", "https://cdn.example/cat.png"),
        };

        var links = await pictures.ReadyAsync(sources, sendLinks: true, Ct);
        Assert.Equal("https://cdn.example/cat.png", Assert.Single(links).Url);
        Assert.Empty(asked);

        var bytes = await pictures.ReadyAsync(sources, sendLinks: false, Ct);
        Assert.Equal("https://cdn.example/cat.png", Assert.Single(bytes).Url);
        Assert.Equal(["https://cdn.example/cat.png"], asked);
    }

    [Fact]
    public async Task AtMostFourPicturesGo_EachWithItsOwnKey()
    {
        var pictures = Pictures(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sources = Enumerable.Range(1, 9)
            .Select(i => new PictureSource($"Attachment {i}.png", $"https://cdn.example/{i}.png"))
            .ToList();

        var ready = await pictures.ReadyAsync(sources, sendLinks: true, Ct);

        Assert.Equal(ModerationPictures.MostPictures, ready.Count);
        Assert.Equal(["p1", "p2", "p3", "p4"], ready.Select(p => p.Key));
    }

    [Fact]
    public async Task TheSamePictureTwiceIsSentOnce()
    {
        var pictures = Pictures(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ready = await pictures.ReadyAsync(
            [
                new PictureSource("Attachment cat.png", "https://cdn.example/cat.png"),
                new PictureSource("Discord avatar", "https://cdn.example/cat.png"),
            ],
            sendLinks: true,
            Ct);

        Assert.Single(ready);
    }

    [Fact]
    public async Task SomethingThatIsNotAPicture_OrIsTooBig_IsSkipped()
    {
        var pictures = Pictures(request => request.RequestUri!.AbsolutePath switch
        {
            "/html.png" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>") { Headers = { ContentType = new("text/html") } },
            },
            "/huge.png" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[ModerationPictures.MostBytes + 1])
                {
                    Headers = { ContentType = new("image/png") },
                },
            },
            "/gone.png" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } },
            },
        });

        var ready = await pictures.ReadyAsync(
            [
                new PictureSource("Attachment html.png", "https://cdn.example/html.png"),
                new PictureSource("Attachment huge.png", "https://cdn.example/huge.png"),
                new PictureSource("Attachment gone.png", "https://cdn.example/gone.png"),
                new PictureSource("Attachment cat.png", "https://cdn.example/cat.png"),
            ],
            sendLinks: false,
            Ct);

        var only = Assert.Single(ready);
        Assert.Equal("Attachment cat.png", only.Label);
        Assert.Equal("image/png", only.MediaType);
        Assert.Equal(3, only.Bytes!.ToArray().Length);
    }

    [Theory]
    [InlineData("openrouter", true)]
    [InlineData("openai", true)]
    [InlineData("xai", true)]
    // Anthropic's compatibility layer and a local server take the bytes, not a link.
    [InlineData("anthropic", false)]
    [InlineData("custom", false)]
    [InlineData(null, false)]
    public void AProviderEitherTakesALinkOrTheBytes(string? provider, bool links)
        => Assert.Equal(links, ModerationPictures.SendsLinks(provider));

    private static ModerationPictures Pictures(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ModerationPictures.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(reply));

        return new ModerationPictures(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(reply(request));
    }
}
