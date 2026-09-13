using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Client.Devices;

/// <summary>
/// The durable <see cref="IClientDeviceStore"/>, backed by <c>client_device</c> and
/// <c>client_pairing_code</c>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <see cref="InMemoryClientDeviceStore"/>, which was correct and deliberately not
/// durable. With that one, every redeploy silently unpaired every moderator — and because a
/// client treats <c>401</c> as terminal and stops reporting rather than retrying, the symptom
/// would have been presence data quietly ceasing after each deploy, with each moderator having to
/// notice and re-pair.
/// </para>
/// <para>
/// Nothing here is cached. Revocation has to take effect at the next request, not at the next
/// cache expiry, because the case it exists for is a moderator who should stop being able to
/// report facts right now.
/// </para>
/// </remarks>
public sealed class DatabaseClientDeviceStore(ModbotContext db) : IClientDeviceStore
{
    public async Task<PairingCode> IssueCodeAsync(PairingCode code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);

        db.ClientPairingCodes.Add(new ClientPairingCodeRecord
        {
            CodeHash = code.CodeHash,
            IssuedToUserId = code.IssuedToUserId,
            IssuedAt = code.IssuedAt,
            ExpiresAt = code.ExpiresAt,
            RedeemedAt = code.RedeemedAt,
        });

        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>
    /// Marks a code redeemed and returns it, or null when it never existed, has expired, or was
    /// already used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One conditional <c>UPDATE</c>, not a read followed by a write. Two clients racing on a
    /// single-use code is exactly what the credential exists to prevent, and a check-then-act
    /// would let both through: both read an unredeemed row, both write, both pair. Letting the
    /// database decide who wins is the only version that holds under concurrency.
    /// </para>
    /// <para>
    /// All three failure reasons return the same answer on purpose. Telling a caller which one it
    /// was would help somebody guessing codes.
    /// </para>
    /// </remarks>
    public async Task<PairingCode?> TryRedeemCodeAsync(
        string codeHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var redeemed = await db.ClientPairingCodes
            .Where(c => c.CodeHash == codeHash && c.RedeemedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RedeemedAt, now), ct);

        if (redeemed == 0) return null;

        var row = await db.ClientPairingCodes.AsNoTracking()
            .SingleAsync(c => c.CodeHash == codeHash, ct);

        return new PairingCode(row.CodeHash, row.IssuedToUserId, row.IssuedAt, row.ExpiresAt, row.RedeemedAt);
    }

    public async Task<ClientDevice> AddDeviceAsync(ClientDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        db.ClientDevices.Add(new ClientDeviceRecord
        {
            Id = device.Id,
            TokenHash = device.TokenHash,
            ClientVersion = device.ClientVersion,
            Platform = device.Platform,
            IssuedToUserId = device.IssuedToUserId,
            IssuedAt = device.IssuedAt,
            LastSeenAt = device.LastSeenAt,
            RevokedAt = device.RevokedAt,
        });

        await db.SaveChangesAsync(ct);
        return device;
    }

    /// <summary>Resolves a presented token. Null for unknown <em>and</em> for revoked.</summary>
    /// <remarks>
    /// Revoked is filtered here rather than at each call site, so there is exactly one place the
    /// check can be forgotten — and it is not forgotten.
    /// </remarks>
    public async Task<ClientDevice?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        var row = await db.ClientDevices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.TokenHash == tokenHash && d.RevokedAt == null, ct);

        return row is null ? null : Map(row);
    }

    /// <remarks>
    /// A bare <c>UPDATE</c> rather than load-modify-save: this runs on every batch a client sends,
    /// and it must never be the thing that makes ingest slow or that fails a report because a
    /// concurrent write touched the same row.
    /// </remarks>
    public Task TouchAsync(Guid deviceId, DateTimeOffset seenAt, string clientVersion, CancellationToken ct) =>
        db.ClientDevices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(
                u => u.SetProperty(d => d.LastSeenAt, seenAt)
                      .SetProperty(d => d.ClientVersion, clientVersion),
                ct);

    /// <summary>
    /// Every device, revoked ones included.
    /// </summary>
    /// <remarks>
    /// An operator auditing who can report needs to see what was revoked and when, not a list
    /// that quietly omits it.
    /// </remarks>
    public async Task<IReadOnlyList<ClientDevice>> ListDevicesAsync(CancellationToken ct)
    {
        var rows = await db.ClientDevices.AsNoTracking()
            .OrderByDescending(d => d.IssuedAt)
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    /// <remarks>
    /// Idempotent, and it never un-revokes. Revoking twice returns false the second time because
    /// nothing changed — not because anything went wrong — and the original revocation time is
    /// preserved, since that is the moment the facts after it become suspect.
    /// </remarks>
    public async Task<bool> RevokeAsync(Guid deviceId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        var changed = await db.ClientDevices
            .Where(d => d.Id == deviceId && d.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.RevokedAt, revokedAt), ct);

        return changed > 0;
    }

    private static ClientDevice Map(ClientDeviceRecord r) => new(
        r.Id,
        r.TokenHash,
        r.ClientVersion,
        r.Platform,
        r.IssuedToUserId,
        r.IssuedAt,
        r.LastSeenAt,
        r.RevokedAt);
}
