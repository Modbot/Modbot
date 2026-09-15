using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Events;

/// <summary>
/// The live event WebSocket (API keys design §5), over the real pipeline: how it authenticates,
/// what a subscription chooses, what a caller may see, and resuming from a cursor.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventSocketTests
{
    private readonly PostgresFixture _db;

    public EventSocketTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ApiTestHost> StartAsync(PostgresFixture db, EventSocketOptions? options = null)
        => ApiTestHost.StartAsync(db, configure: services => services.AddSingleton(options ?? new EventSocketOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
        }));

    private static async Task<string> KeyAsync(ApiTestHost host, ModbotPermissions permissions)
    {
        var (_, cookie) = await host.SignedInAsync(permissions | ModbotPermissions.ManageApiKeys, Ct);
        var names = Enum.GetValues<ModbotPermissions>()
            .Where(p => p != ModbotPermissions.None && permissions.HasFlag(p))
            .Select(p => p.ToString())
            .ToArray();

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/api-keys", new { name = "socket", permissions = names }, cookie, Ct);
        response.EnsureSuccessStatusCode();
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("key").GetString()!;
    }

    private static Task<WebSocket> ConnectAsync(ApiTestHost host, string? key = null, string? ticket = null, string? cookie = null)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (key is not null)
                request.Headers.Authorization = $"Bearer {key}";
            if (cookie is not null)
                request.Headers.Cookie = cookie;
        };

        var address = "wss://localhost" + EventEndpoints.SocketPath + (ticket is null ? "" : $"?ticket={Uri.EscapeDataString(ticket)}");
        return client.ConnectAsync(new Uri(address), Ct);
    }

    private static Task SendAsync(WebSocket socket, object message)
        => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, Ct);

    /// <summary>The next message, or the close code when the server closed instead.</summary>
    private static async Task<(JsonElement Message, int? Closed)> NextAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                return (default, (int?)result.CloseStatus);

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return (JsonDocument.Parse(message.ToArray()).RootElement.Clone(), null);
        }
    }

    /// <summary>The next message that is not a heartbeat.</summary>
    private static async Task<JsonElement> NextOfKindAsync(WebSocket socket, string kind)
    {
        while (true)
        {
            var (message, closed) = await NextAsync(socket);
            Assert.Null(closed);

            var got = message.GetProperty("kind").GetString();
            if (got == "heartbeat")
                continue;

            Assert.Equal(kind, got);
            return message;
        }
    }

    private static async Task<int?> ClosedWithAsync(WebSocket socket)
    {
        while (true)
        {
            var (_, closed) = await NextAsync(socket);
            if (closed is not null)
                return closed;
        }
    }

    private static async Task<string> SubscribeAsync(WebSocket socket, object subscribe)
    {
        await NextOfKindAsync(socket, "hello");
        await SendAsync(socket, subscribe);
        return (await NextOfKindAsync(socket, "subscribed")).GetProperty("cursor").GetString()!;
    }

    private static async Task<long> WriteFactAsync(ApiTestHost host, string type, string subject, string? actorName = null)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(host.Clock.UtcNow, Ct);

        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = host.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            ActorPlatform = actorName is null ? null : FactPlatform.VRChat,
            ActorId = actorName is null ? null : "usr_actor",
            Source = FactSource.AuditLog,
            Data = actorName is null ? new JsonObject() : new JsonObject { ["actorDisplayName"] = actorName },
        }, Ct);

        return result.Id;
    }

    private static string Subject() => $"usr_{Guid.NewGuid():N}";

    [Fact]
    public async Task AKeyInTheHeader_ReceivesNewFactsOfTheChosenTypes_InOrder()
    {
        await using var host = await StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);
        using var socket = await ConnectAsync(host, key);

        var cursor = await SubscribeAsync(socket, new { op = "subscribe", types = new[] { "vrchat.group.member.*" } });
        Assert.True(long.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) >= 0);

        var subject = Subject();
        var ban = await WriteFactAsync(host, FactType.MemberBanned, subject, actorName: "Alice");
        await WriteFactAsync(host, FactType.RoleUpdated, subject);
        var join = await WriteFactAsync(host, FactType.MemberJoined, subject);

        var first = (await NextOfKindAsync(socket, "event")).GetProperty("event");
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal(ban.ToString(System.Globalization.CultureInfo.InvariantCulture), first.GetProperty("id").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), first.GetProperty("cursor").GetString());
        Assert.Equal(FactType.MemberBanned, first.GetProperty("type").GetString());
        Assert.Equal("moderation", first.GetProperty("category").GetString());
        Assert.Equal("AuditLog", first.GetProperty("source").GetString());
        Assert.Equal("VRChat", first.GetProperty("subject").GetProperty("platform").GetString());
        Assert.Equal(subject, first.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal("Alice", first.GetProperty("actor").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("occurred_before").ValueKind);
        Assert.True(first.TryGetProperty("occurred_at", out _));
        Assert.Equal("Alice", first.GetProperty("data").GetProperty("actorDisplayName").GetString());

        var second = (await NextOfKindAsync(socket, "event")).GetProperty("event");
        Assert.Equal(join.ToString(System.Globalization.CultureInfo.InvariantCulture), second.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ASubjectFilter_NarrowsToThoseSubjects()
    {
        await using var host = await StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);
        using var socket = await ConnectAsync(host, key);

        var wanted = Subject();
        await SubscribeAsync(socket, new { op = "subscribe", types = new[] { "*" }, subjects = new[] { wanted } });

        await WriteFactAsync(host, FactType.MemberBanned, Subject());
        await WriteFactAsync(host, FactType.MemberKicked, wanted);

        var next = (await NextOfKindAsync(socket, "event")).GetProperty("event");
        Assert.Equal(wanted, next.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal(FactType.MemberKicked, next.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ACaller_IsSentOnlyWhatTheirPermissionsReach()
    {
        await using var host = await StartAsync(_db);
        var auditOnly = await KeyAsync(host, ModbotPermissions.ViewAuditLog);
        var withLive = await KeyAsync(host, ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewLiveRooms);

        using var plain = await ConnectAsync(host, auditOnly);
        using var live = await ConnectAsync(host, withLive);
        await SubscribeAsync(plain, new { op = "subscribe" });
        await SubscribeAsync(live, new { op = "subscribe" });

        var subject = Subject();
        await WriteFactAsync(host, FactType.InstanceJoined, subject);
        await WriteFactAsync(host, FactType.SettingsChanged, subject);
        await WriteFactAsync(host, FactType.MemberBanned, subject);

        // Presence needs ViewLiveRooms on a live feed; operational facts need ViewOperationalLog.
        Assert.Equal(FactType.MemberBanned, (await NextOfKindAsync(plain, "event")).GetProperty("event").GetProperty("type").GetString());

        Assert.Equal(FactType.InstanceJoined, (await NextOfKindAsync(live, "event")).GetProperty("event").GetProperty("type").GetString());
        Assert.Equal(FactType.MemberBanned, (await NextOfKindAsync(live, "event")).GetProperty("event").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Resuming_WithACursor_SendsWhatWasMissed_AndNothingBefore()
    {
        await using var host = await StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);
        var subject = Subject();

        string seen;
        using (var socket = await ConnectAsync(host, key))
        {
            await SubscribeAsync(socket, new { op = "subscribe", subjects = new[] { subject } });
            await WriteFactAsync(host, FactType.MemberJoined, subject);
            seen = (await NextOfKindAsync(socket, "event")).GetProperty("event").GetProperty("cursor").GetString()!;
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
        }

        // Written while nobody was connected.
        var missed = await WriteFactAsync(host, FactType.MemberBanned, subject);

        using var again = await ConnectAsync(host, key);
        var cursor = await SubscribeAsync(again, new { op = "subscribe", subjects = new[] { subject }, cursor = seen });
        Assert.Equal(seen, cursor);

        var next = (await NextOfKindAsync(again, "event")).GetProperty("event");
        Assert.Equal(missed.ToString(System.Globalization.CultureInfo.InvariantCulture), next.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ATicket_OpensTheSocketFromASession_Once()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/events/tickets", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("ticket").GetString()!;

        using (var socket = await ConnectAsync(host, ticket: ticket))
        {
            var hello = await NextOfKindAsync(socket, "hello");
            Assert.Contains("ViewAuditLog", hello.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
        }

        using var reused = await ConnectAsync(host, ticket: ticket);
        Assert.Equal(EventCloseCodes.NotAuthenticated, await ClosedWithAsync(reused));
    }

    [Fact]
    public async Task AnExpiredTicket_IsRefused()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/events/tickets", null, cookie, Ct);
        var ticket = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("ticket").GetString()!;

        host.Clock.Advance(TimeSpan.FromSeconds(61));

        using var socket = await ConnectAsync(host, ticket: ticket);
        Assert.Equal(EventCloseCodes.NotAuthenticated, await ClosedWithAsync(socket));
    }

    [Fact]
    public async Task ASessionCookieAlone_OrNothing_DoesNotOpenTheSocket()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        using (var withCookie = await ConnectAsync(host, cookie: cookie))
            Assert.Equal(EventCloseCodes.NotAuthenticated, await ClosedWithAsync(withCookie));

        using (var anonymous = await ConnectAsync(host))
            Assert.Equal(EventCloseCodes.NotAuthenticated, await ClosedWithAsync(anonymous));

        using var badKey = await ConnectAsync(host, key: Modbot.Api.Auth.ApiKeySecrets.NewKey());
        Assert.Equal(EventCloseCodes.NotAuthenticated, await ClosedWithAsync(badKey));
    }

    [Fact]
    public async Task ACallerWhoCanSeeNoEvents_IsRefusedATicketAndTheSocket()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, "/api/events/tickets", null, cookie, Ct)).StatusCode);

        var key = await KeyAsync(host, ModbotPermissions.ViewMembers);
        using var socket = await ConnectAsync(host, key);
        Assert.Equal(EventCloseCodes.NoAccess, await ClosedWithAsync(socket));
    }

    [Fact]
    public async Task OneKeyTooManyConnections_IsRefused()
    {
        await using var host = await StartAsync(_db, new EventSocketOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            MaxConnectionsPerCaller = 1,
        });
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);

        using var first = await ConnectAsync(host, key);
        await NextOfKindAsync(first, "hello");

        using var second = await ConnectAsync(host, key);
        Assert.Equal(EventCloseCodes.TooManyConnections, await ClosedWithAsync(second));
    }

    [Fact]
    public async Task NoSubscribeInTime_ClosesTheConnection()
    {
        await using var host = await StartAsync(_db, new EventSocketOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            SubscribeTimeout = TimeSpan.FromMilliseconds(200),
        });
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);

        using var socket = await ConnectAsync(host, key);
        Assert.Equal(EventCloseCodes.NoSubscribe, await ClosedWithAsync(socket));
    }

    [Fact]
    public async Task RevokingTheKey_EndsAnOpenConnection_AtTheNextHeartbeat()
    {
        await using var host = await StartAsync(_db, new EventSocketOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        });
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ManageApiKeys, Ct);
        var made = await ApiTestHost.BodyOf(await host.SendJsonAsync(
            HttpMethod.Post, "/api/api-keys", new { name = "socket", permissions = new[] { "ViewAuditLog" } }, cookie, Ct), Ct);
        var key = made.GetProperty("key").GetString()!;
        var id = made.GetProperty("apiKey").GetProperty("id").GetGuid();

        using var socket = await ConnectAsync(host, key);
        await SubscribeAsync(socket, new { op = "subscribe" });
        Assert.Equal("heartbeat", (await NextAsync(socket)).Message.GetProperty("kind").GetString());

        await host.SendJsonAsync(HttpMethod.Delete, $"/api/api-keys/{id}", null, cookie, Ct);

        Assert.Equal(EventCloseCodes.NoAccess, await ClosedWithAsync(socket));
    }

    [Fact]
    public async Task ABadSubscribe_GetsAnError_AndTheConnectionStaysOpen()
    {
        await using var host = await StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);
        using var socket = await ConnectAsync(host, key);
        await NextOfKindAsync(socket, "hello");

        await SendAsync(socket, new { op = "subscribe", types = new[] { "Banned" } });
        Assert.Contains("not an event type", (await NextOfKindAsync(socket, "error")).GetProperty("message").GetString(), StringComparison.Ordinal);

        await socket.SendAsync(Encoding.UTF8.GetBytes("not json"), WebSocketMessageType.Text, true, Ct);
        await NextOfKindAsync(socket, "error");

        await SendAsync(socket, new { op = "ping" });
        await NextOfKindAsync(socket, "pong");
    }

    [Fact]
    public async Task TheTypeList_IsWhatTheCallerMayBeSent()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var types = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/events/types", null, cookie, Ct), Ct))
            .EnumerateArray().Select(t => t.GetProperty("type").GetString()).ToList();

        Assert.Contains(FactType.MemberBanned, types);
        Assert.DoesNotContain(FactType.InstanceJoined, types);
        Assert.DoesNotContain(FactType.SettingsChanged, types);
    }

    [Fact]
    public async Task ThePlainAddress_WithoutAnUpgrade_Is400()
    {
        await using var host = await StartAsync(_db);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(EventEndpoints.SocketPath, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheFeed_WaitsForALowerIdThatCommitsLate()
    {
        await using var host = await StartAsync(_db);
        var subject = Subject();

        await using var slow = _db.NewContext();
        await using var fast = _db.NewContext();
        var clock = host.Clock;

        long before;
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(clock.UtcNow, Ct);
            before = await new FactFeed(scope.ServiceProvider.GetRequiredService<ModbotContext>()).NewestIdAsync(Ct);
        }

        // The slow transaction takes the lower id and has not committed when the higher one has.
        await using var transaction = await slow.Database.BeginTransactionAsync(Ct);
        var lower = await new FactWriter(slow, clock).WriteAsync(Record(subject, clock.UtcNow), Ct);
        var higher = await new FactWriter(fast, clock).WriteAsync(Record(subject, clock.UtcNow), Ct);
        Assert.True(higher.Id > lower.Id);

        await using var reader = _db.NewContext();
        var feed = new FactFeed(reader);

        var waiting = await feed.ReadAsync(before, 50, clock.UtcNow, Ct);
        Assert.DoesNotContain(waiting.Facts, f => f.Id == higher.Id);
        Assert.Equal(before, waiting.Through);

        await transaction.CommitAsync(Ct);

        var both = await feed.ReadAsync(before, 50, clock.UtcNow, Ct);
        Assert.Equal([lower.Id, higher.Id], both.Facts.Where(f => f.SubjectId == subject).Select(f => f.Id));
        Assert.Equal(higher.Id, both.Through);
    }

    [Fact]
    public async Task TheFeed_StepsOverAGapOnceItIsOld()
    {
        await using var host = await StartAsync(_db);
        var clock = host.Clock;
        var subject = Subject();

        await using var db = _db.NewContext();
        await new EventPartitionMaintainer(db, clock).EnsureForAsync(clock.UtcNow, Ct);
        var feed = new FactFeed(db);
        var before = await feed.NewestIdAsync(Ct);

        // A rollback leaves exactly this: a sequence value taken and never used.
        await db.Database.ExecuteSqlRawAsync("SELECT nextval(pg_get_serial_sequence('modbot_event', 'id'))", Ct);
        var after = await new FactWriter(db, clock).WriteAsync(Record(subject, clock.UtcNow), Ct);

        Assert.Empty((await feed.ReadAsync(before, 50, clock.UtcNow, Ct)).Facts);

        var later = clock.UtcNow + FactFeed.DefaultGapWait + TimeSpan.FromSeconds(1);
        var page = await feed.ReadAsync(before, 50, later, Ct);
        Assert.Equal(after.Id, Assert.Single(page.Facts).Id);
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("vrchat.group.member.ban", true)]
    [InlineData("vrchat.*", true)]
    [InlineData("vrchat.group.*", true)]
    [InlineData("Banned", false)]
    [InlineData("vrchat..ban", false)]
    [InlineData(".*", false)]
    [InlineData("vrchat*", false)]
    public void TypePatterns(string pattern, bool valid)
        => Assert.Equal(valid, EventFilter.IsTypePattern(pattern));

    [Fact]
    public void AFilter_MatchesExactTypesPrefixesAndSubjects()
    {
        Assert.True(EventFilter.TryCreate(["vrchat.group.member.*", "modbot.user.login"], ["usr_a"], out var filter, out _));

        Assert.True(filter.Matches("vrchat.group.member.ban", "usr_a"));
        Assert.True(filter.Matches("modbot.user.login", "usr_a"));
        Assert.False(filter.Matches("vrchat.group.member.ban", "usr_b"));
        Assert.False(filter.Matches("vrchat.group.role.update", "usr_a"));
        Assert.False(filter.Matches("modbot.user.login.failed", "usr_a"));
    }

    private static FactRecord Record(string subject, DateTimeOffset at) => new()
    {
        Type = FactType.MemberJoined,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        Source = FactSource.AuditLog,
        Data = new JsonObject(),
    };
}
