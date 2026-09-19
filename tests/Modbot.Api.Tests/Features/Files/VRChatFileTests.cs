using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Files;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Files;

namespace Modbot.Api.Tests.Features.Files;

/// <summary>
/// <c>GET /api/files/vrchat</c> (VRChat files design): the route that lets a browser show a
/// VRChat picture at all, what it refuses, and what it never asks VRChat for twice.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VRChatFileTests : IDisposable
{
    private const string Address = "https://api.vrchat.cloud/api/1/file/file_abc/1/file";

    private readonly PostgresFixture _db;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "modbot-file-endpoint-tests", Guid.NewGuid().ToString("n"));

    public VRChatFileTests(PostgresFixture db) => _db = db;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private Task<ApiTestHost> StartAsync(FakeVRChatGate gate) =>
        ApiTestHost.StartAsync(
            _db,
            gate,
            services => services.AddSingleton(new VRChatFileCacheOptions { Root = _root }));

    private static string Ask(string url) =>
        VRChatFileEndpoints.Path + "?url=" + Uri.EscapeDataString(url);

    /// <summary>Turns the operator's picture switch off.</summary>
    /// <summary>
    /// Turns the operator's picture switch on. It ships off, so every test that expects Modbot to
    /// fetch a picture has to say so first; off is a redirect to VRChat, not a fetch.
    /// </summary>
    private static async Task ProxyPicturesAsync(ApiTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.VRChatImagesProxied = true;
        await db.SaveChangesAsync(ct);
    }

    private static async Task StopProxyingPicturesAsync(ApiTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.VRChatImagesProxied = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The cap has to be in the settings row before a miss will store anything.</summary>
    private static async Task GiveCacheRoomAsync(ApiTestHost host, long bytes, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.VRChatFileCacheBytes = bytes;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The picture comes back as a picture, cached in the browser for a week, with the ETag a
    /// versioned address makes safe.
    /// </summary>
    [Fact]
    public async Task APictureIsFetchedAndServed()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        await using var host = await StartAsync(new FakeVRChatGate().SignedInAs().Serves([1, 2, 3], "image/png"));
        await GiveCacheRoomAsync(host, 1_000_000, ct);
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync(ct));
        // Structured properties, not the raw header text: HttpClient reparses the "Cache-Control"
        // header into a CacheControlHeaderValue, and its ToString() emits directives in its own
        // canonical order, not the order the server wrote them in.
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromSeconds(604800), response.Headers.CacheControl!.MaxAge);
        Assert.Equal($"\"{VRChatFileCache.KeyFor(Address)}\"", response.Headers.ETag!.ToString());

        // Somebody else's bytes on Modbot's own address: never sniffed, never an origin.
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary>
    /// <strong>No permission gates this.</strong> The files are public on VRChat's own delivery
    /// network to anyone holding the address, and the caller is holding it already -- fetching it
    /// for them adds no reach. Any signed-in person may ask.
    /// </summary>
    [Fact]
    public async Task AnySignedInPersonMayAsk_AndNobodyElseMay()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        await using var host = await StartAsync(new FakeVRChatGate().SignedInAs().Serves([1], "image/png"));
        await GiveCacheRoomAsync(host, 1_000_000, ct);
        await ProxyPicturesAsync(host, ct);

        var signedOut = await host.Client.GetAsync(Ask(Address), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var signedIn = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    /// <summary>
    /// The route must never become a fetcher for whatever address somebody types. Anything that
    /// is not VRChat's is refused before a single request goes out.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/x.png")]
    [InlineData("https://notvrchat.cloud/x.png")]
    [InlineData("http://api.vrchat.cloud/x.png")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task AnyAddressButVRChatsIsRefused(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        var gate = new FakeVRChatGate().SignedInAs().Serves([1], "image/png");
        await using var host = await StartAsync(gate);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(url), cookie), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gate.Fetched);
    }

    /// <summary>A second look at the same picture costs VRChat nothing.</summary>
    [Fact]
    public async Task ASecondAskIsServedFromDiskWithoutTouchingVRChat()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        var gate = new FakeVRChatGate().SignedInAs().Serves([7, 7, 7], "image/jpeg");
        await using var host = await StartAsync(gate);
        await GiveCacheRoomAsync(host, 1_000_000, ct);
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);

        var first = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal([7, 7, 7], await second.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal("image/jpeg", second.Content.Headers.ContentType!.MediaType);

        Assert.Single(gate.Fetched);
        Assert.Equal(Address, gate.Fetched[0].ToString());
    }

    /// <summary>With no room set aside, every ask is a miss and every miss reaches VRChat.</summary>
    [Fact]
    public async Task WithNoCacheRoomEveryAskReachesVRChat()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        var gate = new FakeVRChatGate().SignedInAs().Serves([1], "image/png");
        await using var host = await StartAsync(gate);
        await GiveCacheRoomAsync(host, 0, ct);
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);

        await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);
        await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(2, gate.Fetched.Count);
    }

    /// <summary>
    /// A deployment with no VRChat account -- one still being set up, and every demo -- answers
    /// 404. From the browser's side there is no picture there, and a broken image is a better
    /// answer than a page of red. A demo therefore shows what its cache holds and nothing else.
    /// </summary>
    [Fact]
    public async Task WithNoVRChatAccountTheAnswerIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        await using var host = await StartAsync(new FakeVRChatGate().Refuses(
            VRChatFileOutcome.NoSession, "VRChat is off in the demo."));
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A demo has no VRChat account but does have whatever was seeded into its cache, and that
    /// is served without anything being asked of VRChat.
    /// </summary>
    [Fact]
    public async Task ADemoStillServesWhatItsCacheHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        var gate = new FakeVRChatGate().Refuses(VRChatFileOutcome.NoSession, "VRChat is off in the demo.");
        await using var host = await StartAsync(gate);

        await ProxyPicturesAsync(host, ct);
        var cache = host.Services.GetRequiredService<VRChatFileCache>();
        await cache.StoreAsync(Address, new VRChatFile([4, 2], "image/png"), 1_000_000, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([4, 2], await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Empty(gate.Fetched);
    }

    [Fact]
    public async Task SomethingThatIsNotAPictureIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        await using var host = await StartAsync(new FakeVRChatGate().SignedInAs().Refuses(
            VRChatFileOutcome.NotShowable, "That is not a picture."));
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task AFileTooLargeToPassOnIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        await using var host = await StartAsync(new FakeVRChatGate().SignedInAs().Refuses(
            VRChatFileOutcome.TooBig, "Too big."));
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>A browser that already has the picture is told so rather than sent it again.</summary>
    [Fact]
    public async Task AKnownEtagIsAnsweredNotModified()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        await using var host = await StartAsync(new FakeVRChatGate().SignedInAs().Serves([1, 2], "image/png"));
        await GiveCacheRoomAsync(host, 1_000_000, ct);
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        var again = host.Authenticated(HttpMethod.Get, Ask(Address), cookie);
        again.Headers.TryAddWithoutValidation("If-None-Match", $"\"{VRChatFileCache.KeyFor(Address)}\"");

        var response = await host.Client.SendAsync(again, ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    /// <summary>
    /// With the switch off this server does not fetch VRChat pictures. It does not refuse either:
    /// the browser is sent to VRChat for the picture, which is the operator saying "not through
    /// me" rather than "no picture".
    /// </summary>
    [Fact]
    public async Task WithTheSwitchOffTheBrowserIsSentToVRChat()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        var gate = new FakeVRChatGate().Serves([1, 2, 3]);
        await using var host = await StartAsync(gate);
        await StopProxyingPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Address, response.Headers.Location?.ToString());
        Assert.Empty(gate.Fetched);
    }

    /// <summary>
    /// Off means off, not "serve the ones already held": a picture already on disk is sent to
    /// VRChat the same way, so turning the switch off is one answer for every picture rather than
    /// two depending on what this server happens to have fetched before.
    /// </summary>
    [Fact]
    public async Task WithTheSwitchOffEvenAHeldPictureIsNotServed()
    {
        var ct = TestContext.Current.CancellationToken;
        await ApiTestHost.ResetDeploymentAsync(_db, ct);

        var gate = new FakeVRChatGate().Serves([1, 2, 3]);
        await using var host = await StartAsync(gate);
        await GiveCacheRoomAsync(host, 1024 * 1024, ct);
        await ProxyPicturesAsync(host, ct);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var first = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await StopProxyingPicturesAsync(host, ct);

        var second = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Ask(Address), cookie), ct);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(Address, second.Headers.Location?.ToString());
    }
}
