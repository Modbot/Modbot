using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Live;

/// <summary>
/// How a live connection ends, rather than what it carries: a client that vanishes while the
/// server is waiting to hear from it, and a second connection opening while the first is still
/// on its way out.
/// </summary>
/// <remarks>
/// These are the two shapes a browser produces by turning pages quickly -- each page draw drops
/// the socket and opens another -- and both were suspected of the crash investigated in
/// <c>.agent/research/2026-09-18-live-socket-crash.md</c>. Neither leaves anything behind, and
/// these say so, so that the next person reading that note does not have to work it out again.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class LiveSocketTeardownTests
{
    private readonly PostgresFixture _db;

    public LiveSocketTeardownTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static async Task<WebSocket> ConnectAsync(ApiTestHost host, string cookie)
    {
        var ticket = await TicketAsync(host, cookie);
        var client = host.Server.CreateWebSocketClient();
        var address = $"wss://localhost{LiveStreamEndpoints.SocketPath}?ticket={Uri.EscapeDataString(ticket)}";

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

    /// <summary>The caller's key, as <see cref="LiveStreamEndpoints"/> builds it.</summary>
    private static string KeyFor(ModbotUser user) => "live:" + EventConnections.KeyFor(user.Id, null);

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
    /// The case the crash note is about: the browser drops the socket without a close frame while
    /// the server is inside a receive that has not come back. The request must not finish -- and
    /// so the WebSocket must not be disposed -- with that receive still outstanding, and the
    /// caller must get its place back.
    /// </summary>
    [Fact]
    public async Task AConnectionDroppedWhileTheServerIsWaitingToReceive_GivesItsPlaceBack()
    {
        await using var host = await StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var key = KeyFor(user);

        var socket = await ConnectAsync(host, cookie);
        await HelloAsync(socket);

        Assert.Equal(1, host.Services.GetRequiredService<EventConnections>().OpenFor(key));

        // No close frame, no warning: the connection simply stops, which is what a page being
        // thrown away in a browser looks like from here.
        socket.Abort();
        socket.Dispose();

        await WaitForOpenAsync(host, key, 0);

        // And the next one is served, rather than being refused as a connection that is still open.
        using var again = await ConnectAsync(host, cookie);
        await HelloAsync(again);
        await WaitForOpenAsync(host, key, 1);
    }

    /// <summary>
    /// Turning pages fast opens the next connection before the last has finished closing. The two
    /// share nothing but the count of the caller's open connections, and the second is served.
    /// </summary>
    [Fact]
    public async Task ASecondConnectionOpenedWhileTheFirstCloses_IsServed_AndBothPlacesComeBack()
    {
        await using var host = await StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var key = KeyFor(user);

        var first = await ConnectAsync(host, cookie);
        await HelloAsync(first);

        // Dropped and immediately replaced, with no wait in between: the first connection's
        // teardown is still running while the second is being accepted.
        first.Abort();
        first.Dispose();

        var second = await ConnectAsync(host, cookie);
        await HelloAsync(second);

        // The second connection works whatever the first is doing.
        await WaitForOpenAsync(host, key, 1);

        second.Abort();
        second.Dispose();

        await WaitForOpenAsync(host, key, 0);
    }

    /// <summary>
    /// The cap counts what is open, not what has ever been opened. Five drops and five reconnects
    /// is what the Members page produced while the server was dying, and it must not use the
    /// caller's five places up.
    /// </summary>
    [Fact]
    public async Task ManyQuickReconnects_DoNotUseUpTheCallersPlaces()
    {
        await using var host = await StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var key = KeyFor(user);

        for (var turn = 0; turn < 8; turn++)
        {
            var socket = await ConnectAsync(host, cookie);
            await HelloAsync(socket);
            socket.Abort();
            socket.Dispose();

            await WaitForOpenAsync(host, key, 0);
        }

        using var last = await ConnectAsync(host, cookie);
        await HelloAsync(last);
    }
}
