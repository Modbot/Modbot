using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Live;

/// <summary>
/// The web app's live updates (live updates design §4): the ticket, the WebSocket, what each
/// permission is sent, resuming from a cursor, and long polling as the backup.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LiveStreamTests
{
    private readonly PostgresFixture _db;

    public LiveStreamTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Instance = "39911";

    private static Task<ApiTestHost> StartAsync(PostgresFixture db)
        => ApiTestHost.StartAsync(db, configure: services => services.AddSingleton(new EventSocketOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
        }));

    private static async Task<string> TicketAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, LiveStreamEndpoints.TicketPath, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("ticket").GetString()!;
    }

    private static Task<WebSocket> ConnectAsync(ApiTestHost host, string? ticket, long? after = null, string? cookie = null)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookie is not null)
                request.Headers.Cookie = cookie;
        };

        var query = new List<string>();
        if (ticket is not null)
            query.Add($"ticket={Uri.EscapeDataString(ticket)}");
        if (after is { } cursor)
            query.Add($"after={cursor}");

        var address = "wss://localhost" + LiveStreamEndpoints.SocketPath + (query.Count == 0 ? "" : "?" + string.Join('&', query));
        return client.ConnectAsync(new Uri(address), Ct);
    }

    /// <summary>The next message, or the close code and reason when the server closed instead.</summary>
    private static async Task<(JsonElement Message, int? Closed, string? Reason)> NextAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                return (default, (int?)result.CloseStatus, result.CloseStatusDescription);

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return (JsonDocument.Parse(message.ToArray()).RootElement.Clone(), null, null);
        }
    }

    private static async Task<JsonElement> NextOfKindAsync(WebSocket socket, string kind)
    {
        while (true)
        {
            var (message, closed, reason) = await NextAsync(socket);
            Assert.True(closed is null, $"The server closed the socket with {closed}: {reason}");

            var got = message.GetProperty("kind").GetString();
            if (got == "heartbeat")
                continue;

            Assert.Equal(kind, got);
            return message;
        }
    }

    private static async Task<JsonElement> NextEventAsync(WebSocket socket)
        => (await NextOfKindAsync(socket, "event")).GetProperty("event");

    private static async Task<long> WriteAsync(ApiTestHost host, string type, string subject, string? instance = Instance, string? displayName = null, JsonObject? data = null)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(host.Clock.UtcNow, Ct);

        data ??= new JsonObject();
        if (displayName is not null)
            data["displayName"] = displayName;

        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = host.Clock.UtcNow,
            SubjectPlatform = type == FactType.InsightAlert ? FactPlatform.Modbot : FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = instance is null ? null : "wrld_4b34",
            InstanceId = instance,
            Source = FactSource.Companion,
            Data = data,
        }, Ct);

        return result.Id;
    }

    private static string Subject() => $"usr_{Guid.NewGuid():N}";

    [Fact]
    public async Task AModeratorWhoMaySeeLiveInstances_IsSentJoinsAndLeaves_WithThePersonDescribed()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        using var socket = await ConnectAsync(host, await TicketAsync(host, cookie));

        var hello = await NextOfKindAsync(socket, "hello");
        Assert.Equal(1, hello.GetProperty("version").GetInt32());
        Assert.True(long.Parse(hello.GetProperty("cursor").GetString()!, System.Globalization.CultureInfo.InvariantCulture) >= 0);

        // The world's name rides beside its id, so a client can say "The Black Cat #<number>".
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Modbot.Core.Data.ModbotContext>();
            db.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = "wrld_4b34",
                Name = "The Black Cat",
                FirstSeenAt = host.Clock.UtcNow,
                LastSeenAt = host.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var subject = Subject();
        var join = await WriteAsync(host, FactType.InstanceJoined, subject, displayName: "Rin");
        await WriteAsync(host, FactType.RoleUpdated, subject, instance: null);
        var leave = await WriteAsync(host, FactType.InstanceLeft, subject);

        var first = await NextEventAsync(socket);
        Assert.Equal(join.ToString(System.Globalization.CultureInfo.InvariantCulture), first.GetProperty("id").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), first.GetProperty("cursor").GetString());
        Assert.Equal(LiveKinds.PersonJoined, first.GetProperty("kind").GetString());
        Assert.Equal(Instance, first.GetProperty("instanceId").GetString());
        Assert.Equal("wrld_4b34", first.GetProperty("worldId").GetString());
        Assert.Equal("The Black Cat", first.GetProperty("worldName").GetString());
        Assert.True(first.TryGetProperty("at", out _));
        Assert.False(first.GetProperty("flagged").GetBoolean());

        var person = first.GetProperty("person");
        Assert.Equal(subject, person.GetProperty("id").GetString());
        Assert.Equal("Rin", person.GetProperty("displayName").GetString());
        Assert.Equal("Ordinary", person.GetProperty("standing").GetString());
        Assert.Equal(JsonValueKind.Null, person.GetProperty("trustRank").ValueKind);

        var second = await NextEventAsync(socket);
        Assert.Equal(leave.ToString(System.Globalization.CultureInfo.InvariantCulture), second.GetProperty("id").GetString());
        Assert.Equal(LiveKinds.PersonLeft, second.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AJoinBySomebodyWithPriorActions_ArrivesAsAFlaggedJoin()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var subject = Subject();
        await WriteAsync(host, FactType.MemberBanned, subject, instance: null);
        await WriteAsync(host, FactType.MemberKicked, subject, instance: null);

        using var socket = await ConnectAsync(host, await TicketAsync(host, cookie));
        await NextOfKindAsync(socket, "hello");

        await WriteAsync(host, FactType.InstanceJoined, subject, displayName: "Trouble");

        var @event = await NextEventAsync(socket);
        Assert.Equal(LiveKinds.FlaggedJoin, @event.GetProperty("kind").GetString());
        Assert.True(@event.GetProperty("flagged").GetBoolean());
        Assert.Equal("2 prior moderation actions", @event.GetProperty("reason").GetString());
        Assert.Equal("Flagged", @event.GetProperty("person").GetProperty("standing").GetString());
        Assert.Equal(2, @event.GetProperty("person").GetProperty("priorActions").GetInt32());
        Assert.False(@event.GetProperty("byThisDevice").GetBoolean());
    }

    [Fact]
    public async Task WhatArrivesFollowsThePermissionsHeld()
    {
        await using var host = await StartAsync(_db);

        // Analytics only: alerts, never presence.
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        using var socket = await ConnectAsync(host, await TicketAsync(host, cookie));
        await NextOfKindAsync(socket, "hello");

        await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Rin");
        await WriteAsync(host, FactType.InsightAlert, "vrchat-joins", instance: null, data: new JsonObject { ["label"] = "Joins" });

        var @event = await NextEventAsync(socket);
        Assert.Equal(LiveKinds.Alert, @event.GetProperty("kind").GetString());
        Assert.Equal("Joins", @event.GetProperty("data").GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, @event.GetProperty("person").ValueKind);
    }

    [Fact]
    public async Task AnyOtherFact_ArrivesAsAFact_WithItsTypeSubjectAndActor_ForWhoeverReadsThatLog()
    {
        await using var host = await StartAsync(_db);

        // The audit log's reader: every moderation fact, presence only with "See live instances".
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        using var socket = await ConnectAsync(host, await TicketAsync(host, cookie));
        await NextOfKindAsync(socket, "hello");

        var subject = Subject();
        await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Rin");
        var ban = await WriteAsync(host, FactType.MemberBanned, subject, instance: null, data: new JsonObject { ["actorDisplayName"] = "Alice" });

        var @event = await NextEventAsync(socket);
        Assert.Equal(ban.ToString(System.Globalization.CultureInfo.InvariantCulture), @event.GetProperty("id").GetString());
        Assert.Equal(LiveKinds.Fact, @event.GetProperty("kind").GetString());
        Assert.Equal(FactType.MemberBanned, @event.GetProperty("type").GetString());
        Assert.Equal("moderation", @event.GetProperty("category").GetString());
        // The name the source goes by on the wire, which is what a reader of this stream sees.
        Assert.Equal("Companion", @event.GetProperty("source").GetString());
        Assert.Equal(subject, @event.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal("VRChat", @event.GetProperty("subject").GetProperty("platform").GetString());
        Assert.Equal("Person", @event.GetProperty("subject").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, @event.GetProperty("actor").ValueKind);
        Assert.Equal("Alice", @event.GetProperty("data").GetProperty("actorDisplayName").GetString());
        Assert.True(@event.TryGetProperty("label", out _));
    }

    [Fact]
    public async Task AModeratorWhoMaySeeLiveInstancesButNotTheLog_IsSentPresenceAndNothingElse()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var start = await WriteAsync(host, FactType.InstanceJoined, Subject());
        await WriteAsync(host, FactType.MemberBanned, Subject(), instance: null);
        await WriteAsync(host, FactType.Login, "u_" + Guid.NewGuid().ToString("N"), instance: null);
        var join = await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Rin");

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, $"{LiveStreamEndpoints.PollPath}?after={start}&wait=0", cookie), Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(events);
        Assert.Equal(join.ToString(System.Globalization.CultureInfo.InvariantCulture), events[0].GetProperty("id").GetString());
        Assert.Equal(LiveKinds.PersonJoined, events[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task SomebodyWithNoneOfThePermissions_GetsNoTicket()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, LiveStreamEndpoints.TicketPath, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ASessionCookieAlone_DoesNotOpenTheSocket()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        using var socket = await ConnectAsync(host, ticket: null, cookie: cookie);

        var (_, closed, _) = await NextAsync(socket);

        Assert.Equal(EventCloseCodes.NotAuthenticated, closed);
    }

    [Fact]
    public async Task ReconnectingWithACursor_ReplaysWhatWasMissed_InOrder()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        // Different people each time: a client's report of the same person joining the same
        // instance within five seconds is deduplicated by the writer, which is right and not what
        // this test is about.
        var subject = Subject();
        var before = await WriteAsync(host, FactType.InstanceJoined, subject);
        var missedOne = await WriteAsync(host, FactType.InstanceLeft, subject);
        var missedTwo = await WriteAsync(host, FactType.InstanceJoined, Subject());

        using var socket = await ConnectAsync(host, await TicketAsync(host, cookie), after: before);
        var hello = await NextOfKindAsync(socket, "hello");
        Assert.Equal(before.ToString(System.Globalization.CultureInfo.InvariantCulture), hello.GetProperty("cursor").GetString());

        Assert.Equal(missedOne.ToString(System.Globalization.CultureInfo.InvariantCulture), (await NextEventAsync(socket)).GetProperty("id").GetString());
        Assert.Equal(missedTwo.ToString(System.Globalization.CultureInfo.InvariantCulture), (await NextEventAsync(socket)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task LongPolling_AnswersAtOnceWithEventsAfterTheCursor_AndTheSameShape()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var before = await WriteAsync(host, FactType.InstanceJoined, Subject());
        var join = await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Rin");

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, $"{LiveStreamEndpoints.PollPath}?after={before}&wait=5", cookie), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await ApiTestHost.BodyOf(response, Ct);
        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(events);
        Assert.Equal(join.ToString(System.Globalization.CultureInfo.InvariantCulture), events[0].GetProperty("id").GetString());
        Assert.Equal(LiveKinds.PersonJoined, events[0].GetProperty("kind").GetString());
        Assert.Equal("Rin", events[0].GetProperty("person").GetProperty("displayName").GetString());
        Assert.Equal(join.ToString(System.Globalization.CultureInfo.InvariantCulture), body.GetProperty("cursor").GetString());
        Assert.False(body.GetProperty("more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("notice").ValueKind);
    }

    [Fact]
    public async Task LongPolling_WaitsAndIsWokenByAWrittenFact()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var subject = Subject();
        var start = await WriteAsync(host, FactType.InstanceJoined, subject);

        var poll = host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, $"{LiveStreamEndpoints.PollPath}?after={start}&wait=20", cookie), Ct);

        await Task.Delay(300, Ct);
        Assert.False(poll.IsCompleted);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var leave = await WriteAsync(host, FactType.InstanceLeft, subject);

        var body = await ApiTestHost.BodyOf(await poll, Ct);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}");
        Assert.Equal(leave.ToString(System.Globalization.CultureInfo.InvariantCulture), body.GetProperty("events")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task LongPolling_AnswersEmptyWhenTheWaitRunsOut_AndRefusesNobody()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, $"{LiveStreamEndpoints.PollPath}?wait=0", cookie), Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("events").EnumerateArray());

        var anonymous = await host.Client.GetAsync($"{LiveStreamEndpoints.PollPath}?wait=0", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task LongPolling_SendsOnlyWhatThePersonMaySee()
    {
        await using var host = await StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ReviewTickets, Ct);

        var start = await WriteAsync(host, FactType.InstanceJoined, Subject());
        await WriteAsync(host, FactType.InstanceJoined, Subject());
        var review = await WriteAsync(host, FactType.ReviewOpened, "usr_mod", instance: null);

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, $"{LiveStreamEndpoints.PollPath}?after={start}&wait=0", cookie), Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(events);
        Assert.Equal(LiveKinds.ReviewOpened, events[0].GetProperty("kind").GetString());
        Assert.Equal(review.ToString(System.Globalization.CultureInfo.InvariantCulture), body.GetProperty("cursor").GetString());
    }
}
