using Modbot.Core.Time;

namespace Modbot.Api.Features.Client.Devices;

/// <summary>
/// One moderator's install of the Windows client, as this deployment knows it.
/// </summary>
/// <remarks>
/// <para><strong>Every fact records which device reported it</strong>, which is what makes a
/// compromised or misbehaving client identifiable and its facts revocable as a set.</para>
/// <para><strong>One per device, individually revocable.</strong> A moderator leaving the team
/// must not require rotating every other moderator's token.</para>
/// </remarks>
/// <param name="TokenHash">The stored hash. The token itself never reaches this deployment's disk.</param>
/// <param name="IssuedToUserId">
/// Whose device this is. There is deliberately no device name beside it: a label the moderator
/// typed told an operator nothing they could act on, and the owner, the platform, the version and
/// when it last reported answer every question a settings list is asked.
/// </param>
/// <param name="RevokedAt">
/// Set rather than deleted, so the facts this device reported keep a device to point at. A revoked
/// device is refused at every endpoint from the moment it is set.
/// </param>
public sealed record ClientDevice(
    Guid Id,
    string TokenHash,
    string ClientVersion,
    string Platform,
    Guid IssuedToUserId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? LastSeenAt = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// A pairing code, between being shown in the web UI and being redeemed by a client.
/// </summary>
/// <param name="IssuedToUserId">
/// Whose code it is. The device inherits it, which is how "which moderator's client is this" is
/// answered.
/// </param>
public sealed record PairingCode(
    string CodeHash,
    Guid IssuedToUserId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt = null);

/// <summary>Where paired devices and outstanding pairing codes live.</summary>
/// <remarks>
/// <para>An interface because the durable implementation needs a table and a migration that this
/// change does not add. The shape is fixed here so the endpoints, their tests and the schema can
/// be written against the same contract.</para>
/// <para><strong>Redemption must be atomic.</strong> Two clients racing on one code is exactly the
/// case a short single-use credential exists to prevent, so <see cref="TryRedeemCodeAsync"/>
/// returns the code to exactly one caller rather than leaving the check and the mark as two
/// steps.</para>
/// </remarks>
public interface IClientDeviceStore
{
    Task<PairingCode> IssueCodeAsync(PairingCode code, CancellationToken ct);

    /// <summary>
    /// Marks a code redeemed and returns it, or null when it never existed, has expired, or has
    /// already been used. All three are one answer on purpose: telling a caller which of them it
    /// was would help somebody guessing.
    /// </summary>
    Task<PairingCode?> TryRedeemCodeAsync(string codeHash, DateTimeOffset now, CancellationToken ct);

    Task<ClientDevice> AddDeviceAsync(ClientDevice device, CancellationToken ct);

    /// <summary>Resolves a presented token. Returns null for unknown <em>and</em> for revoked.</summary>
    Task<ClientDevice?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>
    /// Records that this device was heard from, so an operator can see which moderators are
    /// actually reporting and which installs are stale.
    /// </summary>
    Task TouchAsync(Guid deviceId, DateTimeOffset seenAt, string clientVersion, CancellationToken ct);

    Task<IReadOnlyList<ClientDevice>> ListDevicesAsync(CancellationToken ct);

    Task<bool> RevokeAsync(Guid deviceId, DateTimeOffset revokedAt, CancellationToken ct);
}

/// <summary>
/// An in-memory store: correct, and deliberately not durable.
/// </summary>
/// <remarks>
/// <para>It exists so the client endpoints are complete and testable now. The durable
/// implementation needs a <c>client_device</c> table and a migration, which this change does not
/// add — see the notes on <see cref="IClientDeviceStore"/>.</para>
/// <para><strong>Restarting loses every pairing</strong>, which would mean every moderator
/// re-pairing after a deploy. That is why this must not be what a deployment runs.</para>
/// </remarks>
public sealed class InMemoryClientDeviceStore : IClientDeviceStore
{
    private readonly Dictionary<string, PairingCode> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ClientDevice> _devices = [];
    private readonly Lock _gate = new();

    public Task<PairingCode> IssueCodeAsync(PairingCode code, CancellationToken ct)
    {
        lock (_gate)
            _codes[code.CodeHash] = code;

        return Task.FromResult(code);
    }

    public Task<PairingCode?> TryRedeemCodeAsync(string codeHash, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_codes.TryGetValue(codeHash, out var code)
                || code.RedeemedAt is not null
                || code.ExpiresAt <= now)
            {
                return Task.FromResult<PairingCode?>(null);
            }

            var redeemed = code with { RedeemedAt = now };
            _codes[codeHash] = redeemed;
            return Task.FromResult<PairingCode?>(redeemed);
        }
    }

    public Task<ClientDevice> AddDeviceAsync(ClientDevice device, CancellationToken ct)
    {
        lock (_gate)
            _devices[device.Id] = device;

        return Task.FromResult(device);
    }

    public Task<ClientDevice?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        lock (_gate)
        {
            var device = _devices.Values.FirstOrDefault(
                d => string.Equals(d.TokenHash, tokenHash, StringComparison.Ordinal));

            // Revoked is the same answer as unknown, deliberately: revocation is immediate, and a
            // client that could tell the difference would know its token had once been valid.
            return Task.FromResult(device is { IsRevoked: false } ? device : null);
        }
    }

    public Task TouchAsync(Guid deviceId, DateTimeOffset seenAt, string clientVersion, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var device))
                _devices[deviceId] = device with { LastSeenAt = seenAt, ClientVersion = clientVersion };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientDevice>> ListDevicesAsync(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ClientDevice>>([.. _devices.Values.OrderBy(d => d.IssuedAt)]);
    }

    public Task<bool> RevokeAsync(Guid deviceId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || device.IsRevoked)
                return Task.FromResult(false);

            _devices[deviceId] = device with { RevokedAt = revokedAt };
            return Task.FromResult(true);
        }
    }
}

/// <summary>How long a pairing code is worth anything.</summary>
public static class PairingCodeLifetime
{
    /// <summary>
    /// Long enough to click through from the pairing page to the client, short enough that a code
    /// left in a browser history, a screenshot or a stream is worthless by the time anybody sees it.
    /// </summary>
    /// <remarks>
    /// Was ten minutes when the code was something a person read off a screen and retyped. It now
    /// travels inside a <c>modbot-client://</c> link, which lands in browser history and in Windows'
    /// record of protocol launches, so the window it is exposed in is cut to what the flow needs:
    /// a click, not a walk between screens.
    /// </remarks>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    public static PairingCode Issue(IModbotClock clock, Guid userId, string code)
        => new(DeviceTokens.Hash(code), userId, clock.UtcNow, clock.UtcNow + Default);
}
