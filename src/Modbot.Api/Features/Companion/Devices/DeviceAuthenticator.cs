using Microsoft.AspNetCore.Http;
using Modbot.Core;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Companion.Devices;

/// <param name="Device">Null unless the token resolved to a live device.</param>
/// <param name="Failure">The response to return when it did not.</param>
public readonly record struct DeviceAuthentication(CompanionDevice? Device, IResult? Failure)
{
    public bool Succeeded => Device is not null;
}

/// <summary>
/// Resolves the <c>Authorization: Bearer</c> header on a client request to a paired device.
/// </summary>
/// <remarks>
/// <para><strong>Ingest scope, and nothing else.</strong> A device token authenticates a
/// <em>client</em>, never a person. It cannot ban, cannot kick, cannot read the member list and
/// cannot reach any endpoint outside this feature — a moderator acting on what they see in the
/// overlay goes through the normal authenticated API as themselves. Stolen, this token can submit
/// presence facts and read one group's roster context, which is the whole blast radius.</para>
/// <para><strong>Revoked and unknown are the same answer.</strong> Revocation is server-side and
/// immediate; a client that could tell "revoked" from "never existed" would learn that its token
/// had once been valid.</para>
/// <para><strong>This is deliberately not an authentication scheme.</strong> Registering it beside
/// the staff cookie handler would put a second credential type in front of every endpoint in the
/// application, and the one property worth guaranteeing is that a device token opens nothing
/// outside this folder. A resolver the client endpoints call explicitly cannot leak sideways.</para>
/// </remarks>
public sealed class DeviceAuthenticator
{
    private readonly ICompanionDeviceStore _devices;
    private readonly IModbotClock _clock;

    public DeviceAuthenticator(ICompanionDeviceStore devices, IModbotClock clock)
    {
        _devices = devices;
        _clock = clock;
    }

    public async Task<DeviceAuthentication> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ReadBearer(context) is not { Length: > 0 } token)
            return new DeviceAuthentication(null, CompanionApiErrors.Unauthorised());

        var device = await _devices.FindByTokenHashAsync(DeviceTokens.Hash(token), ct);
        if (device is null)
            return new DeviceAuthentication(null, CompanionApiErrors.Unauthorised());

        // Last-seen and the reported client version are surfaced in settings, so an operator can
        // see which moderators are actually reporting and how much of their coverage is running a
        // stale log parser.
        await _devices.TouchAsync(device.Id, _clock.UtcNow, ReadClientVersion(context) ?? device.CompanionVersion, ct);

        return new DeviceAuthentication(device, null);
    }

    private static string? ReadBearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>
    /// The client's own version, when it offered one. Reads like telemetry and is not: it is what
    /// tells an operator that half their coverage is on a build whose log parser broke last week.
    /// </summary>
    private static string? ReadClientVersion(HttpContext context)
        => context.Request.Headers.TryGetValue("X-Modbot-Companion-Version", out var values)
            ? values.ToString() is { Length: > 0 } and { Length: <= 32 } version ? version : null
            : null;
}

/// <summary>Whether a requested <c>/api/v{n}/</c> is one this server still speaks.</summary>
/// <remarks>
/// The version is a property of the pairing rather than of a request, so this is not content
/// negotiation — it is the check that turns "the operator upgraded past this client" into a
/// <c>409</c> the client knows to renegotiate from, instead of failures it would read as
/// transient and retry forever.
/// </remarks>
public static class CompanionApiVersion
{
    public static bool IsSupported(int version)
        => version >= ModbotVersion.ApiMinimum && version <= ModbotVersion.Api;
}
