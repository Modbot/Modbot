using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Evidence;
using Modbot.Evidence.Options;
using Modbot.Evidence.Upload;
using Modbot.Shared.Clips;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// How a companion answers a clip request over HTTP: the file or a word, once, from the computer
/// that was asked, inside 15 minutes. Every refusal is the same 404.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ClipAnswerTests(PostgresFixture db)
{
    private const long SizeCap = 4096;

    private static readonly byte[] Mp4 =
        [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class Scene : IAsyncDisposable
    {
        public required CompanionApiTestHost Host { get; init; }
        public required string Root { get; init; }
        public required string Token { get; init; }
        public required Guid DeviceId { get; init; }
        public required Guid ModeratorId { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Litter in the temp folder, not a failed test.
            }
        }
    }

    private async Task<Scene> StartAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "modbot-clip-answer-tests", Guid.NewGuid().ToString("n"));

        await using (var context = db.NewContext())
        {
            await context.ClipRequests.ExecuteDeleteAsync(ct);
            var settings = await context.GetSettingsAsync(ct);
            settings.EvidenceBackend = (short)EvidenceBackend.Filesystem;
            settings.EvidenceRoot = root;
            settings.EvidenceMaxFileBytes = SizeCap;
            await context.SaveChangesAsync(ct);
        }

        var host = await CompanionApiTestHost.StartAsync(db, services =>
        {
            services.AddSingleton(HostPlatform.SelfHosted);
            services.AddSingleton<ISecretProtector>(provider =>
            {
                using var scope = provider.CreateScope();
                return AesGcmSecretProtector.CreateAsync(scope.ServiceProvider.GetRequiredService<ModbotContext>())
                    .GetAwaiter().GetResult();
            });
            services.AddModbotEvidence();
            services.AddModbotEvidenceSettings();
            services.AddScoped<IEvidenceMetadata, DatabaseEvidenceMetadata>();
        });

        // Set up the way the settings page does, which also writes the store marker the store needs.
        await host.Services.LoadEvidenceSettingsAsync(ct);
        using (var scope = host.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<EvidenceSettingsService>()
                .SaveBackendAsync(new EvidenceBackendRequest("Filesystem", Root: root), "clip tests", null, ct);
            Assert.True(saved.Succeeded, saved.Message);
        }

        var moderator = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion, ct);
        var (token, deviceId) = await host.PairDeviceToAsync(moderator.Id, ct);

        return new Scene { Host = host, Root = root, Token = token, DeviceId = deviceId, ModeratorId = moderator.Id };
    }

    /// <summary>A kick asked of <paramref name="deviceId"/> at the host's clock, so it needs no case file.</summary>
    private async Task<Guid> AskAsync(Scene scene, Guid deviceId, CancellationToken ct)
    {
        await using var context = db.NewContext();
        var request = new ClipRequest
        {
            FactId = Random.Shared.NextInt64(1, long.MaxValue),
            Action = ClipAction.Kick,
            SubjectId = "usr_person",
            ModeratorUserId = scene.ModeratorId,
            ActionAt = scene.Host.Clock.UtcNow.AddSeconds(-30),
            WorldId = "wrld_cats",
            InstanceId = "39911",
            Status = ClipStatus.Asked,
            CreatedAt = scene.Host.Clock.UtcNow,
            DeviceId = deviceId,
            RequestedAt = scene.Host.Clock.UtcNow,
        };
        context.ClipRequests.Add(request);
        await context.SaveChangesAsync(ct);
        return request.Id;
    }

    private async Task<ClipRequest> RowAsync(Guid id, CancellationToken ct)
    {
        await using var context = db.NewContext();
        return await context.ClipRequests.AsNoTracking().SingleAsync(r => r.Id == id, ct);
    }

    private static Task<HttpResponseMessage> UploadAsync(Scene scene, Guid id, string? token, byte[] bytes, CancellationToken ct)
    {
        var message = scene.Host.WithToken(HttpMethod.Post, $"/api/v1/companion/clip-requests/{id}/clip", token);
        message.Content = new ByteArrayContent(bytes);
        return scene.Host.Client.SendAsync(message, ct);
    }

    private static Task<HttpResponseMessage> AnswerAsync(Scene scene, Guid id, string? token, string outcome, CancellationToken ct)
    {
        var message = scene.Host.WithToken(HttpMethod.Post, $"/api/v1/companion/clip-requests/{id}/answer", token);
        message.Content = JsonContent.Create(new ClipRequestAnswer(outcome));
        return scene.Host.Client.SendAsync(message, ct);
    }

    [Fact]
    public async Task AClipForAnOpenRequestIsAcceptedOnceAndMarksTheRequestAttached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);

        using var first = await UploadAsync(scene, id, scene.Token, Mp4, ct);
        using var second = await UploadAsync(scene, id, scene.Token, Mp4, ct);

        Assert.True(first.StatusCode == HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var row = await RowAsync(id, ct);
        Assert.Equal(ClipStatus.Attached, row.Status);
        Assert.False(string.IsNullOrEmpty(row.BlobHash));

        await using var context = db.NewContext();
        var blob = await context.EvidenceBlobs.AsNoTracking().SingleAsync(b => b.Hash == row.BlobHash, ct);
        Assert.NotNull(blob.ClipSavedAt);
        Assert.Equal(scene.DeviceId, blob.ClipDeviceId);
    }

    [Fact]
    public async Task AClipFromAnotherComputerIsRefusedAndTheRequestStaysOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);
        var (otherToken, _) = await scene.Host.PairDeviceToAsync(scene.ModeratorId, ct);

        using var response = await UploadAsync(scene, id, otherToken, Mp4, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ClipStatus.Asked, (await RowAsync(id, ct)).Status);
    }

    [Fact]
    public async Task AClipForARequestThatDoesNotExistIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);

        using var response = await UploadAsync(scene, Guid.NewGuid(), scene.Token, Mp4, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AClipAfterFifteenMinutesIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);

        scene.Host.Clock.Advance(ClipRequest.AnswerWindow + TimeSpan.FromSeconds(1));
        using var response = await UploadAsync(scene, id, scene.Token, Mp4, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ClipStatus.Asked, (await RowAsync(id, ct)).Status);
    }

    [Fact]
    public async Task AClipOverTheSizeLimitIsRefusedAndTheRequestStaysOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);

        var big = new byte[SizeCap + 1024];
        Mp4.CopyTo(big, 0);
        using var response = await UploadAsync(scene, id, scene.Token, big, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ClipStatus.Asked, (await RowAsync(id, ct)).Status);
    }

    [Fact]
    public async Task AClipWithoutADeviceTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);

        using var response = await UploadAsync(scene, id, null, Mp4, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(ClipRequestWire.ClipsOff, ClipStatus.ClipsOff)]
    [InlineData(ClipRequestWire.CouldNotCut, ClipStatus.CouldNotCut)]
    [InlineData(ClipRequestWire.UploadFailed, ClipStatus.UploadFailed)]
    public async Task AWordIsAcceptedOnceAndMarksTheRequest(string outcome, ClipStatus expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);

        using var first = await AnswerAsync(scene, id, scene.Token, outcome, ct);
        using var second = await AnswerAsync(scene, id, scene.Token, outcome, ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(expected, (await RowAsync(id, ct)).Status);
    }

    [Fact]
    public async Task AWordThatIsNotAnAnswerIsABadRequestAndLeavesTheRequestOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);

        using var response = await AnswerAsync(scene, id, scene.Token, "attached", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ClipStatus.Asked, (await RowAsync(id, ct)).Status);
    }

    [Fact]
    public async Task AWordFromAnotherComputerOrAfterFifteenMinutesIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await StartAsync(ct);
        var id = await AskAsync(scene, scene.DeviceId, ct);
        var (otherToken, _) = await scene.Host.PairDeviceToAsync(scene.ModeratorId, ct);

        using var wrongDevice = await AnswerAsync(scene, id, otherToken, ClipRequestWire.ClipsOff, ct);
        using var wrongId = await AnswerAsync(scene, Guid.NewGuid(), scene.Token, ClipRequestWire.ClipsOff, ct);

        scene.Host.Clock.Advance(ClipRequest.AnswerWindow + TimeSpan.FromSeconds(1));
        using var late = await AnswerAsync(scene, id, scene.Token, ClipRequestWire.ClipsOff, ct);

        Assert.Equal(HttpStatusCode.NotFound, wrongDevice.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Equal(ClipStatus.Asked, (await RowAsync(id, ct)).Status);
    }
}
