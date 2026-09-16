using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Cloud.Features.InstanceAlerts;
using Modbot.Cloud.Features.Mail;

namespace Modbot.Cloud.Tests;

/// <summary>
/// Cloud watching a Modbot deployment from outside: silence, errors, and saying it once.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class InstanceAlertTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Keeps what would have been sent, and can be made to fail — which the shared
    /// <see cref="TestMailer"/> cannot, and which is the case this file is mostly about.
    /// </summary>
    private sealed class StubMailer : ICloudMailer
    {
        public bool CanSend => true;

        /// <summary>True makes every send fail.</summary>
        public bool Failing { get; set; }

        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            if (Failing)
                return Task.FromResult(false);

            Sent.Add((to, subject, body));
            return Task.FromResult(true);
        }
    }

    private static async Task<(Guid Id, string Bearer)> WithLogsAsync(CloudTestHost host, DateTimeOffset at)
    {
        var (id, bearer) = await host.RegisterServerAsync();

        using var sent = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(at)));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        return (id, bearer);
    }

    private static async Task<InstanceAlertRun> CheckAsync(CloudTestHost host, StubMailer mailer)
    {
        using var scope = host.Services.CreateScope();

        var checker = new InstanceAlertChecker(
            scope.ServiceProvider.GetRequiredService<Modbot.Cloud.Data.CloudContext>(),
            scope.ServiceProvider.GetRequiredService<Modbot.Cloud.Engine.EngineContext>(),
            mailer,
            host.Time);

        return await checker.RunOnceAsync(Ct);
    }

    private static Task<HttpResponseMessage> SaveAsync(
        CloudTestHost host, Guid id, object body) =>
        host.SendAsync(HttpMethod.Put, $"/api/admin/installs/{id}/alerts", body, bearer: CloudTestHost.RootKey);

    [Fact]
    public async Task ADeploymentThatGoesQuietIsEmailedAboutOnce()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, _) = await WithLogsAsync(host, CloudTestHost.Start);

        using (var saved = await SaveAsync(host, id, new
        {
            on = true,
            email = "keeper@example.com",
            silentAfterMinutes = 60,
            errorsAnHour = 0,
            quietHours = 6,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var mailer = new StubMailer();

        // Still hearing from it: nothing to say.
        host.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty((await CheckAsync(host, mailer)).Problems);
        Assert.Empty(mailer.Sent);

        // Two hours of silence.
        host.Time.Advance(TimeSpan.FromHours(2));
        var first = await CheckAsync(host, mailer);

        Assert.Equal([id], first.Problems);
        Assert.Equal(1, first.Sent);
        Assert.Equal("keeper@example.com", mailer.Sent[0].To);

        // Still quiet, inside the quiet time: said once, not again.
        host.Time.Advance(TimeSpan.FromHours(1));
        Assert.Empty((await CheckAsync(host, mailer)).Problems);
        Assert.Single(mailer.Sent);

        // Past the quiet time: a problem nobody fixed is still a problem.
        host.Time.Advance(TimeSpan.FromHours(6));
        Assert.Equal([id], (await CheckAsync(host, mailer)).Problems);
        Assert.Equal(2, mailer.Sent.Count);
    }

    [Fact]
    public async Task ADeploymentThatComesBackIsSaidToBeOver()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await WithLogsAsync(host, CloudTestHost.Start);

        using (var saved = await SaveAsync(host, id, new
        {
            on = true,
            email = "keeper@example.com",
            silentAfterMinutes = 60,
            errorsAnHour = 0,
            quietHours = 6,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var mailer = new StubMailer();

        host.Time.Advance(TimeSpan.FromHours(3));
        Assert.Single((await CheckAsync(host, mailer)).Problems);

        // A batch arrives again.
        host.Time.Advance(TimeSpan.FromMinutes(1));
        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(host.Time.GetUtcNow()))))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        var back = await CheckAsync(host, mailer);

        Assert.Equal([id], back.Recoveries);
        Assert.Equal(2, mailer.Sent.Count);
        Assert.Contains("working again", mailer.Sent[1].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorsPastTheNumberAskedForAreAProblem()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        var lines = Enumerable.Range(0, 5)
            .Select(i => CloudTestHost.LogLine(CloudTestHost.Start, $"broke {i}", level: "Error"))
            .ToArray<object>();

        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(lines)))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var saved = await SaveAsync(host, id, new
        {
            on = true,
            email = "keeper@example.com",
            silentAfterMinutes = 1440,
            errorsAnHour = 3,
            quietHours = 6,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var mailer = new StubMailer();
        host.Time.Advance(TimeSpan.FromMinutes(5));

        var run = await CheckAsync(host, mailer);

        Assert.Equal([id], run.Problems);
        Assert.Contains("5 error", mailer.Sent[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmailThatCouldNotBeSentDoesNotStartTheQuietTime()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, _) = await WithLogsAsync(host, CloudTestHost.Start);

        using (var saved = await SaveAsync(host, id, new
        {
            on = true,
            email = "keeper@example.com",
            silentAfterMinutes = 60,
            errorsAnHour = 0,
            quietHours = 6,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var mailer = new StubMailer { Failing = true };

        host.Time.Advance(TimeSpan.FromHours(3));
        await CheckAsync(host, mailer);

        await using var cloud = db.NewCloudContext();
        var alert = await cloud.InstanceAlerts.SingleAsync(a => a.InstallId == id, Ct);

        Assert.True(alert.Problem);
        Assert.Null(alert.LastSentAt);
        Assert.Equal("Modbot Cloud could not send the email.", alert.LastError);

        // So the next pass tries again rather than waiting out a quiet time nobody was told about.
        mailer.Failing = false;
        host.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal([id], (await CheckAsync(host, mailer)).Problems);
        Assert.Single(mailer.Sent);
    }

    [Fact]
    public async Task TurningItOffClearsWhatItWasSaying()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, _) = await WithLogsAsync(host, CloudTestHost.Start);

        using (var saved = await SaveAsync(host, id, new
        {
            on = true,
            email = "keeper@example.com",
            silentAfterMinutes = 60,
            errorsAnHour = 0,
            quietHours = 6,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var mailer = new StubMailer();
        host.Time.Advance(TimeSpan.FromHours(3));
        await CheckAsync(host, mailer);

        using (var off = await SaveAsync(host, id, new
        {
            on = false,
            email = "keeper@example.com",
            silentAfterMinutes = 60,
            errorsAnHour = 0,
            quietHours = 6,
        }))
        {
            var view = await off.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.False(view.GetProperty("problem").GetBoolean());
        }

        // Turning it back on must not open with a recovery about a problem nobody was told about.
        Assert.Empty((await CheckAsync(host, mailer)).Recoveries);
    }

    [Fact]
    public async Task SettingUpAlertsNeedsTheAdminSignIn()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        using var anonymous = await host.GetAsync($"/api/admin/installs/{id}/alerts");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var asInstall = await host.GetAsync($"/api/admin/installs/{id}/alerts", bearer);
        Assert.Equal(HttpStatusCode.Unauthorized, asInstall.StatusCode);
    }

    [Fact]
    public async Task TurningItOnWithoutAnAddressIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, _) = await host.RegisterServerAsync();

        using var response = await SaveAsync(host, id, new
        {
            on = true,
            email = "",
            silentAfterMinutes = 60,
            errorsAnHour = 0,
            quietHours = 6,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
