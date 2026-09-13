using System.Net.Http.Headers;
using Amazon.S3;
using Amazon.S3.Model;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.S3;
using Modbot.Evidence.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Evidence.Tests.Storage;

/// <summary>
/// The conformance suite against a real S3 implementation, plus presigning, which is the one
/// capability the other two backends do not have.
/// </summary>
[Collection(nameof(MinioCollection))]
public class S3StoreTests : EvidenceStoreConformanceTests
{
    private readonly MinioFixture _minio;
    private readonly List<IAmazonS3> _clients = [];

    private S3EvidenceOptions _options = new();

    public S3StoreTests(MinioFixture minio)
    {
        ArgumentNullException.ThrowIfNull(minio);
        _minio = minio;
    }

    /// <summary>
    /// A clock seeded to real time.
    /// </summary>
    /// <remarks>
    /// The presigned window is enforced by the <em>store's</em> clock, and the store here is a
    /// container running on real time. A fixed fake instant would produce signatures MinIO reads
    /// as already expired — which says nothing about Modbot and everything about the fake.
    /// </remarks>
    private static FakeClock LiveClock() => new(DateTimeOffset.UtcNow);

    protected override async Task<IEvidenceStore> CreateStoreAsync(CancellationToken ct)
    {
        var options = _minio.OptionsFor($"modbot-{Guid.NewGuid():N}");
        var client = _minio.CreateClient(options);
        _clients.Add(client);

        await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);

        if (_options.Bucket.Length == 0)
            _options = options;

        return new S3EvidenceStore(client, options, LiveClock());
    }

    protected override ValueTask CleanUpAsync()
    {
        foreach (var client in _clients)
            client.Dispose();

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ItDeclaresPresigningAndServerSideCopy()
    {
        Assert.True(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedRead));
        Assert.True(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedWrite));
        Assert.True(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.ServerSideCopy));
    }

    /// <summary>
    /// The point of presigned delivery: the bytes go from the bucket to the browser, and Modbot is
    /// not in the path at all.
    /// </summary>
    [Fact]
    public async Task APresignedReadUrlActuallyServesTheObject()
    {
        var content = SampleMedia.Png(2048);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        var url = await Store.TryCreatePresignedReadAsync(
            staged.Hash, TimeSpan.FromMinutes(5), new PresignedReadOptions("image/png", "screenshot.png"), Ct);

        Assert.NotNull(url);

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, Ct);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)} for {url}");

        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// Design section 10.3: a presigned GET can pin the disposition and the type by signing them,
    /// which is what stops a browser from navigating to an attacker's file inline.
    /// </summary>
    [Fact]
    public async Task APresignedReadUrlPinsTheDispositionAndType()
    {
        var content = SampleMedia.Jpeg(1024);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        var url = await Store.TryCreatePresignedReadAsync(
            staged.Hash, TimeSpan.FromMinutes(5), new PresignedReadOptions("image/jpeg", "proof.jpg"), Ct);

        using var http = new HttpClient();
        using var response = await http.GetAsync(url!, Ct);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    /// <summary>
    /// The cheap path of design section 9.2: the browser writes straight to the bucket, and Modbot
    /// reads the staged object back to hash and validate it.
    /// </summary>
    [Fact]
    public async Task APresignedWriteUrlStagesBytesModbotCanThenReadBack()
    {
        var uploadId = EvidenceUploadId.New();
        var content = SampleMedia.Webp(3000);

        var url = await Store.TryCreatePresignedWriteAsync(uploadId, TimeSpan.FromMinutes(5), "image/webp", Ct);
        Assert.NotNull(url);

        using var http = new HttpClient();
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/webp");

        using var put = await http.PutAsync(url, body, Ct);
        put.EnsureSuccessStatusCode();

        await using var staged = await Store.OpenStagedAsync(uploadId, Ct);
        Assert.NotNull(staged);
        Assert.Equal(content, await DrainAsync(staged));
    }

    /// <summary>
    /// A presigned URL cannot be made single-use, so anyone holding one within its window can
    /// fetch the object without authenticating. The window is therefore the only control there is,
    /// and it is the store that enforces it.
    /// </summary>
    [Fact]
    public async Task APresignedUrlStopsWorkingOnceItsWindowHasPassed()
    {
        var content = SampleMedia.Gif(512);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        var url = await Store.TryCreatePresignedReadAsync(staged.Hash, TimeSpan.FromSeconds(1), null, Ct);

        using var http = new HttpClient();

        using (var immediately = await http.GetAsync(url!, Ct))
            Assert.True(immediately.IsSuccessStatusCode, $"{(int)immediately.StatusCode} while still valid.");

        // Real time, because the expiry is checked by the container's clock and not by Modbot's.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        using var afterwards = await http.GetAsync(url!, Ct);
        Assert.False(afterwards.IsSuccessStatusCode);
    }

    /// <summary>
    /// A prefix lets one bucket hold two deployments, and neither may see the other's store marker.
    /// </summary>
    [Fact]
    public async Task TwoPrefixesInOneBucketAreSeparateStores()
    {
        var mine = _options with { Prefix = "one" };
        var theirs = _options with { Prefix = "two" };

        using var first = new S3EvidenceStore(_minio.CreateClient(mine), mine, LiveClock());
        using var second = new S3EvidenceStore(_minio.CreateClient(theirs), theirs, LiveClock());

        var id = Guid.NewGuid();
        await first.WriteStoreMarkerAsync(new StoreMarker(id, DateTimeOffset.UnixEpoch, "one"), Ct);

        Assert.Equal(StoreProbeOutcome.Present, (await first.ProbeAsync(Ct)).Outcome);
        Assert.Equal(StoreProbeOutcome.Absent, (await second.ProbeAsync(Ct)).Outcome);
    }

    /// <summary>
    /// A mistyped bucket name is the S3 shape of the unmounted volume, and it must read as
    /// "not our store" rather than as an error nobody classifies.
    /// </summary>
    [Fact]
    public async Task AMissingBucketProbesAsAbsentRatherThanThrowing()
    {
        var wrong = _options with { Bucket = $"modbot-never-created-{Guid.NewGuid():N}" };
        using var store = new S3EvidenceStore(_minio.CreateClient(wrong), wrong, LiveClock());

        Assert.Equal(StoreProbeOutcome.Absent, (await store.ProbeAsync(Ct)).Outcome);
    }

    /// <summary>
    /// An endpoint that is not listening is "did not answer" — the one probe outcome that must
    /// never lock, because a thirty-second outage is not evidence of loss.
    /// </summary>
    [Fact]
    public async Task AnUnreachableEndpointProbesAsUnreachable()
    {
        // Port 1 on the loopback: nothing is listening, and nothing can be.
        var unreachable = _options with { Endpoint = "http://127.0.0.1:1" };
        using var store = new S3EvidenceStore(_minio.CreateClient(unreachable), unreachable, LiveClock());

        var probe = await store.ProbeAsync(Ct);

        Assert.Equal(StoreProbeOutcome.Unreachable, probe.Outcome);
        Assert.NotNull(probe.Failure);
    }
}
