using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;

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
/// <para><strong>A device is only as good as the person it belongs to.</strong> Every request
/// checks the owner again (<see cref="DeviceStanding"/>): an account that is disabled, deleted or
/// no longer holds "Pair a companion" stops its companions at their next request, the same way its
/// browser sessions stop.</para>
/// </remarks>
public sealed class DeviceAuthenticator
{
    private readonly ICompanionDeviceStore _devices;
    private readonly UserAccountService _accounts;
    private readonly IModbotClock _clock;

    public DeviceAuthenticator(ICompanionDeviceStore devices, UserAccountService accounts, IModbotClock clock)
    {
        _devices = devices;
        _accounts = accounts;
        _clock = clock;
    }

    public async Task<DeviceAuthentication> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ReadBearer(context) is not { Length: > 0 } token)
            return new DeviceAuthentication(null, CompanionApiErrors.Unauthorised());

        var now = _clock.UtcNow;

        // Refused the same way as an unknown token, whatever the reason. A companion that could
        // tell "your owner was disabled" from "no such token" would learn something about a
        // person from a credential that no longer speaks for them.
        var device = await DeviceStanding.ResolveAsync(DeviceTokens.Hash(token), _devices, _accounts, now, ct);
        if (device is null)
            return new DeviceAuthentication(null, CompanionApiErrors.Unauthorised());

        // Last-seen and the reported client version are surfaced in settings, so an operator can
        // see which moderators are actually reporting and how much of their coverage is running a
        // stale log parser. After the idle check, so a device refused for being idle stays idle.
        await _devices.TouchAsync(device.Id, now, ReadClientVersion(context) ?? device.CompanionVersion, ct);

        return new DeviceAuthentication(device, null);
    }

    /// <summary>The bearer token on the request, or null. The live stream re-hashes it to check a device again.</summary>
    internal static string? ReadBearer(HttpContext context)
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

/// <summary>
/// Whether a paired device may still be used: not revoked, not idle too long, and owned by an
/// account that is enabled and holds "Pair a companion".
/// </summary>
/// <remarks>
/// <para>
/// One place, because two callers ask it: <see cref="DeviceAuthenticator"/> on every request, and
/// the live stream's refresh while a socket or a poll stays open. Both have to give the same
/// answer, or a disabled moderator's open socket would carry on after their next request was refused.
/// </para>
/// <para>
/// <strong>Idle.</strong> A device not heard from in <see cref="IdleLimit"/> is refused, counted
/// from when it was last seen, or from when it was paired if it never was. Nothing is written when
/// it happens: the row keeps its last-seen time, which is what the settings list shows, and the
/// device stays refused because refusing it writes nothing newer. The limit is fixed rather than
/// a setting, and there is no column for it; last-seen already says everything it needs.
/// </para>
/// <para>
/// <strong>The owner</strong> is read with the session check's own read
/// (<see cref="UserAccountService.StateAsync"/>), so a companion and a browser session stop for
/// the same reasons. A deleted account is disabled and holds no roles, so it fails both ways.
/// </para>
/// </remarks>
public static class DeviceStanding
{
    /// <summary>How long a device may go unused before it is refused.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromDays(90);

    /// <summary>The shortest gap between two writes of a device's last-seen time.</summary>
    public static readonly TimeSpan SeenWriteEvery = TimeSpan.FromMinutes(5);

    /// <summary>Whether the device has gone unused for longer than <see cref="IdleLimit"/>.</summary>
    public static bool IsIdle(CompanionDevice device, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);

        return now - (device.LastSeenAt ?? device.IssuedAt) > IdleLimit;
    }

    /// <summary>Whether this account may have a working companion: it exists, is enabled, and holds the permission.</summary>
    public static bool OwnerMayUse(AccountState? owner)
        => owner is { IsDisabled: false } state
           && ModbotAuth.Allows(state.Permissions, ModbotPermissions.PairCompanion);

    /// <summary>
    /// Whether last-seen should be written now. The store's conditional update says the same thing
    /// in SQL.
    /// </summary>
    public static bool SeenIsDue(
        DateTimeOffset? lastSeenAt, string storedVersion, DateTimeOffset now, string reportedVersion)
        => lastSeenAt is null
           || lastSeenAt.Value <= now - SeenWriteEvery
           || !string.Equals(storedVersion, reportedVersion, StringComparison.Ordinal);

    /// <summary>The device this token belongs to, when it may still be used. Null otherwise.</summary>
    public static async Task<CompanionDevice?> ResolveAsync(
        string tokenHash,
        ICompanionDeviceStore devices,
        UserAccountService accounts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(accounts);

        var device = await devices.FindByTokenHashAsync(tokenHash, ct);
        if (device is null || IsIdle(device, now))
            return null;

        var owner = await accounts.StateAsync(device.IssuedToUserId, ct);
        return OwnerMayUse(owner) ? device : null;
    }
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
