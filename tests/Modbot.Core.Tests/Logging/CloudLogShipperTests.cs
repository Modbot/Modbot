using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging.Store;
using Modbot.Core.Security;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// Sending Modbot's own log to Modbot Cloud: what is sent, what is skipped, and what is counted.
/// </summary>
/// <remarks>
/// Against the real database, because the whole design rests on the place-marker being a row id in
/// <c>modbot_log</c> rather than a queue somebody has to keep in step with it.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class CloudLogShipperTests
{
    private readonly PostgresFixture _db;

    public CloudLogShipperTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static LogEntry Line(int minute, string message = "Stored 42 facts") => new()
    {
        At = Now.AddMinutes(-minute),
        Level = "Information",
        Message = message,
        Template = message,
        Source = "Modbot.VRChat.Sync.AuditLogProducer",
        Area = "Sync",
        Properties = """{"Count":42}""",
    };

    private async Task<(ModbotContext Db, CloudLogShipper Shipper, StubHandler Handler)> ShipperAsync(
        HttpStatusCode answer = HttpStatusCode.OK,
        bool disabled = false,
        CancellationToken ct = default)
    {
        var db = _db.NewContext();

        await db.Logs.ExecuteDeleteAsync(ct);
        await db.Settings.ExecuteDeleteAsync(ct);

        var handler = new StubHandler(answer);
        var clock = new FixedClock(Now);

        var shipper = new CloudLogShipper(
            db,
            new StubFactory(handler),
            new PassThroughProtector(),
            clock,
            new ModbotCloudAddress(new Uri("https://cloud.example"), disabled));

        return (db, shipper, handler);
    }

    [Fact]
    public async Task TurnedOffByTheEnvironmentNothingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, handler) = await ShipperAsync(disabled: true, ct: ct);
        await using var _ = db;

        db.Logs.Add(Line(1));
        await db.SaveChangesAsync(ct);

        var result = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Off, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TurnedOffInSettingsNothingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, handler) = await ShipperAsync(ct: ct);
        await using var _ = db;

        var settings = await db.GetSettingsAsync(ct);
        settings.ShipLogsToCloud = false;
        db.Logs.Add(Line(1));
        await db.SaveChangesAsync(ct);

        var result = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Off, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheFirstPassRegistersAndThenSendsGzippedJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, handler) = await ShipperAsync(ct: ct);
        await using var _ = db;

        db.Logs.AddRange(Line(3, "one"), Line(2, "two"), Line(1, "three"));
        await db.SaveChangesAsync(ct);

        var result = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Sent, result.Outcome);
        Assert.Equal(3, result.Lines);

        Assert.Equal("/api/v1/installs", handler.Requests[0].Path);
        Assert.Equal("/api/v1/logs", handler.Requests[1].Path);
        Assert.StartsWith("Bearer ", handler.Requests[1].Authorization, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        var lines = body.RootElement.GetProperty("lines");

        Assert.Equal(3, lines.GetArrayLength());
        Assert.Equal("one", lines[0].GetProperty("message").GetString());

        // The property document arrives as an object, not as a string holding one.
        Assert.Equal(JsonValueKind.Object, lines[0].GetProperty("properties").ValueKind);
        Assert.Equal(42, lines[0].GetProperty("properties").GetProperty("Count").GetInt32());

        var settings = await db.Settings.AsNoTracking().SingleAsync(ct);
        Assert.NotNull(settings.CloudLogInstallId);
        Assert.Equal(Now, settings.CloudLogSentAt);
    }

    [Fact]
    public async Task ThePlaceMarkerMovesSoTheSameLineIsNotSentTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, handler) = await ShipperAsync(ct: ct);
        await using var _ = db;

        db.Logs.AddRange(Line(2, "one"), Line(1, "two"));
        await db.SaveChangesAsync(ct);

        Assert.Equal(ShipOutcome.Sent, (await shipper.RunOnceAsync(ct)).Outcome);
        Assert.Equal(ShipOutcome.Nothing, (await shipper.RunOnceAsync(ct)).Outcome);

        db.Logs.Add(Line(0, "three"));
        await db.SaveChangesAsync(ct);

        var third = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Sent, third.Outcome);
        Assert.Equal(1, third.Lines);

        using var body = JsonDocument.Parse(handler.Requests[^1].Body!);
        Assert.Equal("three", body.RootElement.GetProperty("lines")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ARefusedBatchIsGivenUpOnRatherThanBlockingEveryLineBehindIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, _) = await ShipperAsync(HttpStatusCode.BadRequest, ct: ct);
        await using var _db = db;

        db.Logs.AddRange(Line(2, "one"), Line(1, "two"));
        await db.SaveChangesAsync(ct);

        var result = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Failed, result.Outcome);
        Assert.Equal(2, result.Dropped);

        var settings = await db.Settings.AsNoTracking().SingleAsync(ct);
        Assert.Equal(2, settings.CloudLogDropped);
        Assert.NotNull(settings.CloudLogError);

        // The marker moved past them, so the next pass is not the same batch again.
        Assert.Equal(ShipOutcome.Nothing, (await shipper.RunOnceAsync(ct)).Outcome);
    }

    [Fact]
    public async Task ACloudThatDoesNotRecogniseThisDeploymentMakesItRegisterAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, _) = await ShipperAsync(HttpStatusCode.Unauthorized, ct: ct);
        await using var _db = db;

        db.Logs.Add(Line(1));
        await db.SaveChangesAsync(ct);

        Assert.Equal(ShipOutcome.Failed, (await shipper.RunOnceAsync(ct)).Outcome);

        var settings = await db.Settings.AsNoTracking().SingleAsync(ct);
        Assert.Null(settings.CloudLogInstallId);
        Assert.Null(settings.CloudLogSecretEncrypted);
    }

    [Fact]
    public async Task ACloudThatCannotBeReachedLosesNothingAndSendsAgainLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, shipper, handler) = await ShipperAsync(HttpStatusCode.ServiceUnavailable, ct: ct);
        await using var _db = db;

        db.Logs.AddRange(Line(2, "one"), Line(1, "two"));
        await db.SaveChangesAsync(ct);

        var failed = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Failed, failed.Outcome);
        Assert.Equal(0, failed.Dropped);

        var settings = await db.Settings.AsNoTracking().SingleAsync(ct);
        Assert.Equal(0, settings.CloudLogSentThroughId);

        handler.Answer = HttpStatusCode.OK;
        var sent = await shipper.RunOnceAsync(ct);

        Assert.Equal(ShipOutcome.Sent, sent.Outcome);
        Assert.Equal(2, sent.Lines);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Seen(string Path, string? Authorization, string? Body);

    /// <summary>Answers without a network, and keeps what was sent.</summary>
    private sealed class StubHandler(HttpStatusCode answer) : HttpMessageHandler
    {
        public HttpStatusCode Answer { get; set; } = answer;

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;

            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);

                body = request.Content.Headers.ContentEncoding.Contains("gzip")
                    ? Ungzip(bytes)
                    : System.Text.Encoding.UTF8.GetString(bytes);
            }

            Requests.Add(new Seen(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                body));

            if (request.RequestUri.AbsolutePath == "/api/v1/installs")
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""
                        {"installId":"{{Guid.NewGuid()}}","secret":"a-secret","serverTime":"2026-09-16T12:00:00Z"}
                        """,
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(Answer)
            {
                Content = new StringContent("""{"stored":0}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private static string Ungzip(byte[] bytes)
        {
            using var source = new MemoryStream(bytes);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : Modbot.Core.Time.IModbotClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>The secret is stored encrypted in real life; here it only has to round-trip.</summary>
    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
