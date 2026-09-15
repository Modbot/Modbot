using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Email;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// The daily email limit and the email queue (accounts and access design §4.4), through the real
/// sender and the real queue over real PostgreSQL, with a fake clock and a fake relay.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EmailLimitTests
{
    private readonly PostgresFixture _db;

    public EmailLimitTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, FakeMailRelay Relay)> StartAsync(int? limit = null)
    {
        await ApiTestHost.ClearEmailQueueAsync(_db, Ct);

        var relay = new FakeMailRelay();
        var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IMailRelay>(relay));

        if (limit is { } value)
            await SetLimitAsync(value);

        return (host, relay);
    }

    private async Task SetLimitAsync(int limit)
    {
        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.EmailLimitPer24Hours = limit;
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Emails that went out at this moment, written straight to the table.</summary>
    private async Task SentAtAsync(int count, DateTimeOffset at)
    {
        await using var context = _db.NewContext();

        for (var i = 0; i < count; i++)
        {
            context.EmailQueue.Add(new EmailQueueEntry
            {
                Kind = EmailKinds.Other,
                ToAddress = $"earlier{i}@example.com",
                Subject = "Earlier",
                State = EmailStates.Sent,
                QueuedAt = at,
                SentAt = at,
                FinishedAt = at,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    private static async Task<SendOutcome> SendAsync(ApiTestHost host, EmailMessage message)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(message, Ct);
    }

    private static async Task<int> RunQueueAsync(ApiTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EmailQueuePass>().RunOnceAsync(Ct);
    }

    private async Task<List<EmailQueueEntry>> RowsAsync()
    {
        await using var context = _db.NewContext();
        return await context.EmailQueue.AsNoTracking().OrderBy(e => e.QueuedAt).ToListAsync(Ct);
    }

    private static EmailMessage Other(string to) => new(to, "Hello", "An ordinary message.", EmailKind.Other);

    private static EmailMessage Account(string to, DateTimeOffset? expiresAt = null)
        => new(to, "Reset your Modbot password", "https://modbot.example.com/reset/secret-token", EmailKind.Account, expiresAt);

    // ── The limit ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OtherEmail_IsCappedAtTheLimitLessTwenty_AndTheTwentyStayForAccountEmail()
    {
        var (host, relay) = await StartAsync(limit: 25);
        await using var _ = host;

        for (var i = 0; i < 5; i++)
            Assert.True((await SendAsync(host, Other($"other{i}@example.com"))).Sent);

        var sixth = await SendAsync(host, Other("other5@example.com"));
        Assert.False(sixth.Sent);
        Assert.True(sixth.Queued);

        // However much other email was asked for, the last twenty are still there for account email.
        for (var i = 0; i < EmailLimit.KeptForAccountEmails; i++)
            Assert.True((await SendAsync(host, Account($"account{i}@example.com"))).Sent);

        var over = await SendAsync(host, Account("one-too-many@example.com"));
        Assert.True(over.Queued);

        Assert.Equal(25, relay.Sent.Count);
        Assert.DoesNotContain(relay.Tried, m => m.To is "other5@example.com" or "one-too-many@example.com");
    }

    [Fact]
    public async Task AtTheLimit_EmailIsQueued_AndSentWhenTheOldestSendTurnsADayOld()
    {
        var (host, relay) = await StartAsync(limit: EmailLimit.Minimum);
        await using var _ = host;

        var start = host.Clock.UtcNow;
        await SentAtAsync(EmailLimit.Minimum, start);

        var outcome = await SendAsync(host, Account("waiting@example.com"));
        Assert.True(outcome.Queued);
        Assert.Equal(start + TimeSpan.FromHours(24), outcome.SendsAt);

        // Queued, with its body stored encrypted rather than as it was written.
        var row = Assert.Single(await RowsAsync(), r => r.State == EmailStates.Queued);
        Assert.NotNull(row.BodyEncrypted);
        Assert.DoesNotContain("secret-token", row.BodyEncrypted, StringComparison.Ordinal);

        host.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, await RunQueueAsync(host));
        Assert.Empty(relay.Tried);

        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await RunQueueAsync(host));

        var sent = Assert.Single(relay.Sent);
        Assert.Equal("waiting@example.com", sent.To);
        Assert.Contains("secret-token", sent.Body, StringComparison.Ordinal);

        row = Assert.Single(await RowsAsync(), r => r.Id == row.Id);
        Assert.Equal(EmailStates.Sent, row.State);
        Assert.Null(row.BodyEncrypted);

        // Sent once: another pass does not send it again.
        Assert.Equal(0, await RunQueueAsync(host));
        Assert.Single(relay.Tried);
    }

    [Fact]
    public async Task AccountEmail_GoesAheadOfOtherEmailInTheQueue()
    {
        var (host, relay) = await StartAsync(limit: 30);
        await using var _ = host;

        await SentAtAsync(30, host.Clock.UtcNow);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await SendAsync(host, Other("asked-first@example.com"))).Queued);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await SendAsync(host, Account("asked-second@example.com"))).Queued);

        host.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(2, await RunQueueAsync(host));

        Assert.Equal(["asked-second@example.com", "asked-first@example.com"], relay.Tried.Select(m => m.To));
    }

    [Fact]
    public async Task NewAccountEmail_QueuesBehindWaitingAccountEmail_EvenWithRoom()
    {
        var (host, _) = await StartAsync(limit: EmailLimit.Minimum);
        await using var h = host;

        await SentAtAsync(EmailLimit.Minimum, host.Clock.UtcNow);
        Assert.True((await SendAsync(host, Account("first@example.com"))).Queued);

        // The window frees, but the queue has not run yet: a new message does not jump the line.
        host.Clock.Advance(TimeSpan.FromHours(24));
        Assert.True((await SendAsync(host, Account("second@example.com"))).Queued);
    }

    [Fact]
    public async Task AQueuedLinkThatExpires_IsMarkedExpired_AndNeverSent()
    {
        var (host, relay) = await StartAsync(limit: EmailLimit.Minimum);
        await using var _ = host;

        await SentAtAsync(EmailLimit.Minimum, host.Clock.UtcNow);

        var outcome = await SendAsync(host, Account("late@example.com", expiresAt: host.Clock.UtcNow + TimeSpan.FromHours(1)));
        Assert.True(outcome.Queued);

        host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(0, await RunQueueAsync(host));

        Assert.Empty(relay.Tried);
        var row = Assert.Single(await RowsAsync(), r => r.ToAddress == "late@example.com");
        Assert.Equal(EmailStates.Expired, row.State);
        Assert.Null(row.BodyEncrypted);
    }

    [Fact]
    public async Task ARefusedQueuedEmail_IsTriedAgainLater_NotInATightLoop_AndFailsAfterAFewTries()
    {
        var (host, relay) = await StartAsync(limit: EmailLimit.Minimum);
        await using var _ = host;

        await SentAtAsync(EmailLimit.Minimum, host.Clock.UtcNow);
        Assert.True((await SendAsync(host, Account("refused@example.com"))).Queued);

        host.Clock.Advance(TimeSpan.FromHours(24));
        relay.RefuseWith = "The mail server refused the message: mailbox unavailable";

        await RunQueueAsync(host);
        Assert.Single(relay.Tried);

        var row = Assert.Single(await RowsAsync(), r => r.ToAddress == "refused@example.com");
        Assert.Equal(EmailStates.Queued, row.State);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.NextAttemptAt);

        // Straight away again: nothing is tried before the next attempt is due.
        await RunQueueAsync(host);
        Assert.Single(relay.Tried);

        var options = host.Services.GetRequiredService<EmailQueueOptions>();
        for (var i = 1; i < options.MaxAttempts; i++)
        {
            host.Clock.Advance(TimeSpan.FromHours(7));
            await RunQueueAsync(host);
        }

        Assert.Equal(options.MaxAttempts, relay.Tried.Count);

        row = Assert.Single(await RowsAsync(), r => r.ToAddress == "refused@example.com");
        Assert.Equal(EmailStates.Failed, row.State);
        Assert.Contains("mailbox unavailable", row.LastError, StringComparison.Ordinal);
        Assert.Null(row.BodyEncrypted);

        host.Clock.Advance(TimeSpan.FromHours(7));
        await RunQueueAsync(host);
        Assert.Equal(options.MaxAttempts, relay.Tried.Count);

        // The health page warns about it.
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var health = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/health/sync", null, cookie, Ct), Ct);
        Assert.Equal(1, health.GetProperty("email").GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task ASendRefusedStraightAway_ReturnsTheRefusal_AndTakesNoPlaceUnderTheLimit()
    {
        var (host, relay) = await StartAsync();
        await using var _ = host;

        relay.RefuseWith = "The mail server refused the message: bad login";

        var outcome = await SendAsync(host, Other("test@example.com"));

        Assert.False(outcome.Sent);
        Assert.False(outcome.Queued);
        Assert.Contains("bad login", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(await RowsAsync());
    }

    // ── Forgot password ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_AnswersTheSameForARealAndAnUnknownUsername_WhenEmailIsQueued()
    {
        var (host, relay) = await StartAsync(limit: EmailLimit.Minimum);
        await using var _ = host;

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.PublicAddress = "https://modbot.example.com";
            await context.SaveChangesAsync(Ct);
        }

        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        await using (var context = _db.NewContext())
        {
            var row = await context.Users.FindAsync([user.Id], Ct);
            row!.Email = "locked-out@example.com";
            await context.SaveChangesAsync(Ct);
        }

        await SentAtAsync(EmailLimit.Minimum, host.Clock.UtcNow);

        var real = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);
        var unknown = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = $"nobody_{Guid.NewGuid():N}" }, null, Ct);

        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal(real.StatusCode, unknown.StatusCode);

        var realText = await real.Content.ReadAsStringAsync(Ct);
        Assert.Equal(realText, await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Contains(Modbot.Api.Features.Users.ForgotPasswordResponse.Delayed.Message, realText, StringComparison.Ordinal);

        // Only the real account's link was queued, with the link's own expiry.
        Assert.Empty(relay.Tried);
        var queued = Assert.Single(await RowsAsync(), r => r.State == EmailStates.Queued);
        Assert.Equal("locked-out@example.com", queued.ToAddress);
        Assert.Equal(EmailKinds.Account, queued.Kind);
        Assert.Equal(host.Clock.UtcNow + Modbot.Api.Features.Users.OneTimeLinkService.ResetLifetime, queued.ExpiresAt);

        var fact = Assert.Single(await host.FactsAsync(FactType.ResetLinkCreated, user.Id.ToString(), Ct));
        Assert.True(ApiTestHost.DataOf(fact).GetProperty("queued").GetBoolean());
    }

    // ── The settings page ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLimit_DefaultsToAHundred_AndCannotBeSetBelowTwenty()
    {
        var (host, _) = await StartAsync();
        await using var _h = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/settings/email", null, cookie, Ct), Ct);
        Assert.Equal(EmailLimit.Default, view.GetProperty("limitPer24Hours").GetInt32());

        var tooLow = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/email/limit", new { limitPer24Hours = 19 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLow.StatusCode);

        var lowest = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/email/limit", new { limitPer24Hours = 20 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, lowest.StatusCode);
        Assert.Equal(20, (await ApiTestHost.BodyOf(lowest, Ct)).GetProperty("limitPer24Hours").GetInt32());

        await using var context = _db.NewContext();
        Assert.Equal(20, (await context.GetSettingsAsync(Ct)).EmailLimitPer24Hours);
    }

    [Fact]
    public async Task TheSettingsPage_ShowsTheCountAndTheQueue_WithoutBodies()
    {
        var (host, _) = await StartAsync(limit: EmailLimit.Minimum);
        await using var _h = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SentAtAsync(EmailLimit.Minimum, host.Clock.UtcNow);
        await SendAsync(host, Account("waiting@example.com"));

        // The test message is other email, and at the minimum limit other email has no room.
        var test = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, "/api/settings/email/test", new { to = "me@example.com" }, cookie, Ct), Ct);
        Assert.False(test.GetProperty("sent").GetBoolean());
        Assert.True(test.GetProperty("queued").GetBoolean());

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/settings/email", null, cookie, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var view = await ApiTestHost.BodyOf(response, Ct);

        Assert.Equal(EmailLimit.Minimum, view.GetProperty("sentInLast24Hours").GetInt32());
        Assert.Equal(2, view.GetProperty("queued").GetInt32());
        Assert.Equal(host.Clock.UtcNow + TimeSpan.FromHours(24), view.GetProperty("nextSendAt").GetDateTimeOffset());

        var rows = view.GetProperty("emails").EnumerateArray().ToList();
        Assert.Equal("waiting@example.com", rows[0].GetProperty("to").GetString());
        Assert.Equal("account", rows[0].GetProperty("kind").GetString());
        Assert.Equal("queued", rows[0].GetProperty("state").GetString());
        Assert.Equal("other", rows[1].GetProperty("kind").GetString());

        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("body", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET", "/api/settings/email")]
    [InlineData("PUT", "/api/settings/email/limit")]
    [InlineData("POST", "/api/settings/email/test")]
    public async Task TheEmailSettings_NeedManageSettings(string method, string path)
    {
        var (host, relay) = await StartAsync();
        await using var _ = host;

        var (_, cookie) = await host.SignedInAsync(
            ModbotPermissions.ManageUsers | ModbotPermissions.ViewOperationalLog | ModbotPermissions.ViewAuditLog, Ct);

        object? body = method switch
        {
            "PUT" => new { limitPer24Hours = 50 },
            "POST" => new { to = "me@example.com" },
            _ => null,
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(new HttpMethod(method), path, body, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(new HttpMethod(method), path, body, null, Ct)).StatusCode);

        Assert.Empty(relay.Tried);
        await using var context = _db.NewContext();
        Assert.Equal(EmailLimit.Default, (await context.GetSettingsAsync(Ct)).EmailLimitPer24Hours);
    }
}
