using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Files;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Files;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The pictures of the people on the overlay's roster: the roster names a path on this server, the
/// device is sent the picture stored for that person through the server's own cache, and the roster
/// names none while the operator has VRChat pictures off.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class OverlayPictureTests : IDisposable
{
    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private const string StoredAddress = "https://api.vrchat.cloud/api/1/file/file_abc/1/file";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "modbot-companion-picture-tests", Guid.NewGuid().ToString("n"));

    public OverlayPictureTests(PostgresFixture db) => _db = db;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private async Task<(CompanionApiTestHost Host, string Token, (string Token, Guid DeviceId, string VRChatUserId) Moderator)> ReadyAsync(
        FakeVRChatGate gate, bool proxyPictures, CancellationToken ct)
    {
        await CompanionApiTestHost.ResetAsync(_db, ct);

        var host = await CompanionApiTestHost.StartAsync(_db, services =>
        {
            services.AddSingleton<IVRChatGate>(gate);
            services.AddSingleton(sp => new VRChatFileCache(
                new VRChatFileCacheOptions { Root = _root }, sp.GetRequiredService<IModbotClock>()));
        });

        await host.ConfigureGroupAsync(_db, Group, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(ct);
            settings.VRChatImagesProxied = proxyPictures;
            settings.VRChatFileCacheBytes = 1_000_000;

            // Profile rows outlive a test, so the same three are put back as they should be.
            await db.VRChatUsers
                .Where(u => u.UserId == "usr_pic" || u.UserId == "usr_plain" || u.UserId == "usr_elsewhere")
                .ExecuteDeleteAsync(ct);

            db.VRChatUsers.AddRange(
                User("usr_pic", iconUrl: StoredAddress),
                User("usr_plain"),
                User("usr_elsewhere", iconUrl: "https://example.com/not-vrchat.png"));

            await db.SaveChangesAsync(ct);
        }

        var moderator = await host.PairModeratorAsync(ct);

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteManyAsync(
            [
                Joined(moderator.VRChatUserId, "Mod", Noon.AddMinutes(-20), moderator.DeviceId),
                Joined("usr_pic", "Pic", Noon.AddMinutes(-10), moderator.DeviceId),
                Joined("usr_plain", "Plain", Noon.AddMinutes(-9), moderator.DeviceId),
                Joined("usr_elsewhere", "Elsewhere", Noon.AddMinutes(-8), moderator.DeviceId),
            ]);
        }

        return (host, await host.PairDeviceAsync(ct), moderator);
    }

    private static VRChatUser User(string id, string? iconUrl = null) => new()
    {
        UserId = id,
        DisplayName = id,
        IconUrl = iconUrl,
        FirstSeenAt = Noon.AddDays(-1),
        LastSeenAt = Noon,
        LastRefreshedAt = Noon.AddDays(-1),
    };

    private static FactRecord Joined(string subject, string name, DateTimeOffset at, Guid device)
        => new()
        {
            Type = FactType.InstanceJoined,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = "wrld_4b34",
            InstanceId = Instance,
            Source = FactSource.Modbot,
            Data = new System.Text.Json.Nodes.JsonObject
            {
                ["displayName"] = name,
                ["deviceId"] = device.ToString(),
            },
        };

    private static async Task<InstanceContextDto> RosterAsync(CompanionApiTestHost host, string token, CancellationToken ct)
    {
        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v1/companion/context?instanceId={Instance}", token), ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<InstanceContextDto>(ct))!;
    }

    private static Task<HttpResponseMessage> PictureAsync(CompanionApiTestHost host, string? token, string subject, CancellationToken ct)
        => host.Client.SendAsync(host.WithToken(HttpMethod.Get, $"/api/v1/companion/picture/{subject}", token), ct);

    [Fact]
    public async Task TheRosterNamesAPathOnThisServerForEachPersonWhoseStoredPictureIsVRChats()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token, _) = await ReadyAsync(new FakeVRChatGate().SignedInAs(), proxyPictures: true, ct);
        await using var _ = host;

        var roster = await RosterAsync(host, token, ct);

        var picture = Assert.Single(roster.Members, m => m.SubjectId == "usr_pic").PictureUrl;
        Assert.StartsWith("/api/v1/companion/picture/usr_pic?v=", picture, StringComparison.Ordinal);
        Assert.DoesNotContain("vrchat.cloud", picture, StringComparison.Ordinal);

        // Nobody else has a stored picture that VRChat serves.
        Assert.Null(Assert.Single(roster.Members, m => m.SubjectId == "usr_plain").PictureUrl);
        Assert.Null(Assert.Single(roster.Members, m => m.SubjectId == "usr_elsewhere").PictureUrl);
    }

    [Fact]
    public async Task TheRosterNamesNoPictureWhileTheOperatorHasVRChatPicturesOff()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token, _) = await ReadyAsync(new FakeVRChatGate().SignedInAs(), proxyPictures: false, ct);
        await using var _ = host;

        var roster = await RosterAsync(host, token, ct);

        Assert.All(roster.Members, member => Assert.Null(member.PictureUrl));
    }

    [Fact]
    public async Task ADeviceIsSentThePictureStoredForAPersonAndTheSecondAskIsAnsweredFromTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().SignedInAs().Serves([1, 2, 3], "image/png");
        var (host, token, _) = await ReadyAsync(gate, proxyPictures: true, ct);
        await using var _ = host;

        var first = await PictureAsync(host, token, "usr_pic", ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        var bytes = await first.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal([new Uri(StoredAddress)], gate.Fetched);

        var second = await PictureAsync(host, token, "usr_pic", ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(gate.Fetched);
    }

    [Fact]
    public async Task APictureIsNotSentWithoutADeviceToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().SignedInAs().Serves([1, 2, 3], "image/png");
        var (host, _, _) = await ReadyAsync(gate, proxyPictures: true, ct);
        await using var _ = host;

        Assert.Equal(HttpStatusCode.Unauthorized, (await PictureAsync(host, null, "usr_pic", ct)).StatusCode);
        Assert.Empty(gate.Fetched);
    }

    [Fact]
    public async Task ADeviceNamesAPersonAndNeverAnAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().SignedInAs().Serves([1, 2, 3], "image/png");
        var (host, token, _) = await ReadyAsync(gate, proxyPictures: true, ct);
        await using var _ = host;

        // Nothing stored, a stored address that is not VRChat's, and somebody nobody has heard of: each is
        // a 404 and none of them reaches VRChat.
        foreach (var subject in (string[])["usr_plain", "usr_elsewhere", "usr_nobody"])
            Assert.Equal(HttpStatusCode.NotFound, (await PictureAsync(host, token, subject, ct)).StatusCode);

        Assert.Empty(gate.Fetched);
    }

    [Fact]
    public async Task WhileTheOperatorHasVRChatPicturesOffADeviceIsNotSentOneAndVRChatIsNotAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().SignedInAs().Serves([1, 2, 3], "image/png");
        var (host, token, _) = await ReadyAsync(gate, proxyPictures: false, ct);
        await using var _ = host;

        var response = await PictureAsync(host, token, "usr_pic", ct);

        // Not a redirect to VRChat, which the device could not follow.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(gate.Fetched);
    }
}
