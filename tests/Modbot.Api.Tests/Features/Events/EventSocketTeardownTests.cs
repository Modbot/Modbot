using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Events;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Events;

/// <summary>
/// The event socket's teardown, which is the same shape as the live socket's: a client that
/// vanishes while the server is waiting to hear from it, and a second connection opening while
/// the first is still closing.
/// </summary>
/// <remarks>
/// Written alongside <c>LiveSocketTeardownTests</c> because the two sessions are the same code
/// twice, so a fault in one is a fault in the other. See
/// <c>.agent/research/2026-09-18-live-socket-crash.md</c>.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class EventSocketTeardownTests
{
    private readonly PostgresFixture _db;

    public EventSocketTeardownTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ApiTestHost> StartAsync(PostgresFixture db)
        => ApiTestHost.StartAsync(db, configure: services => services.AddSingleton(new EventSocketOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
        }));

    /// <summary>
    /// Takes a ticket and opens the socket with it. The caller needs a permission that reaches one
    /// of the two logs -- ViewLiveInstances only widens what a reader of a log may additionally be
    /// sent, so on its own it sees no event at all and the ticket is refused.
    /// </summary>
    private static async Task<WebSocket> ConnectAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/events/tickets", null, cookie, Ct);
        response.EnsureSuccessStatusCode();
        var ticket = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("ticket").GetString()!;

        var client = host.Server.CreateWebSocketClient();
        var address = $"wss://localhost{EventEndpoints.SocketPath}?ticket={Uri.EscapeDataString(ticket)}";

        return await client.ConnectAsync(new Uri(address), Ct);
    }

    /// <summary>Reads until the hello arrives, which means the server is now waiting to receive.</summary>
    private static async Task HelloAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            var kind = JsonDocument.Parse(message.ToArray()).RootElement.GetProperty("kind").GetString();
            if (kind == "hello")
                return;

            message.SetLength(0);
        }
    }

    private static async Task WaitForOpenAsync(ApiTestHost host, string key, int expected)
    {
        var connections = host.Services.GetRequiredService<EventConnections>();
        var clock = Stopwatch.StartNew();

        while (connections.OpenFor(key) != expected)
        {
            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(15),
                $"{key} was still held by {connections.OpenFor(key)} connections, not {expected}");

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// A client that stops without a close frame, while the server is inside a receive that has
    /// not come back. The connection ends and the caller gets its place back.
    /// </summary>
    [Fact]
    public async Task AConnectionDroppedWhileTheServerIsWaitingToReceive_GivesItsPlaceBack()
    {
        await using var host = await StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var key = EventConnections.KeyFor(user.Id, null);

        var socket = await ConnectAsync(host, cookie);
        await HelloAsync(socket);

        Assert.Equal(1, host.Services.GetRequiredService<EventConnections>().OpenFor(key));

        socket.Abort();
        socket.Dispose();

        await WaitForOpenAsync(host, key, 0);

        using var again = await ConnectAsync(host, cookie);
        await HelloAsync(again);
        await WaitForOpenAsync(host, key, 1);
    }

    /// <summary>
    /// A second connection opened before the first has finished closing is served, and both
    /// places come back afterwards.
    /// </summary>
    [Fact]
    public async Task ASecondConnectionOpenedWhileTheFirstCloses_IsServed_AndBothPlacesComeBack()
    {
        await using var host = await StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var key = EventConnections.KeyFor(user.Id, null);

        var first = await ConnectAsync(host, cookie);
        await HelloAsync(first);

        first.Abort();
        first.Dispose();

        var second = await ConnectAsync(host, cookie);
        await HelloAsync(second);

        await WaitForOpenAsync(host, key, 1);

        second.Abort();
        second.Dispose();

        await WaitForOpenAsync(host, key, 0);
    }
}
