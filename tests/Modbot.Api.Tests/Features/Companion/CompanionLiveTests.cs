using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The companion's live updates (live updates design §5): a device token opens the socket, sees
/// presence in the instance it named and nothing else, is told which joins it reported itself,
/// and can carry on by long polling with the same cursor.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CompanionLiveTests
{
    private readonly PostgresFixture _db;

    public CompanionLiveTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private async Task<(CompanionApiTestHost Host, string Token)> ReadyAsync()
    {
        await CompanionApiTestHost.ResetAsync(_db, Ct);
        var host = await CompanionApiTestHost.StartAsync(_db);
        await host.ConfigureGroupAsync(_db, Group, Ct);

        // The reset empties the log while the id sequence carries on, so "from now" would be 0
        // and the first fact written would look like a gap the feed waits ten seconds on (API
        // keys design §4.2) -- with the fixed test clock, forever. One fact first, as any
        // deployment has.
        await WriteAsync(host, FactType.InstanceLeft, Subject(), instance: "0");

        return (host, await host.PairDeviceAsync(Ct));
    }

    private static Task<WebSocket> ConnectAsync(CompanionApiTestHost host, string? token, string? instance = Instance, long? after = null)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (token is not null)
                request.Headers.Authorization = $"Bearer {token}";
        };

        var query = new List<string>();
        if (instance is not null)
            query.Add($"instanceId={Uri.EscapeDataString(instance)}");
        if (after is { } cursor)
            query.Add($"after={cursor}");

        return client.ConnectAsync(new Uri("wss://localhost/api/v1/companion/ws?" + string.Join('&', query)), Ct);
    }

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

    private static async Task<long> WriteAsync(
        CompanionApiTestHost host, string type, string subject, string instance = Instance, string? displayName = null, Guid? reporter = null)
    {
        var data = new JsonObject();
        if (displayName is not null)
            data["displayName"] = displayName;
        if (reporter is { } device)
            data["deviceId"] = device.ToString();

        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = host.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = "wrld_4b34",
            InstanceId = instance,
            Source = FactSource.Client,
            Data = data,
        }, Ct);

        return result.Id;
    }

    private static string Subject() => $"usr_{Guid.NewGuid():N}";

    private static async Task<Guid> DeviceIdAsync(CompanionApiTestHost host)
        => (await host.Devices.ListDevicesAsync(Ct)).Single().Id;

    [Fact]
    public async Task ADevice_IsSentPresenceInItsInstanceOnly_AndToldWhichJoinsItReported()
    {
        var (host, token) = await ReadyAsync();
        await using var keep = host;
        var deviceId = await DeviceIdAsync(host);

        using var socket = await ConnectAsync(host, token);
        var hello = await NextOfKindAsync(socket, "hello");
        Assert.True(hello.TryGetProperty("cursor", out _));

        // Opening the stream for an instance is the device saying where it is standing.
        Assert.True(host.Services.GetRequiredService<DeviceLocations>().IsIn(deviceId, Instance, host.Clock.UtcNow));

        var elsewhere = await WriteAsync(host, FactType.InstanceJoined, Subject(), instance: "11111", displayName: "Somebody Else");
        var own = await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Rin", reporter: deviceId);
        var theirs = await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Mei", reporter: Guid.NewGuid());

        var first = await NextEventAsync(socket);
        Assert.Equal(own.ToString(System.Globalization.CultureInfo.InvariantCulture), first.GetProperty("id").GetString());
        Assert.Equal(LiveKinds.PersonJoined, first.GetProperty("kind").GetString());
        Assert.True(first.GetProperty("byThisDevice").GetBoolean());
        Assert.Equal("Rin", first.GetProperty("person").GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("data").ValueKind);

        var second = await NextEventAsync(socket);
        Assert.Equal(theirs.ToString(System.Globalization.CultureInfo.InvariantCulture), second.GetProperty("id").GetString());
        Assert.False(second.GetProperty("byThisDevice").GetBoolean());

        Assert.NotEqual(elsewhere, own);
    }

    [Fact]
    public async Task AFlaggedJoin_ArrivesAsOne_WithItsReason()
    {
        var (host, token) = await ReadyAsync();
        await using var keep = host;

        var subject = Subject();
        await WriteAsync(host, FactType.MemberKicked, subject, instance: "0");

        using var socket = await ConnectAsync(host, token);
        await NextOfKindAsync(socket, "hello");

        await WriteAsync(host, FactType.InstanceJoined, subject, displayName: "Trouble");

        var @event = await NextEventAsync(socket);
        Assert.Equal(LiveKinds.FlaggedJoin, @event.GetProperty("kind").GetString());
        Assert.Equal("1 prior moderation action", @event.GetProperty("reason").GetString());
        Assert.Equal("Flagged", @event.GetProperty("person").GetProperty("standing").GetString());
    }

    [Fact]
    public async Task WalkingIntoAnotherInstance_IsASubscribe_NotAReconnect()
    {
        var (host, token) = await ReadyAsync();
        await using var keep = host;
        var deviceId = await DeviceIdAsync(host);

        using var socket = await ConnectAsync(host, token);
        await NextOfKindAsync(socket, "hello");

        await socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", instanceId = "85019" }),
            WebSocketMessageType.Text, true, Ct);

        // Give the change a moment to apply before the facts it should decide about are written.
        await Task.Delay(200, Ct);

        await WriteAsync(host, FactType.InstanceJoined, Subject(), instance: Instance, displayName: "Old room");
        var moved = await WriteAsync(host, FactType.InstanceJoined, Subject(), instance: "85019", displayName: "New room");

        var @event = await NextEventAsync(socket);
        Assert.Equal(moved.ToString(System.Globalization.CultureInfo.InvariantCulture), @event.GetProperty("id").GetString());
        Assert.True(host.Services.GetRequiredService<DeviceLocations>().IsIn(deviceId, "85019", host.Clock.UtcNow));
    }

    [Fact]
    public async Task LongPolling_CarriesOnFromACursor_WithTheSameEvents()
    {
        var (host, token) = await ReadyAsync();
        await using var keep = host;

        // Different people: the writer deduplicates one person joining one room twice within
        // five seconds, which is right and not what this test is about.
        var before = await WriteAsync(host, FactType.InstanceJoined, Subject());
        var join = await WriteAsync(host, FactType.InstanceJoined, Subject(), displayName: "Rin");
        await WriteAsync(host, FactType.InstanceJoined, Subject(), instance: "11111");

        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v1/companion/poll?instanceId={Instance}&after={before}&wait=5", token), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);

        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(events);
        Assert.Equal(join.ToString(System.Globalization.CultureInfo.InvariantCulture), events[0].GetProperty("id").GetString());
        Assert.Equal("Rin", events[0].GetProperty("person").GetProperty("displayName").GetString());

        // The cursor moved past the join in the other instance, which was read and not sent.
        Assert.True(long.Parse(body.GetProperty("cursor").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > join);
    }

    [Fact]
    public async Task LongPolling_WithoutAValidToken_IsRefused()
    {
        var (host, _) = await ReadyAsync();
        await using var keep = host;

        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v1/companion/poll?instanceId={Instance}&wait=0", "not-a-token"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("device_token_invalid", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheSocket_WithoutAValidToken_IsRefusedAtTheHandshake()
    {
        var (host, _) = await ReadyAsync();
        await using var keep = host;

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host, "not-a-token"));
    }

    [Fact]
    public async Task AnUnsupportedApiVersion_IsToldToRenegotiate()
    {
        var (host, token) = await ReadyAsync();
        await using var keep = host;

        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v99/companion/poll?instanceId={Instance}&wait=0", token), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TheOldAlertLongPoll_StillAnswers()
    {
        // Kept for clients built before the stream: the protocol version is unchanged.
        var (host, token) = await ReadyAsync();
        await using var keep = host;

        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, "/api/v1/companion/alerts?wait=1", token), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
