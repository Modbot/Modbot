using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Cloud;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Cloud;

/// <summary>
/// The usage report and its switch: what it carries (the privacy policy lists exactly these fields),
/// that it is on when Modbot is installed, and that off means nothing is built or sent.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UsageReportSwitchTests
{
    private readonly PostgresFixture _db;

    public UsageReportSwitchTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The policy's table of what a report carries. A new field must be added here and to
    /// <c>PRIVACY_POLICY.md</c>, the docs page <c>self-hosting/index.mdx</c> and the Cloud section of
    /// the docs privacy page in the same change, or this test fails.
    /// </summary>
    [Fact]
    public void A_report_carries_exactly_the_fields_the_privacy_policy_lists()
    {
        var json = JsonSerializer.Serialize(new ServerReport());
        using var document = JsonDocument.Parse(json);

        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList();

        Assert.Equal(
            new[]
            {
                "aiModerationEnabled",
                "discordConnected",
                "groupBannerUrl",
                "groupDescription",
                "groupIconUrl",
                "groupId",
                "groupName",
                "hostPlatform",
                "publicAddress",
                "rateLimitColdStops",
                "termListsImported",
                "version",
                "wafBlocks",
            }.Order(),
            fields);
    }

    [Fact]
    public void The_switch_is_on_when_modbot_is_installed()
    {
        Assert.True(new Settings().SendUsageReport);
    }

    [Fact]
    public async Task A_new_settings_row_in_the_database_has_the_switch_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _db.NewContext();

        await db.Settings.ExecuteDeleteAsync(ct);

        Assert.True((await db.GetSettingsAsync(ct)).SendUsageReport);
    }

    [Fact]
    public void The_report_goes_first_two_minutes_after_start_and_then_every_six_hours()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), ServerReportingService.FirstDelay);
        Assert.Equal(TimeSpan.FromHours(6), ServerReportingService.WaitAfter(true));
        Assert.Equal(TimeSpan.FromHours(1), ServerReportingService.WaitAfter(false));
    }

    [Fact]
    public void With_the_switch_off_the_service_looks_again_in_minutes_not_hours()
    {
        Assert.Equal(ServerReportingService.OffDelay, ServerReportingService.WaitAfter(null));
        Assert.True(ServerReportingService.OffDelay < TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task With_the_switch_off_nothing_is_sent_and_the_last_report_stays_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, reporter, handler) = await ReporterAsync(reportOn: false, ct);
        await using var owner = db;

        var result = await reporter.ReportAsync(ct);

        Assert.Null(result);
        Assert.Empty(handler.Paths);

        var settings = await db.GetSettingsAsync(ct);
        Assert.Equal(Now.AddDays(-1), settings.CloudLastReportAt);
        Assert.True(settings.CloudLastReportOk);
    }

    [Fact]
    public async Task With_the_switch_on_the_report_goes_and_the_last_report_moves_to_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, reporter, handler) = await ReporterAsync(reportOn: true, ct);
        await using var owner = db;

        var result = await reporter.ReportAsync(ct);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal(["/api/v1/servers/report"], handler.Paths);

        var settings = await db.GetSettingsAsync(ct);
        Assert.Equal(Now, settings.CloudLastReportAt);
    }

    /// <summary>
    /// An owner who turns the report off must not be surprised by it: the one thing that still
    /// reaches Cloud is the registration a link code needs, and that carries three fields.
    /// </summary>
    [Fact]
    public async Task Registering_carries_the_address_the_version_and_the_operating_system_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _, handler) = await ReporterAsync(reportOn: false, ct, registered: false);
        await using var owner = db;

        var client = new CloudServerClient(
            new ModbotCloudAddress(new Uri("https://cloud.example"), false), new StubFactory(handler));

        await client.RegisterAsync(
            new ServerReport
            {
                PublicAddress = "https://modbot.example",
                Version = "1.2.3",
                HostPlatform = "linux-x64",
                GroupName = "Nobody should see this",
                GroupId = "grp_1",
            },
            ct);

        var body = Assert.Single(handler.Bodies);
        using var document = JsonDocument.Parse(body);

        Assert.Equal(
            new[] { "hostPlatform", "publicAddress", "version" },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    private async Task<(ModbotContext Db, ServerReporter Reporter, StubHandler Handler)> ReporterAsync(
        bool reportOn, CancellationToken ct, bool registered = true)
    {
        var db = _db.NewContext();

        await db.Settings.ExecuteDeleteAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.SendUsageReport = reportOn;
        settings.CloudLastReportAt = Now.AddDays(-1);
        settings.CloudLastReportOk = true;

        if (registered)
        {
            settings.CloudServerId = Guid.NewGuid().ToString();
            settings.CloudServerSecretEncrypted = "a-secret";
        }

        await db.SaveChangesAsync(ct);

        var handler = new StubHandler();
        var client = new CloudServerClient(
            new ModbotCloudAddress(new Uri("https://cloud.example"), false), new StubFactory(handler));

        return (db, new ServerReporter(db, client, new PassThroughProtector(), new FakeClock(Now)), handler);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers without a network, and keeps where each request went and what it carried.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);

            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>The secret is stored encrypted in real life; here it only has to round-trip.</summary>
    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
