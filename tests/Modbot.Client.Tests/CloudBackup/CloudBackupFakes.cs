using System.IO.Compression;
using System.Text.Json;
using Modbot.Client.CloudBackup;
using Modbot.Client.Ingest;
using Modbot.Client.LogReading;
using Modbot.Client.Time;

namespace Modbot.Client.Tests.CloudBackup;

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

    public int LinesSent => Sent.Sum(s => s.Body.RootElement.GetProperty("lines").GetArrayLength());
}

internal sealed class MemoryInstallStore : ICloudInstallStore
{
    public Dictionary<string, CloudInstall> Installs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CloudInstall? Find(Uri endpoint) => Installs.GetValueOrDefault(endpoint.GetLeftPart(UriPartial.Authority));

    public void Save(CloudInstall install) => Installs[install.Endpoint.GetLeftPart(UriPartial.Authority)] = install;

    public void Forget(Uri endpoint) => Installs.Remove(endpoint.GetLeftPart(UriPartial.Authority));
}

internal static class Lines
{
    public const string File = "output_log_2026-09-15_10-00-00.txt";

    public static ReadLogLine Live(long offset, string text = "2026.09.15 10:00:00 Debug      -  [IK Debug Log] fps 90") =>
        Make(offset, text, replay: false);

    public static ReadLogLine Replay(long offset, string text = "2026.09.15 09:00:00 Debug      -  old") =>
        Make(offset, text, replay: true);

    private static ReadLogLine Make(long offset, string text, bool replay)
    {
        VRChatLogLine? parsed = VRChatLogLineParser.TryParse(text, out var line) ? line : null;
        return new ReadLogLine(File, offset, text, replay, parsed, null);
    }
}
