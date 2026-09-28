using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Members;

/// <summary>
/// Keeps <c>discord_ban</c>, the Discord server's ban list as the bot last saw it, for the Bans
/// page's Discord list.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two sources.</strong> The whole list is read from Discord on sign-in and once a day
/// (<see cref="ListedAsync"/>), which is the only way to learn of bans from before the bot joined
/// and needs Ban Members. Between reads, a ban or unban seen on the gateway changes its one row
/// (<see cref="BannedAsync"/>, <see cref="UnbannedAsync"/>), with no request. The gateway event
/// carries no reason; the audit log does when the bot may read it (<see cref="AuditLogAsync"/>), and
/// otherwise the next read fills it in.
/// </para>
/// <para>
/// <strong>Dates.</strong> Discord's list carries none, so a ban first found in a read has no
/// <see cref="DiscordBan.BannedAt"/>: it happened some time before. One seen as it happened has the
/// moment it was seen, or the audit log's.
/// </para>
/// <para>
/// This is not the fact log. The facts for bans and unbans are written by
/// <see cref="DiscordEventRecorder"/>, as before; this only keeps the list those facts describe.
/// </para>
/// </remarks>
public sealed class DiscordBanList
{
    /// <summary>How old the last full read may get before the next is due.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromDays(1);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public DiscordBanList(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Whether a full read is due: the bot holds Ban Members, and the list has never been read or
    /// was last read <see cref="ReadEvery"/> ago or more.
    /// </summary>
    public async Task<bool> ReadDueAsync(string guildId, CancellationToken ct)
    {
        var server = await _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => new { s.BotCanBanMembers, s.BansListedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return server is { BotCanBanMembers: true }
            && (server.BansListedAt is not { } at || _clock.UtcNow - at >= ReadEvery);
    }

    /// <summary>
    /// The whole list, as Discord gave it: every ban in it stands, and every stored ban that stands
    /// but is not in it was lifted while nobody was looking.
    /// </summary>
    public async Task ListedAsync(string guildId, IReadOnlyList<DiscordBanSnapshot> bans, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bans);

        var now = _clock.UtcNow;

        var rows = await _db.DiscordBans
            .Where(b => b.GuildId == guildId)
            .ToDictionaryAsync(b => b.UserId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var names = await MemberNamesAsync(guildId, bans.Select(b => b.UserId).ToList(), ct).ConfigureAwait(false);
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ban in bans)
        {
            if (!listed.Add(ban.UserId))
                continue;

            if (!rows.TryGetValue(ban.UserId, out var row))
            {
                row = new DiscordBan { GuildId = guildId, UserId = ban.UserId, FirstSeenAt = now };
                _db.DiscordBans.Add(row);
            }
            else if (row.LiftedAt is not null)
            {
                // Banned again while the bot was away: when is not known.
                row.LiftedAt = null;
                row.BannedAt = null;
            }

            row.Username = Blank(ban.Username) ?? row.Username;
            row.DisplayName = names.GetValueOrDefault(ban.UserId) ?? Blank(ban.GlobalName) ?? Blank(ban.Username) ?? row.DisplayName;
            row.AvatarUrl = Blank(ban.AvatarUrl) ?? row.AvatarUrl;
            row.Reason = Blank(ban.Reason) ?? row.Reason;
            row.UpdatedAt = now;
        }

        foreach (var row in rows.Values)
        {
            if (row.LiftedAt is null && !listed.Contains(row.UserId))
            {
                row.LiftedAt = now;
                row.UpdatedAt = now;
            }
        }

        if (await _db.DiscordServers.FirstOrDefaultAsync(s => s.GuildId == guildId, ct).ConfigureAwait(false) is { } server)
            server.BansListedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A ban seen on the gateway, now.</summary>
    public async Task BannedAsync(string guildId, string userId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var row = await _db.DiscordBans.FirstOrDefaultAsync(b => b.GuildId == guildId && b.UserId == userId, ct).ConfigureAwait(false);

        if (row is null)
        {
            row = new DiscordBan { GuildId = guildId, UserId = userId, FirstSeenAt = now, BannedAt = now };
            _db.DiscordBans.Add(row);
        }
        else if (row.LiftedAt is not null)
        {
            row.LiftedAt = null;
            row.BannedAt = now;
            row.Reason = null;
        }

        if (await MemberAsync(guildId, userId, ct).ConfigureAwait(false) is { } member)
        {
            row.Username = member.Username;
            row.DisplayName = member.DisplayName;
            row.AvatarUrl = member.AvatarUrl ?? row.AvatarUrl;
        }

        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>An unban seen on the gateway, now.</summary>
    public async Task UnbannedAsync(string guildId, string userId, CancellationToken ct)
    {
        var row = await _db.DiscordBans.FirstOrDefaultAsync(b => b.GuildId == guildId && b.UserId == userId, ct).ConfigureAwait(false);
        if (row is null || row.LiftedAt is not null)
            return;

        var now = _clock.UtcNow;
        row.LiftedAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The audit log's ban entries: the reason and the exact time, for a ban that still stands.
    /// Only entries at or after the stored ban count, so an old entry does not describe a newer ban.
    /// </summary>
    public async Task AuditLogAsync(string guildId, IReadOnlyList<DiscordAuditEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var bans = entries
            .Where(e => e.Kind == DiscordAuditKinds.Ban && e.TargetId is not null)
            .ToList();

        if (bans.Count == 0)
            return;

        var ids = bans.Select(e => e.TargetId!).Distinct(StringComparer.Ordinal).ToList();
        var rows = await _db.DiscordBans
            .Where(b => b.GuildId == guildId && ids.Contains(b.UserId) && b.LiftedAt == null)
            .ToDictionaryAsync(b => b.UserId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var changed = false;

        foreach (var entry in bans)
        {
            if (!rows.TryGetValue(entry.TargetId!, out var row))
                continue;

            // A ban seen live is stamped when the gateway said so; the audit entry for it can be a
            // moment earlier. Anything older than that window is an earlier ban.
            if (row.BannedAt is { } at && entry.At < at - DiscordEventRecorder.SameActionWindow)
                continue;

            row.BannedAt = entry.At;
            row.Reason = Blank(entry.Reason) ?? row.Reason;
            row.UpdatedAt = _clock.UtcNow;
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private Task<DiscordMember?> MemberAsync(string guildId, string userId, CancellationToken ct)
        => _db.DiscordMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);

    /// <summary>The name each banned person had in the server, for those who were ever members.</summary>
    private async Task<Dictionary<string, string>> MemberNamesAsync(string guildId, List<string> ids, CancellationToken ct)
    {
        var names = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && ids.Contains(m.UserId))
            .Select(m => new { m.UserId, m.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return names
            .Where(n => !string.IsNullOrEmpty(n.DisplayName))
            .ToDictionary(n => n.UserId, n => n.DisplayName, StringComparer.Ordinal);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
