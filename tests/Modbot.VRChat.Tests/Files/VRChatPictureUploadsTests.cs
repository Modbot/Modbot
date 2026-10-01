using Modbot.VRChat.Files;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Client;
using VRChat.API.Model;
using VRChatFileModel = VRChat.API.Model.File;

namespace Modbot.VRChat.Tests.Files;

/// <summary>
/// Uploading a calendar event's VRChat picture (calendar design §2, added 2026-10-01): one request on
/// files.upload per picture, never sent again after a 429, and nothing sent for a file that is too big
/// or is not a PNG or JPEG.
/// </summary>
public class VRChatPictureUploadsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Png(int length = 64)
    {
        var bytes = new byte[length];
        PngStart.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    // ── The budget ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Taken from the codebase on 2026-10-01, not measured: calendar.write's one a minute, on a lane
    /// of its own, through the global backstop (foundation §4.3.4).
    /// </summary>
    [Fact]
    public void UploadsAreOneAMinute_OnTheirOwnLane_ThroughTheGlobalBackstop()
    {
        var upload = VRChatRateLimits.Defaults[VRChatEndpointClass.FilesUpload];
        var write = VRChatRateLimits.Defaults[VRChatEndpointClass.CalendarWrite];

        Assert.Equal(1.0 / 60, upload.HardMaxPerSecond, 9);
        Assert.Equal(write.HardMaxPerSecond, upload.HardMaxPerSecond, 9);
        Assert.Equal(VRChatEndpointClass.Global, upload.Backstop);
        Assert.Equal(1, upload.BurstTokens);
        Assert.NotEqual(write.Lane, upload.Lane);
        Assert.NotEqual(VRChatRateLimits.GroupLane, upload.Lane);
        Assert.DoesNotContain(VRChatEndpointClass.FilesUpload, VRChatRateLimits.Scheduled);
    }

    [Fact]
    public async Task UploadsGoOutAtMostOnceAMinute()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 3; i++)
            await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.FilesUpload), ct: Ct);

        Assert.True(harness.Clock.UtcNow - start >= TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
    }

    /// <summary>A 429 on an upload stops uploads, and the calendar writes still go out.</summary>
    [Fact]
    public async Task A429OnAnUploadStopsUploadsAndNothingElse()
    {
        var harness = new LimiterHarness();
        var upload = new VRChatEndpoint(VRChatEndpointClass.FilesUpload);

        await harness.CallAsync(upload, status: 429, ct: Ct);

        var refused = await harness.CallAsync(upload, ct: Ct);
        Assert.False(refused.IsAcquired);
        Assert.Equal(RateLimitDenialReason.ColdStop, refused.Denial?.Reason);

        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.CalendarWrite, "grp_test"), ct: Ct)).IsAcquired);
    }

    // ── The call ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APictureIsOneCall_OnFilesUpload_AsSomebodyIsWaiting()
    {
        var gate = new CountingGate();

        await new VRChatPictureUploads(gate).UploadAsync(Png(), Ct);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.FilesUpload, call.Endpoint.Class);
        Assert.Equal("UploadImage", call.Endpoint.Operation);
        Assert.Null(call.Endpoint.ResourceId);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
    }

    /// <summary>Foundation §4.3.1: the 429 comes back to the person; the upload is not sent again.</summary>
    [Fact]
    public async Task A429IsReturned_NotSentAgain()
    {
        var gate = new CountingGate
        {
            Answer = VRChatResult<VRChatFileModel>.Failure(429, "Too many requests", kind: VRChatFailureKind.RateLimited),
        };

        var answer = await new VRChatPictureUploads(gate).UploadAsync(Jpeg(), Ct);

        Assert.True(answer.IsRateLimited);
        Assert.Single(gate.Calls);
    }

    [Fact]
    public async Task NothingIsSentForAPictureTooBig_NotAPicture_OrEmpty()
    {
        var gate = new CountingGate();
        var uploads = new VRChatPictureUploads(gate);

        await Assert.ThrowsAsync<ArgumentException>(() => uploads.UploadAsync(Png((int)VRChatPictureUploads.MaxBytes + 1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.UploadAsync("GIF89a"u8.ToArray(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.UploadAsync("<svg></svg>"u8.ToArray(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.UploadAsync([], Ct));

        Assert.Empty(gate.Calls);
    }

    // ── The checks ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTypeIsReadFromTheBytes()
    {
        Assert.Equal(VRChatPictureUploads.Png, VRChatPictureUploads.TypeOf(Png()));
        Assert.Equal(VRChatPictureUploads.Jpeg, VRChatPictureUploads.TypeOf(Jpeg()));
        Assert.Null(VRChatPictureUploads.TypeOf("GIF89a"u8));
        Assert.Null(VRChatPictureUploads.TypeOf("RIFF\0\0\0\0WEBP"u8));
        Assert.Null(VRChatPictureUploads.TypeOf(PngStart.AsSpan(0, 4)));
        Assert.Null(VRChatPictureUploads.TypeOf([]));
    }

    [Fact]
    public void TheLimitIsTenMegabytes_AndExactlyThatMuchIsAllowed()
    {
        Assert.Equal(10L * 1024 * 1024, VRChatPictureUploads.MaxBytes);
        Assert.Null(VRChatPictureUploads.Problem(Png((int)VRChatPictureUploads.MaxBytes)));
        Assert.NotNull(VRChatPictureUploads.Problem(Png((int)VRChatPictureUploads.MaxBytes + 1)));
    }

    /// <summary>Records each call and answers from <see cref="Answer"/>; the SDK call itself is not run.</summary>
    private sealed class CountingGate : IVRChatGate
    {
        public List<(VRChatEndpoint Endpoint, VRChatCallPriority Priority)> Calls { get; } = [];

        public VRChatResult<VRChatFileModel> Answer { get; init; } = VRChatResult<VRChatFileModel>.Ok(null, 200);

        public VRChatSessionState State => VRChatSessionState.Healthy;

        public Task<VRChatResult<T>> ExecuteAsync<T>(
            VRChatEndpoint endpoint,
            Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
            VRChatCallPriority priority = VRChatCallPriority.Background,
            CancellationToken ct = default)
        {
            Calls.Add((endpoint, priority));

            return Task.FromResult((object)Answer is VRChatResult<T> typed
                ? typed
                : VRChatResult<T>.Failure(0, "not sent", kind: VRChatFailureKind.NotConfigured));
        }

        public Task<VRChatResult<CurrentUserLoginResponse>> SignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Modbot.VRChat.Session.SignInStatus> DescribeSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ResumeAfterWaitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VRChatResult<Modbot.VRChat.Proxy.VRChatProxyResponse>> ForwardAsync(
            VRChatEndpoint endpoint,
            Modbot.VRChat.Proxy.VRChatProxyRequest request,
            Modbot.VRChat.Proxy.VRChatProxyAccount account,
            VRChatCallPriority priority = VRChatCallPriority.Interactive,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VRChatFileResult> FetchFileAsync(Uri url, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
