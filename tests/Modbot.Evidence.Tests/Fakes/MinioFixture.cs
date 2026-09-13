using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage.S3;

namespace Modbot.Evidence.Tests.Fakes;

/// <summary>
/// One MinIO container for the whole assembly.
/// </summary>
/// <remarks>
/// <para>
/// A real S3 implementation rather than a substitute, for the same reason the data tests use real
/// PostgreSQL: what is being tested is SigV4 against a non-AWS endpoint, path-style addressing,
/// server-side copy and presigned URLs, and a hand-written fake would assert only that the fake
/// agrees with itself.
/// </para>
/// <para>
/// MinIO is also the closest available stand-in for the providers this actually has to work with —
/// Railway Buckets (Tigris), Wasabi and R2 — none of which can be spun up in a test. It is a
/// stand-in and not a proof: design section 20 lists the provider-specific questions that only a
/// live bucket can answer.
/// </para>
/// </remarks>
public sealed class MinioFixture : IAsyncLifetime
{
    private const string AccessKey = "modbotevidence";
    private const string SecretKey = "modbotevidence-secret";

    /// <summary>
    /// From quay.io, which is MinIO's own registry, and not from Docker Hub.
    /// </summary>
    /// <remarks>
    /// <c>docker.io/minio/minio</c> no longer resolves — Docker Hub answers 404 for the
    /// repository, and a pull fails with <em>"pull access denied … repository does not exist"</em>,
    /// which reads like a credentials problem and is not one. The release tag was always correct;
    /// only the registry was wrong. It passed locally for a while because the image was already in
    /// the daemon's cache, so the first machine to notice was CI — after five commits of a red
    /// build that nothing else was failing.
    /// </remarks>
    private const string MinioImage = "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z";

    private readonly IContainer _container = new ContainerBuilder(MinioImage)
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithCommand("server", "/data")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>
    /// Options pointing at a fresh bucket. Path-style, which is what MinIO defaults to and what
    /// Railway issues on older buckets.
    /// </summary>
    public S3EvidenceOptions OptionsFor(string bucket) => new()
    {
        Bucket = bucket,
        Endpoint = Endpoint,
        AccessKeyId = AccessKey,
        SecretAccessKey = SecretKey,
        Region = "us-east-1",
        UsePathStyle = true,
    };

    /// <summary>
    /// Built through the production configuration, so the tests exercise the same signing,
    /// addressing and checksum settings a deployment would.
    /// </summary>
    public IAmazonS3 CreateClient(S3EvidenceOptions options)
        => new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey), S3EvidenceStore.BuildConfig(options));
}

/// <summary>One container per assembly; see <see cref="PostgresCollection"/> for why it lives here.</summary>
[CollectionDefinition(nameof(MinioCollection))]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>;
