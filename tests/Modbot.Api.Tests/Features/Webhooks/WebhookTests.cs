using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Webhooks;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Webhooks;

/// <summary>A receiver that records every request and answers from a script.</summary>
public sealed class FakeReceiver : HttpMessageHandler
{
    public sealed record Received(Uri Url, Dictionary<string, string> Headers, byte[] Body);

    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _answers = new();

    public List<Received> Requests { get; } = [];

    /// <summary>What to answer once the script runs out.</summary>
    public HttpStatusCode Otherwise { get; set; } = HttpStatusCode.OK;

    public void Then(HttpStatusCode code, Action<HttpResponseMessage>? shape = null)
        => _answers.Enqueue(() =>
        {
            var response = new HttpResponseMessage(code);
            shape?.Invoke(response);
            return response;
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                headers[h.Key] = string.Join(",", h.Value);
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        lock (Requests)
            Requests.Add(new Received(request.RequestUri!, headers, body));

        return _answers.TryDequeue(out var answer) ? answer() : new HttpResponseMessage(Otherwise);
    }
}

/// <summary>
/// Webhooks (API keys design §6): signed, in order, retried with backoff, turned off after a day
/// of failing, kept away from private addresses, and managed only by the right people.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WebhookTests
{
    private const string Path = "/api/webhooks";

    private readonly PostgresFixture _db;

    public WebhookTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, FakeReceiver Receiver)> StartAsync(WebhookOptions? options = null)
    {
        // Every webhook in the shared database would otherwise be delivered through this test's
        // receiver.
        await using (var db = _db.NewContext())
        {
            await db.Webhooks.ExecuteDeleteAsync(Ct);
            var settings = await db.GetSettingsAsync(Ct);
            settings.WebhooksAllowPrivateAddresses = false;
            await db.SaveChangesAsync(Ct);
        }

        var receiver = new FakeReceiver();
        options ??= new WebhookOptions();

        var host = await ApiTestHost.StartAsync(_db, configure: services =>
        {
            services.AddSingleton(options);
            services.AddSingleton(sp => new WebhookSender(sp.GetRequiredService<IModbotClock>(), options, null, receiver));
        });

        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(host.Clock.UtcNow, Ct);

        return (host, receiver);
    }

    private static async Task<(Guid Id, string Secret)> CreateAsync(
        ApiTestHost host, string cookie, string[] types, string url = "https://hooks.example.com/modbot", string[]? subjects = null)
    {
        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path, new { name = "Receiver", url, eventTypes = types, subjectIds = subjects ?? [], enabled = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ApiTestHost.BodyOf(response, Ct);
        return (body.GetProperty("webhook").GetProperty("id").GetGuid(), body.GetProperty("secret").GetString()!);
    }

    private static async Task<long> WriteFactAsync(ApiTestHost host, string type, string subject)
    {
        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = host.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            Source = FactSource.AuditLog,
            Data = new JsonObject { ["note"] = "é ü" },
        }, Ct);

        return result.Id;
    }

    private static async Task<int> RunAsync(ApiTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookDispatcher>().RunOnceAsync(Ct);
    }

    private async Task<Webhook> RowAsync(Guid id)
    {
        await using var db = _db.NewContext();
        return await db.Webhooks.AsNoTracking().SingleAsync(w => w.Id == id, Ct);
    }

    private static string Subject() => $"usr_{Guid.NewGuid():N}";

    /// <summary>Written from the documentation, not from the server's code.</summary>
    private static string ReferenceSignature(string secret, string timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return "v1=" + string.Concat(hmac.ComputeHash(signed).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ADelivery_IsSignedTheWayTheDocsSay()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, secret) = await CreateAsync(host, cookie, ["vrchat.group.member.*"]);

        Assert.StartsWith(WebhookSignature.SecretPrefix, secret, StringComparison.Ordinal);

        var subject = Subject();
        var fact = await WriteFactAsync(host, FactType.MemberBanned, subject);

        Assert.Equal(1, await RunAsync(host));

        var request = Assert.Single(receiver.Requests);
        Assert.Equal("https://hooks.example.com/modbot", request.Url.ToString());
        Assert.Equal(host.Clock.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), request.Headers["Modbot-Timestamp"]);
        Assert.Equal(ReferenceSignature(secret, request.Headers["Modbot-Timestamp"], request.Body), request.Headers["Modbot-Signature"]);
        Assert.Equal(fact.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Headers["Modbot-Event-Id"]);
        Assert.Equal(FactType.MemberBanned, request.Headers["Modbot-Event-Type"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);

        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(subject, body.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal(1, body.GetProperty("version").GetInt32());

        // A wrong secret does not verify.
        Assert.NotEqual(ReferenceSignature(secret + "x", request.Headers["Modbot-Timestamp"], request.Body), request.Headers["Modbot-Signature"]);

        var row = await RowAsync(id);
        Assert.Equal(fact, row.DeliveredThrough);
        Assert.NotNull(row.LastSuccessAt);
    }

    [Fact]
    public async Task TheSecret_IsShownOnce_StoredEncrypted_AndNeverInAFact()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, secret) = await CreateAsync(host, cookie, ["*"]);

        var list = await (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(secret, list, StringComparison.Ordinal);

        var row = await RowAsync(id);
        Assert.NotEqual(secret, row.SecretEncrypted);
        Assert.Equal(secret, host.Services.GetRequiredService<ISecretProtector>().Unprotect(row.SecretEncrypted));

        var fact = Assert.Single(await host.FactsAsync(FactType.WebhookCreated, id.ToString(), Ct));
        Assert.DoesNotContain(secret, fact.Data, StringComparison.Ordinal);

        var rolled = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/secret", null, cookie, Ct), Ct);
        var second = rolled.GetProperty("secret").GetString()!;
        Assert.NotEqual(secret, second);
        Assert.Equal(second, host.Services.GetRequiredService<ISecretProtector>().Unprotect((await RowAsync(id)).SecretEncrypted));
    }

    [Fact]
    public async Task EventsGoInOrder_FilteredByTypeSubjectAndWhatTheOwnerMaySee()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);

        var subject = Subject();
        await CreateAsync(host, cookie, ["*"], subjects: [subject]);

        var ban = await WriteFactAsync(host, FactType.MemberBanned, subject);
        await WriteFactAsync(host, FactType.MemberBanned, Subject());       // another subject
        await WriteFactAsync(host, FactType.SettingsChanged, subject);      // operational: the owner cannot see it
        await WriteFactAsync(host, FactType.InstanceJoined, subject);       // presence: needs ViewLiveInstances
        var join = await WriteFactAsync(host, FactType.MemberJoined, subject);

        await RunAsync(host);

        Assert.Equal(
            [ban.ToString(System.Globalization.CultureInfo.InvariantCulture), join.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            receiver.Requests.Select(r => r.Headers["Modbot-Event-Id"]));
    }

    [Fact]
    public async Task AFailure_IsRetriedWithBackoff_AndHoldsLaterEventsBehindIt()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, _) = await CreateAsync(host, cookie, ["vrchat.group.member.*"]);

        var subject = Subject();
        var first = await WriteFactAsync(host, FactType.MemberBanned, subject);
        var second = await WriteFactAsync(host, FactType.MemberJoined, subject);

        receiver.Then(HttpStatusCode.InternalServerError);
        receiver.Then(HttpStatusCode.BadGateway);

        Assert.Equal(1, await RunAsync(host));
        var row = await RowAsync(id);
        Assert.Equal(1, row.FailedAttempts);
        Assert.Equal(host.Clock.UtcNow.AddSeconds(10), row.NextAttemptAt);
        Assert.Equal(first - 1, row.DeliveredThrough);

        // Not due yet.
        Assert.Equal(0, await RunAsync(host));

        host.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await RunAsync(host));
        row = await RowAsync(id);
        Assert.Equal(2, row.FailedAttempts);
        Assert.Equal(host.Clock.UtcNow.AddSeconds(30), row.NextAttemptAt);

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, await RunAsync(host));

        row = await RowAsync(id);
        Assert.Equal(second, row.DeliveredThrough);
        Assert.Equal(0, row.FailedAttempts);
        Assert.Null(row.FailingSince);
        Assert.Null(row.LastError);

        var ids = receiver.Requests.Select(r => r.Headers["Modbot-Event-Id"]).ToList();
        Assert.Equal([first, first, first, second], ids.Select(long.Parse));

        var log = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/{id}/deliveries", null, cookie, Ct), Ct);
        Assert.Equal(["delivered", "delivered", "retrying", "retrying"], log.EnumerateArray().Select(d => d.GetProperty("outcome").GetString()));
        Assert.Equal([1, 3, 2, 1], log.EnumerateArray().Select(d => d.GetProperty("attempt").GetInt32()));
        Assert.Equal(502, log.EnumerateArray().ElementAt(2).GetProperty("statusCode").GetInt32());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(408)]
    public async Task RetryAfter_IsHonoured_UpToAnHour(int code)
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, _) = await CreateAsync(host, cookie, ["vrchat.group.member.*"]);
        await WriteFactAsync(host, FactType.MemberBanned, Subject());

        receiver.Then((HttpStatusCode)code, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));
        await RunAsync(host);
        Assert.Equal(host.Clock.UtcNow.AddSeconds(120), (await RowAsync(id)).NextAttemptAt);

        host.Clock.Advance(TimeSpan.FromSeconds(120));
        receiver.Then((HttpStatusCode)code, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(2)));
        await RunAsync(host);
        Assert.Equal(host.Clock.UtcNow.AddHours(1), (await RowAsync(id)).NextAttemptAt);
    }

    [Fact]
    public async Task AnyOther4xx_SkipsTheEvent_WithoutRetrying()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, _) = await CreateAsync(host, cookie, ["vrchat.group.member.*"]);

        var subject = Subject();
        await WriteFactAsync(host, FactType.MemberBanned, subject);
        var second = await WriteFactAsync(host, FactType.MemberJoined, subject);

        receiver.Then(HttpStatusCode.NotFound);

        Assert.Equal(2, await RunAsync(host));

        var row = await RowAsync(id);
        Assert.Equal(second, row.DeliveredThrough);
        Assert.Null(row.NextAttemptAt);

        // A redirect is not followed and not retried either.
        receiver.Then(HttpStatusCode.Found);
        await WriteFactAsync(host, FactType.MemberKicked, subject);
        Assert.Equal(1, await RunAsync(host));
        Assert.Null((await RowAsync(id)).NextAttemptAt);
    }

    [Fact]
    public async Task ADayOfFailures_TurnsTheWebhookOff_WithTheReason()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, _) = await CreateAsync(host, cookie, ["vrchat.group.member.*"]);
        await WriteFactAsync(host, FactType.MemberBanned, Subject());

        receiver.Otherwise = HttpStatusCode.ServiceUnavailable;

        var started = host.Clock.UtcNow;
        while (host.Clock.UtcNow - started < TimeSpan.FromHours(25) && (await RowAsync(id)).Enabled)
        {
            await RunAsync(host);
            host.Clock.Advance(TimeSpan.FromHours(1));
        }

        var row = await RowAsync(id);
        Assert.False(row.Enabled);
        Assert.StartsWith("Failing since", row.DisabledReason, StringComparison.Ordinal);
        Assert.Contains("503", row.DisabledReason, StringComparison.Ordinal);
        Assert.Single(await host.FactsAsync(FactType.WebhookDisabled, id.ToString(), Ct));

        var view = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct))
            .GetProperty("webhooks").EnumerateArray().Single();
        Assert.Equal("stopped", view.GetProperty("state").GetString());

        // Nothing more is sent while it is off.
        var sent = receiver.Requests.Count;
        host.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await RunAsync(host));
        Assert.Equal(sent, receiver.Requests.Count);

        // Turned back on, it carries on with a clean slate.
        receiver.Otherwise = HttpStatusCode.OK;
        var on = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}",
            new { name = "Receiver", url = "https://hooks.example.com/modbot", eventTypes = new[] { "vrchat.group.member.*" }, subjectIds = Array.Empty<string>(), enabled = true },
            cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(1, await RunAsync(host));
        Assert.Equal("working", (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct))
            .GetProperty("webhooks").EnumerateArray().Single().GetProperty("state").GetString());
    }

    [Fact]
    public async Task AWebhookWhoseOwnerIsDisabled_IsTurnedOff()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (owner, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, _) = await CreateAsync(host, cookie, ["*"]);

        await using (var db = _db.NewContext())
            await db.Users.Where(u => u.Id == owner.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDisabled, true), Ct);

        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);

        Assert.Empty(receiver.Requests);
        Assert.False((await RowAsync(id)).Enabled);
    }

    [Fact]
    public async Task SendTest_DeliversATestEvent_AndLogsIt_KeepingOnlyTheNewestFew()
    {
        var (host, receiver) = await StartAsync(new WebhookOptions { DeliveriesKept = 3 });
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, secret) = await CreateAsync(host, cookie, ["*"]);

        receiver.Then(HttpStatusCode.Unauthorized);
        var result = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/test", null, cookie, Ct), Ct);
        Assert.Equal(401, result.GetProperty("statusCode").GetInt32());
        Assert.True(result.GetProperty("test").GetBoolean());

        var request = Assert.Single(receiver.Requests);
        Assert.Equal("modbot.webhook.test", request.Headers["Modbot-Event-Type"]);
        Assert.Equal(ReferenceSignature(secret, request.Headers["Modbot-Timestamp"], request.Body), request.Headers["Modbot-Signature"]);

        for (var i = 0; i < 4; i++)
            await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/test", null, cookie, Ct);

        var log = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/{id}/deliveries", null, cookie, Ct), Ct);
        Assert.Equal(3, log.GetArrayLength());
    }

    [Fact]
    public async Task OnlyTheOwnerOrAnAdministrator_ChangesAWebhook_ButAnyoneMayTurnItOffOrDeleteIt()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, ownerCookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, otherCookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, adminCookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (id, _) = await CreateAsync(host, ownerCookie, ["*"]);

        object Body(string url, bool enabled) => new { name = "Receiver", url, eventTypes = new[] { "*" }, subjectIds = Array.Empty<string>(), enabled };

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("https://attacker.example.com/", true), otherCookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/secret", null, otherCookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/test", null, otherCookie, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("https://hooks.example.com/modbot", false), otherCookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("https://hooks.example.com/other", true), adminCookie, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, otherCookie, Ct)).StatusCode);
        Assert.Single(await host.FactsAsync(FactType.WebhookDeleted, id.ToString(), Ct));
    }

    private static async Task<(Guid Id, string Key)> MakeKeyAsync(
        ApiTestHost host, string cookie, string[] permissions, DateTimeOffset? expiresAt = null)
    {
        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/api-keys", new { name = "Bot", permissions, expiresAt }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ApiTestHost.BodyOf(response, Ct);
        return (body.GetProperty("apiKey").GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }

    private static Task<HttpResponseMessage> WithKeyAsync(ApiTestHost host, HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        return host.Client.SendAsync(request, Ct);
    }

    private static async Task<Guid> CreateWithKeyAsync(ApiTestHost host, string key, string[] types, string url)
    {
        var response = await WithKeyAsync(
            host, HttpMethod.Post, Path, key, new { name = "Receiver", url, eventTypes = types, subjectIds = Array.Empty<string>(), enabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("webhook").GetProperty("id").GetGuid();
    }

    private static object WebhookBody(string url, bool enabled)
        => new { name = "Receiver", url, eventTypes = new[] { "*" }, subjectIds = Array.Empty<string>(), enabled };

    private static string[] EventTypesSentTo(FakeReceiver receiver, string address)
        => receiver.Requests
            .Where(r => r.Url.AbsoluteUri == address)
            .Select(r => r.Headers["Modbot-Event-Type"])
            .ToArray();

    [Fact]
    public async Task AWebhookMadeWithAKey_SendsOnlyWhatTheKeyAllows_WhateverItsAccountMaySee()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (owner, cookie) = await host.SignedInAsync(
            ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewLiveInstances, Ct);
        var (keyId, key) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);

        var byKey = await CreateWithKeyAsync(host, key, ["*"], "https://hooks.example.com/key");
        await CreateAsync(host, cookie, ["*"], "https://hooks.example.com/browser");

        var row = await RowAsync(byKey);
        Assert.Equal(keyId, row.CreatedByKeyId);
        Assert.Equal(owner.Id, row.CreatedByUserId);

        var subject = Subject();
        await WriteFactAsync(host, FactType.MemberBanned, subject);
        await WriteFactAsync(host, FactType.InstanceJoined, subject);

        await RunAsync(host);

        // The key was not given ViewLiveInstances; the browser's webhook has all the account's.
        Assert.Equal([FactType.MemberBanned], EventTypesSentTo(receiver, "https://hooks.example.com/key"));
        Assert.Equal([FactType.MemberBanned, FactType.InstanceJoined], EventTypesSentTo(receiver, "https://hooks.example.com/browser"));
    }

    [Fact]
    public async Task AWebhookMadeWithAKey_LosesWhatItsAccountLoses()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (owner, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, key) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);
        await CreateWithKeyAsync(host, key, ["*"], "https://hooks.example.com/key");

        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);
        Assert.Single(receiver.Requests);

        // The account gives up the audit log: the key is capped by that on the very next pass.
        await using (var db = _db.NewContext())
        {
            var lesser = await TestAccounts.RoleForAsync(db, ModbotPermissions.ManageApiKeys, Ct);
            await db.UserRoles.Where(ur => ur.UserId == owner.Id).ExecuteDeleteAsync(Ct);
            db.UserRoles.Add(new ModbotUserRole { UserId = owner.Id, RoleId = lesser });
            await db.SaveChangesAsync(Ct);
        }

        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task ARevokedKey_TurnsItsWebhookOff_WithTheReason_AndABrowserOneKeepsSending()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (keyId, key) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);
        var byKey = await CreateWithKeyAsync(host, key, ["*"], "https://hooks.example.com/key");
        var byBrowser = (await CreateAsync(host, cookie, ["*"], "https://hooks.example.com/browser")).Id;

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/api-keys/{keyId}", null, cookie, Ct)).StatusCode);

        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);

        Assert.Empty(EventTypesSentTo(receiver, "https://hooks.example.com/key"));
        Assert.Single(EventTypesSentTo(receiver, "https://hooks.example.com/browser"));

        var stopped = await RowAsync(byKey);
        Assert.False(stopped.Enabled);
        Assert.Equal("The API key that set it up was revoked.", stopped.DisabledReason);
        Assert.Single(await host.FactsAsync(FactType.WebhookDisabled, byKey.ToString(), Ct));
        Assert.True((await RowAsync(byBrowser)).Enabled);

        var view = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct))
            .GetProperty("webhooks").EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == byKey);
        Assert.Equal("stopped", view.GetProperty("state").GetString());

        // Turned back on while the key is still revoked, it stops again on the next pass.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{byKey}", WebhookBody("https://hooks.example.com/key", true), cookie, Ct)).StatusCode);
        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);
        Assert.False((await RowAsync(byKey)).Enabled);
        Assert.Empty(EventTypesSentTo(receiver, "https://hooks.example.com/key"));
    }

    [Fact]
    public async Task AKeyThatExpires_TurnsItsWebhookOff_WithTheReason()
    {
        var (host, receiver) = await StartAsync();
        await using var _ = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, key) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"], host.Clock.UtcNow.AddHours(1));
        var id = await CreateWithKeyAsync(host, key, ["*"], "https://hooks.example.com/key");

        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);
        Assert.Single(receiver.Requests);

        host.Clock.Advance(TimeSpan.FromHours(2));
        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await RunAsync(host);

        Assert.Single(receiver.Requests);
        var row = await RowAsync(id);
        Assert.False(row.Enabled);
        Assert.Equal("The API key that set it up has expired.", row.DisabledReason);
    }

    [Fact]
    public async Task AKey_ChangesOnlyTheWebhooksItMade_AndABrowserSessionKeepsItsOwnRule()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, key) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);
        var (_, otherKey) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);

        var (browserMade, _) = await CreateAsync(host, cookie, ["*"]);
        var ownMade = await CreateWithKeyAsync(host, key, ["*"], "https://hooks.example.com/own");

        // A webhook made in the browser sends what the whole account may see, which is more than
        // the key may: repointing it would hand the key that view.
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Put, $"{Path}/{browserMade}", key, WebhookBody("https://attacker.example.com/", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Post, $"{Path}/{browserMade}/secret", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Post, $"{Path}/{browserMade}/test", key)).StatusCode);

        // So is one made with another key, though it is the same account's.
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Put, $"{Path}/{ownMade}", otherKey, WebhookBody("https://attacker.example.com/", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Post, $"{Path}/{ownMade}/secret", otherKey)).StatusCode);

        // Its own: changed, secret replaced, tested.
        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Put, $"{Path}/{ownMade}", key, WebhookBody("https://hooks.example.com/moved", true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Post, $"{Path}/{ownMade}/secret", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Post, $"{Path}/{ownMade}/test", key)).StatusCode);

        // Turning one off is still anybody's.
        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Put, $"{Path}/{browserMade}", otherKey, WebhookBody("https://hooks.example.com/modbot", false))).StatusCode);

        // The account in the browser keeps what it had: both webhooks are its own.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{ownMade}", WebhookBody("https://hooks.example.com/back", true), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{browserMade}/secret", null, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task EverythingDoneThroughAKey_RecordsTheKey_AndTheBrowsersDoesNot()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (keyId, key) = await MakeKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);

        var byKey = await CreateWithKeyAsync(host, key, ["*"], "https://hooks.example.com/key");
        var (byBrowser, _) = await CreateAsync(host, cookie, ["*"], "https://hooks.example.com/browser");

        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Post, $"{Path}/{byKey}/secret", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await WithKeyAsync(host, HttpMethod.Delete, $"{Path}/{byKey}", key)).StatusCode);

        // Made, secret replaced, deleted: three facts, each naming the key.
        foreach (var type in new[] { FactType.WebhookCreated, FactType.WebhookSecretChanged, FactType.WebhookDeleted })
        {
            var fact = Assert.Single(await host.FactsAsync(type, byKey.ToString(), Ct));
            Assert.Equal(keyId.ToString(), ApiTestHost.DataOf(fact).GetProperty("apiKeyId").GetString());
        }

        var browserFact = Assert.Single(await host.FactsAsync(FactType.WebhookCreated, byBrowser.ToString(), Ct));
        Assert.False(ApiTestHost.DataOf(browserFact).TryGetProperty("apiKeyId", out _));

        // The key itself was made in the browser.
        var keyMade = Assert.Single(await host.FactsAsync(FactType.ApiKeyCreated, keyId.ToString(), Ct));
        Assert.False(ApiTestHost.DataOf(keyMade).TryGetProperty("apiKeyId", out _));
    }

    [Fact]
    public async Task TheFactWriter_MarksOnlyAFactWhoseActorIsTheKeysAccount_OnARequestWithAKey()
    {
        var accountId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var caller = new Modbot.Api.Auth.ApiCaller(accountId, "someone", keyId, ModbotPermissions.ViewAuditLog);
        var now = DateTimeOffset.UnixEpoch;

        FactRecord Fact(FactPlatform? platform, string? actor) => new()
        {
            Type = FactType.WebhookCreated,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = "x",
            ActorPlatform = platform,
            ActorId = actor,
            Source = FactSource.Modbot,
            Data = new JsonObject { ["name"] = "n" },
        };

        async Task<FactRecord> WrittenAsync(System.Security.Claims.ClaimsPrincipal? user, FactRecord fact)
        {
            var inner = new CapturingFactWriter();
            var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
            {
                HttpContext = user is null ? null : new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user },
            };

            await new Modbot.Api.Auth.KeyAttributingFactWriter(inner, accessor).WriteAsync(fact, Ct);
            return Assert.Single(inner.Written);
        }

        var keyRequest = Modbot.Api.Auth.ApiKeyAuthentication.CreatePrincipal(caller, now);
        var sessionRequest = Modbot.Api.Auth.ApiKeyAuthentication.CreatePrincipal(caller with { ApiKeyId = null }, now);

        var marked = await WrittenAsync(keyRequest, Fact(FactPlatform.Modbot, accountId.ToString()));
        Assert.Equal(keyId.ToString(), marked.Data!["apiKeyId"]!.GetValue<string>());
        Assert.Equal("n", marked.Data["name"]!.GetValue<string>());

        // Not the caller's own doing, not from a key, or not in a request at all.
        Assert.False((await WrittenAsync(keyRequest, Fact(FactPlatform.Modbot, Guid.NewGuid().ToString()))).Data!.ContainsKey("apiKeyId"));
        Assert.False((await WrittenAsync(keyRequest, Fact(FactPlatform.VRChat, accountId.ToString()))).Data!.ContainsKey("apiKeyId"));
        Assert.False((await WrittenAsync(keyRequest, Fact(null, null))).Data!.ContainsKey("apiKeyId"));
        Assert.False((await WrittenAsync(sessionRequest, Fact(FactPlatform.Modbot, accountId.ToString()))).Data!.ContainsKey("apiKeyId"));
        Assert.False((await WrittenAsync(null, Fact(FactPlatform.Modbot, accountId.ToString()))).Data!.ContainsKey("apiKeyId"));
    }

    private sealed class CapturingFactWriter : IFactWriter
    {
        public List<FactRecord> Written { get; } = [];

        public Task<FactWriteResult> WriteAsync(FactRecord fact, CancellationToken ct = default)
        {
            Written.Add(fact);
            return Task.FromResult(new FactWriteResult(Written.Count, false));
        }

        public Task<IReadOnlyList<FactWriteResult>> WriteManyAsync(IEnumerable<FactRecord> facts, CancellationToken ct = default)
        {
            var results = facts.Select(f => WriteAsync(f, ct).Result).ToList();
            return Task.FromResult<IReadOnlyList<FactWriteResult>>(results);
        }

        public Task<long?> AlreadyRecordedAsync(FactRecord fact, TimeSpan within, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
    }

    [Fact]
    public async Task ManagingWebhooks_NeedsThePermission_AndTheAddressSettingNeedsSettings()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, settings) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, Path,
            new { name = "x", url = "https://hooks.example.com/", eventTypes = new[] { "*" }, enabled = true }, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, Path, null, null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, "/api/settings/webhooks", new { allowPrivateAddresses = true }, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, "/api/settings/webhooks", new { allowPrivateAddresses = true }, settings, Ct)).StatusCode);

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, manager, Ct), Ct);
        Assert.True(list.GetProperty("allowPrivateAddresses").GetBoolean());
        Assert.False(list.GetProperty("canChangeAllowPrivateAddresses").GetBoolean());
    }

    [Fact]
    public async Task PrivateAddresses_AreRefusedUnlessAllowed()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog | ModbotPermissions.ManageSettings, Ct);

        foreach (var url in new[] { "https://localhost/hook", "https://10.0.0.5/hook", "https://[::1]/hook", "https://169.254.169.254/latest", "http://hooks.example.com/" })
        {
            var refused = await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "x", url, eventTypes = new[] { "*" }, enabled = true }, cookie, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        await host.SendJsonAsync(HttpMethod.Put, "/api/settings/webhooks", new { allowPrivateAddresses = true }, cookie, Ct);

        var allowed = await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "lan", url = "http://192.168.1.20:8080/hook", eventTypes = new[] { "*" }, enabled = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("64:ff9b::a00:1", true)]
    [InlineData("2002:a00:1::", true)]
    [InlineData("2606:4700:4700::1111", false)]
    public void TheAddressRules(string address, bool blocked)
        => Assert.Equal(blocked, WebhookAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public async Task TheGuard_RefusesAtConnectTime_WhatANameResolvesTo()
    {
        // "localhost" resolves on the machine itself, so this reaches the connect-time check without
        // any network, the way a public-looking name that resolves to a private address would.
        using var client = new HttpClient(new SocketsHttpHandler { ConnectCallback = WebhookAddressGuard.ConnectAsync, UseProxy = false });

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:9/", Ct));
        Assert.IsAssignableFrom<WebhookAddressBlockedException>(e.InnerException);
    }

    [Fact]
    public void Backoff_Grows_ThenStaysAtAnHour()
    {
        var options = new WebhookOptions();

        Assert.Equal(
            [10, 30, 90, 270, 810, 2430, 3600, 3600],
            Enumerable.Range(1, 8).Select(n => (int)options.RetryDelay(n, null).TotalSeconds));
    }
}
