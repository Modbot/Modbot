using System.IO.Compression;
using System.Text.Json;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Time;

namespace Modbot.Companion.Tests.CloudBackup;

/// <summary>A Cloud that answers what the test says, and remembers what it was sent.</summary>
internal sealed class FakeCloudClient : ICloudLogClient
{
    public List<Uri> Registered { get; } = [];

    public List<(CloudInstall Install, JsonDocument Body)> Sent { get; } = [];

    public Queue<IngestResult> Answers { get; } = new();

    public CloudRegistration Registration { get; set; } = new(IngestOutcome.Accepted, Guid.Parse("11111111-2222-3333-4444-555555555555"), "the-secret");

    /// <summary>When set, a send waits on this until it completes or the send is cancelled.</summary>
    public TaskCompletionSource? Hang { get; set; }

    public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CloudRegistration> RegisterAsync(Uri endpoint, string clientVersion, CancellationToken cancellationToken)
    {
        Registered.Add(endpoint);
        return Task.FromResult(Registration);
    }

    public async Task<IngestResult> SendAsync(CloudInstall install, byte[] body, CancellationToken cancellationToken)
    {
        SendStarted.TrySetResult();

        if (Hang is { } hang)
            await hang.Task.WaitAsync(cancellationToken);

        using var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        Sent.Add((install, JsonDocument.Parse(gzip)));

        return Answers.TryDequeue(out var answer) ? answer : new IngestResult(IngestOutcome.Accepted);
    }

    public Task<ClockSample?> MeasureAsync(Uri endpoint, CancellationToken cancellationToken) =>
        Task.FromResult<ClockSample?>(null);

    public int EventsSent => Sent.Sum(s => s.Body.RootElement.GetProperty("events").GetArrayLength());
}

internal sealed class MemoryInstallStore : ICloudInstallStore
{
    public Dictionary<string, CloudInstall> Installs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CloudInstall? Find(Uri endpoint) => Installs.GetValueOrDefault(endpoint.GetLeftPart(UriPartial.Authority));

    public void Save(CloudInstall install) => Installs[install.Endpoint.GetLeftPart(UriPartial.Authority)] = install;

    public void Forget(Uri endpoint) => Installs.Remove(endpoint.GetLeftPart(UriPartial.Authority));
}

internal sealed class CountingIds : IClientEventIdSource
{
    private int _next;

    public string Next() => $"event-{++_next}";
}

internal static class Observations
{
    public const string GroupLocation = "wrld_1:39911~group(grp_cats)~groupAccessType(members)~region(eu)";

    public const string PrivateLocation = "wrld_2:77777~private(usr_owner)~nonce(secret-nonce-value)~region(eu)";

    public static ObservedPresence Joined(string subject = "usr_1", string location = GroupLocation, int second = 0)
    {
        Assert.True(InstanceLocation.TryParse(location, out var instance));
        return new ObservedPresence(PresenceKind.Joined, new DateTime(2026, 9, 15, 10, 0, second), subject, "Rin", instance);
    }
}
