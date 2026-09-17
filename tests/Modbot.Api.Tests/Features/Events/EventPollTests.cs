using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Events;

/// <summary>
/// Long polling (API keys design §5.6): the same stream as the WebSocket, answered at once or after
/// a wait, woken by a written fact, limited per key, and never missing a late-committing fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventPollTests
{
    private const string Path = EventPollEndpoint.PollPath;

    private readonly PostgresFixture _db;

    public EventPollTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <remarks>
    /// The fallback check is set far longer than any wait in these tests, so a poll that returns
    /// early can only have been woken by the signal.
    /// </remarks>
    private static Task<ApiTestHost> StartAsync(PostgresFixture db, int maxConnections = 5)
        => ApiTestHost.StartAsync(db, configure: services => services.AddSingleton(new EventSocketOptions
        {
            PollInterval = TimeSpan.FromSeconds(30),
            MaxConnectionsPerCaller = maxConnections,
        }));

    private static async Task<(Guid Id, string Key, Guid UserId)> KeyAsync(ApiTestHost host, params string[] permissions)
    {
        var (user, cookie) = await host.SignedInAsync(
            PermissionsOf(permissions) | ModbotPermissions.ManageApiKeys, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/api-keys", new { name = "poller", permissions }, cookie, Ct);
        response.EnsureSuccessStatusCode();
        var body = await ApiTestHost.BodyOf(response, Ct);
        return (body.GetProperty("apiKey").GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!, user.Id);
    }

    private static ModbotPermissions PermissionsOf(string[] names)
        => names.Aggregate(ModbotPermissions.None, (all, n) => all | Enum.Parse<ModbotPermissions>(n));

    private static Task<HttpResponseMessage> PollAsync(ApiTestHost host, string key, string query, CancellationToken? ct = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Path}?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return host.Client.SendAsync(request, ct ?? Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<long> NewestAsync(ApiTestHost host)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(host.Clock.UtcNow, Ct);
        return await new FactFeed(scope.ServiceProvider.GetRequiredService<ModbotContext>()).NewestIdAsync(Ct);
    }

    private static async Task<long> WriteAsync(ApiTestHost host, string type, string subject)
    {
        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(Record(type, subject, host.Clock.UtcNow), Ct);
        return result.Id;
    }

    private static FactRecord Record(string type, string subject, DateTimeOffset at) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        Source = FactSource.AuditLog,
        Data = new JsonObject(),
    };

    private static string Subject() => $"usr_{Guid.NewGuid():N}";

    private static string Text(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static List<string> Ids(JsonElement body)
        => body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task EventsAfterTheCursor_ReturnAtOnce()
    {
        await using var host = await StartAsync(_db);
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        var before = await NewestAsync(host);
        var subject = Subject();
        var first = await WriteAsync(host, FactType.MemberBanned, subject);
        var second = await WriteAsync(host, FactType.MemberJoined, subject);

        var clock = Stopwatch.StartNew();
        var response = await PollAsync(host, key, $"cursor={before}&subjects={subject}&wait=30");
        var body = await BodyAsync(response);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal([Text(first), Text(second)], Ids(body));
        Assert.Equal(Text(second), body.GetProperty("cursor").GetString());
        Assert.False(body.GetProperty("more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("notice").ValueKind);

        var envelope = body.GetProperty("events")[0];
        Assert.Equal(1, envelope.GetProperty("version").GetInt32());
        Assert.Equal(FactType.MemberBanned, envelope.GetProperty("type").GetString());
        Assert.Equal(subject, envelope.GetProperty("subject").GetProperty("id").GetString());
    }

    [Fact]
    public async Task AWaitingPoll_ReturnsWhenAFactIsWritten()
    {
        await using var host = await StartAsync(_db);
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        var subject = Subject();

        var clock = Stopwatch.StartNew();
        var polling = PollAsync(host, key, $"subjects={subject}&wait=20");

        await Task.Delay(500, Ct);
        Assert.False(polling.IsCompleted);

        var written = await WriteAsync(host, FactType.MemberKicked, subject);
        var body = await BodyAsync(await polling);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Took {clock.Elapsed}");
        Assert.Equal([Text(written)], Ids(body));
    }

    [Fact]
    public async Task AFactWrittenWithoutThePulse_StillWakesAWaitingPoll_ThroughTheWatcher()
    {
        await using var host = await ApiTestHost.StartAsync(_db, configure: services =>
        {
            services.AddSingleton(new EventSocketOptions { PollInterval = TimeSpan.FromSeconds(30) });
            services.AddHostedService<FactFeedWatcher>();
        });
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        await NewestAsync(host);
        var subject = Subject();

        var clock = Stopwatch.StartNew();
        var polling = PollAsync(host, key, $"subjects={subject}&wait=20");
        await Task.Delay(1000, Ct);

        // A writer with no signal, the way a transaction that commits after its insert looks.
        await using var db = _db.NewContext();
        var written = await new FactWriter(db, host.Clock).WriteAsync(Record(FactType.MemberBanned, subject, host.Clock.UtcNow), Ct);

        var body = await BodyAsync(await polling);
        Assert.Equal([Text(written.Id)], Ids(body));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Took {clock.Elapsed}");
    }

    [Fact]
    public async Task AWaitingPoll_ReturnsEmptyAtTheTimeout_WithTheCursorWhereItWas()
    {
        await using var host = await StartAsync(_db);
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        var newest = await NewestAsync(host);

        var clock = Stopwatch.StartNew();
        var body = await BodyAsync(await PollAsync(host, key, $"cursor={newest}&wait=1"));

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900));
        Assert.Empty(Ids(body));
        Assert.Equal(Text(newest), body.GetProperty("cursor").GetString());
        Assert.False(body.GetProperty("more").GetBoolean());

        // No cursor means from now: an empty wait hands back the newest id to carry on from.
        var fromNow = await BodyAsync(await PollAsync(host, key, "wait=0"));
        Assert.Empty(Ids(fromNow));
        Assert.Equal(Text(newest), fromNow.GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task ACursor_PagesThroughWithMore()
    {
        await using var host = await StartAsync(_db);
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        var before = await NewestAsync(host);
        var subject = Subject();

        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(Text(await WriteAsync(host, FactType.MemberJoined, subject)));

        var seen = new List<string>();
        var cursor = Text(before);
        var mores = new List<bool>();

        for (var i = 0; i < 3; i++)
        {
            var body = await BodyAsync(await PollAsync(host, key, $"cursor={cursor}&subjects={subject}&limit=2&wait=0"));
            seen.AddRange(Ids(body));
            mores.Add(body.GetProperty("more").GetBoolean());
            cursor = body.GetProperty("cursor").GetString()!;
        }

        Assert.Equal(ids, seen);
        Assert.Equal([true, true, false], mores);
        Assert.Equal(ids[^1], cursor);
    }

    [Fact]
    public async Task TypeAndSubjectFilters_Narrow()
    {
        await using var host = await StartAsync(_db);
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        var before = await NewestAsync(host);
        var wanted = Subject();
        var other = Subject();

        await WriteAsync(host, FactType.MemberBanned, other);
        await WriteAsync(host, FactType.RoleUpdated, wanted);
        var ban = await WriteAsync(host, FactType.MemberBanned, wanted);
        var join = await WriteAsync(host, FactType.MemberJoined, wanted);

        var body = await BodyAsync(await PollAsync(
            host, key, $"cursor={before}&types=vrchat.group.member.ban,vrchat.group.member.join&subjects={wanted}&wait=0"));
        Assert.Equal([Text(ban), Text(join)], Ids(body));

        var prefixed = await BodyAsync(await PollAsync(
            host, key, $"cursor={before}&types=vrchat.group.member.*&subjects={wanted}&subjects={other}&wait=0"));
        Assert.Equal(3, Ids(prefixed).Count);

        Assert.Equal(HttpStatusCode.BadRequest, (await PollAsync(host, key, "types=Banned&wait=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PollAsync(host, key, "cursor=abc&wait=0")).StatusCode);
    }

    [Fact]
    public async Task APoll_IsSentOnlyWhatTheKeyMaySee()
    {
        await using var host = await StartAsync(_db);
        var (_, auditOnly, _) = await KeyAsync(host, "ViewAuditLog");
        var (_, withLive, _) = await KeyAsync(host, "ViewAuditLog", "ViewLiveInstances");
        var before = await NewestAsync(host);
        var subject = Subject();

        var joined = await WriteAsync(host, FactType.InstanceJoined, subject);
        await WriteAsync(host, FactType.SettingsChanged, subject);
        var ban = await WriteAsync(host, FactType.MemberBanned, subject);

        var plain = await BodyAsync(await PollAsync(host, auditOnly, $"cursor={before}&subjects={subject}&wait=0"));
        Assert.Equal([Text(ban)], Ids(plain));

        var live = await BodyAsync(await PollAsync(host, withLive, $"cursor={before}&subjects={subject}&wait=0"));
        Assert.Equal([Text(joined), Text(ban)], Ids(live));
    }

    [Fact]
    public async Task OnlyAKeyInTheHeader_MayPoll()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, $"{Path}?wait=0", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"{Path}?wait=0", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PollAsync(host, Modbot.Api.Auth.ApiKeySecrets.NewKey(), "wait=0")).StatusCode);

        var (_, membersOnly, _) = await KeyAsync(host, "ViewMembers");
        Assert.Equal(HttpStatusCode.Forbidden, (await PollAsync(host, membersOnly, "wait=0")).StatusCode);
    }

    [Fact]
    public async Task PollsShareTheKeysConnectionLimit_AndTheExtraOneIs429()
    {
        await using var host = await StartAsync(_db, maxConnections: 1);
        var (keyId, key, userId) = await KeyAsync(host, "ViewAuditLog");
        var connections = host.Services.GetRequiredService<EventConnections>();
        var slot = EventConnections.KeyFor(userId, keyId);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = PollAsync(host, key, $"subjects={Subject()}&wait=20", stop.Token);
        await WaitUntilAsync(() => connections.OpenFor(slot) == 1);

        var refused = await PollAsync(host, key, "wait=0");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), refused.Headers.RetryAfter?.Delta);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task AClientThatGoesAway_FreesItsPlaceAtOnce()
    {
        await using var host = await StartAsync(_db, maxConnections: 1);
        var (keyId, key, userId) = await KeyAsync(host, "ViewAuditLog");
        var connections = host.Services.GetRequiredService<EventConnections>();
        var slot = EventConnections.KeyFor(userId, keyId);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = PollAsync(host, key, $"subjects={Subject()}&wait=60", stop.Token);
        await WaitUntilAsync(() => connections.OpenFor(slot) == 1);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Well inside the sixty seconds it asked to wait, and without any fact being written.
        await WaitUntilAsync(() => connections.OpenFor(slot) == 0);
        Assert.Equal(HttpStatusCode.OK, (await PollAsync(host, key, "wait=0")).StatusCode);
    }

    [Fact]
    public async Task APoll_WaitsForALowerIdThatCommitsLate()
    {
        await using var host = await StartAsync(_db);
        var (_, key, _) = await KeyAsync(host, "ViewAuditLog");
        var before = await NewestAsync(host);
        var subject = Subject();
        var clock = host.Clock;

        await using var slow = _db.NewContext();
        await using var fast = _db.NewContext();

        await using var transaction = await slow.Database.BeginTransactionAsync(Ct);
        var lower = await new FactWriter(slow, clock).WriteAsync(Record(FactType.MemberBanned, subject, clock.UtcNow), Ct);
        var higher = await new FactWriter(fast, clock).WriteAsync(Record(FactType.MemberJoined, subject, clock.UtcNow), Ct);

        var early = await BodyAsync(await PollAsync(host, key, $"cursor={before}&subjects={subject}&wait=0"));
        Assert.Empty(Ids(early));
        Assert.Equal(Text(before), early.GetProperty("cursor").GetString());

        await transaction.CommitAsync(Ct);

        var both = await BodyAsync(await PollAsync(host, key, $"cursor={early.GetProperty("cursor").GetString()}&subjects={subject}&wait=0"));
        Assert.Equal([Text(lower.Id), Text(higher.Id)], Ids(both));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting.");
            await Task.Delay(25, Ct);
        }
    }
}
