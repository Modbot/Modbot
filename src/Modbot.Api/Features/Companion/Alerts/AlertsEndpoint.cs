using Microsoft.AspNetCore.Http;
using Modbot.Api.Features.Companion.Devices;

namespace Modbot.Api.Features.Companion.Alerts;

/// <summary>The long poll's request handling: bounds on the wait, and what an empty one returns.</summary>
/// <remarks>
/// The client asks for a wait and shortens it on its own when something in the path keeps hanging
/// up on idle connections. The server bounds what it will honour so that a client asking for an
/// hour cannot pin a request thread, and so that a broken client asking for zero does not turn the
/// channel into a busy loop.
/// </remarks>
public static class AlertsEndpoint
{
    public const int DefaultWaitSeconds = 30;

    public const int MinimumWaitSeconds = 1;

    /// <summary>
    /// Above this, the connection is more likely to be killed by something in the middle than to
    /// deliver anything — which is the condition the client's adaptive wait is already measuring
    /// from its own side.
    /// </summary>
    public const int MaximumWaitSeconds = 60;

    public static async Task<IResult> WaitAsync(
        int apiVersion,
        int? wait,
        HttpContext context,
        DeviceAuthenticator authenticator,
        AlertHub alerts,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var seconds = Math.Clamp(wait ?? DefaultWaitSeconds, MinimumWaitSeconds, MaximumWaitSeconds);
        var alert = await alerts.WaitAsync(authentication.Device!.Id, TimeSpan.FromSeconds(seconds), ct);

        // 204 rather than an empty 200: the ordinary "nothing happened" has to be distinguishable
        // from an alert whose body failed to parse, or a quiet evening would look like a bug.
        return alert is null ? Results.NoContent() : Results.Ok(alert);
    }
}
