using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Companion.Live;

/// <summary>
/// The companion's live updates: the WebSocket, and long polling as its backup (live updates
/// design §5). It replaces the flagged-join long poll, which stays for older clients.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A device token in the header, and only that.</strong> The companion sets headers on
/// its WebSocket, so there are no tickets here and nothing in the address but the instance and
/// the cursor. The same resolver every other companion endpoint uses answers who is calling, and
/// a device sees exactly what <see cref="LiveScope.ForDevice"/> says: presence in the instance it
/// named, nothing else.
/// </para>
/// <para>
/// <strong>Naming the instance is the same signal the roster read gives.</strong> A device that
/// opens the stream for an instance is standing in it, so <see cref="DeviceLocations"/> is told,
/// the way it is told by a roster read -- a by-product of a request the device makes anyway,
/// never a report made for its own sake.
/// </para>
/// </remarks>
public static class CompanionLiveEndpoints
{
    public static async Task<IResult> SocketAsync(
        int apiVersion,
        string? instanceId,
        string? after,
        HttpContext context,
        DeviceAuthenticator authenticator,
        DeviceLocations locations,
        EventSocketOptions options,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        if (!context.WebSockets.IsWebSocketRequest)
            return CompanionApiErrors.Malformed("Connect with a WebSocket.");

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var device = authentication.Device!;
        var tokenHash = DeviceTokens.Hash(DeviceAuthenticator.ReadBearer(context)!);

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = options.PingInterval,
            KeepAliveTimeout = options.PingTimeout,
        });

        if (!LiveStreams.TryParseCursor(after, out var cursor))
        {
            await LiveStreams.RefuseAsync(socket, EventCloseCodes.NoSubscribe, "The cursor is not valid");
            return Results.Empty;
        }

        if (instanceId is { Length: > 0 })
            locations.Record(device.Id, instanceId, clock.UtcNow);

        await LiveStreams.RunSocketAsync(
            context,
            socket,
            LiveScope.ForDevice(device.Id, instanceId),
            DeviceRefresh(tokenHash),
            cursor,
            $"live:device:{device.Id}",
            named => locations.Record(device.Id, named, clock.UtcNow));

        return Results.Empty;
    }

    public static async Task<IResult> PollAsync(
        int apiVersion,
        string? instanceId,
        string? after,
        int? wait,
        HttpContext context,
        DeviceAuthenticator authenticator,
        DeviceLocations locations,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var device = authentication.Device!;
        var tokenHash = DeviceTokens.Hash(DeviceAuthenticator.ReadBearer(context)!);

        if (instanceId is { Length: > 0 })
            locations.Record(device.Id, instanceId, clock.UtcNow);

        return await LiveStreams.PollAsync(
            context,
            database,
            LiveScope.ForDevice(device.Id, instanceId),
            DeviceRefresh(tokenHash),
            $"live:device:{device.Id}",
            after,
            wait,
            limit: null);
    }

    /// <summary>The same check the authenticator makes: a revoked device no longer resolves.</summary>
    private static LiveScopeRefresh DeviceRefresh(string tokenHash) => async (services, current, ct) =>
    {
        var devices = services.GetRequiredService<ICompanionDeviceStore>();
        var device = await devices.FindByTokenHashAsync(tokenHash, ct);

        return device is null ? null : current;
    };
}
