using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Linking;

/// <summary>What one pass of the role job did.</summary>
/// <param name="Problem">The last change Discord refused, as a sentence, or null.</param>
public sealed record LinkedRolePass(int Given, int Removed, int NotInServer, string? Problem);

/// <summary>
/// Gives linked members the linked role and the 18+ role, and takes away the ones they should no
/// longer hold (Discord account linking design §6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One comparison covers every way the answer can move.</strong> Each link row records the
/// role ids Modbot gave. What should be held comes from the settings, from whether the link is still
/// active, and from the VRChat record's sticky 18+ flag. A pass compares the two and changes the
/// difference, so a link saved, a link ended, a role setting changed, profile sync setting the flag
/// and a moderator clearing it all end up here without code of their own.
/// </para>
/// <para>
/// <strong>Only roles Modbot gave are taken away.</strong> A role handed out by hand in Discord is
/// not recorded on the row, so nothing here ever removes it.
/// </para>
/// <para>
/// At most <see cref="MaxRowsPerPass"/> rows a pass; the next pass carries on. A member Discord says
/// is not in the server is left for <see cref="NotInServerWait"/>, or until they join.
/// </para>
/// </remarks>
public sealed class LinkedRoles
{
    public const int MaxRowsPerPass = 50;

    public static readonly TimeSpan NotInServerWait = TimeSpan.FromDays(1);

    public const string LinkedKind = "linked";
    public const string EighteenPlusKind = "18+";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;

    public LinkedRoles(ModbotContext db, IFactWriter facts, EventPartitionMaintainer partitions, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
    }

    public async Task<LinkedRolePass> RunAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordGuildId, s.DiscordLinkedRoleId, s.DiscordEighteenPlusRoleId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var guildId = Blank(settings?.DiscordGuildId);
        if (guildId is null)
            return new LinkedRolePass(0, 0, 0, null);

        var linkedRole = Blank(settings!.DiscordLinkedRoleId);
        var eighteenPlusRole = Blank(settings.DiscordEighteenPlusRoleId);

        var now = _clock.UtcNow;
        var waitedSince = now - NotInServerWait;

        // Everything whose recorded roles differ from what it should hold, decided in the database
        // so a pass over a server where nothing changed reads no rows.
        var rows = await _db.DiscordAccountLinks
            .Where(l => l.NotInServerAt == null || l.NotInServerAt < waitedSince)
            .Select(l => new
            {
                Link = l,
                EighteenPlus = l.UnlinkedAt == null
                               && _db.VRChatUsers.Any(u => u.UserId == l.VRChatUserId && u.Is18PlusVerified),
            })
            .Where(x => x.Link.UnlinkedAt == null
                ? x.Link.LinkedRoleId != linkedRole
                  || (x.EighteenPlus
                      ? x.Link.EighteenPlusRoleId != eighteenPlusRole
                      : x.Link.EighteenPlusRoleId != null)
                : x.Link.LinkedRoleId != null || x.Link.EighteenPlusRoleId != null)
            .OrderBy(x => x.Link.LinkedAt)
            .Take(MaxRowsPerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var pass = new Tally();

        foreach (var row in rows)
        {
            var link = row.Link;
            var active = link.IsActive;

            await FixAsync(
                    gateway, guildId, link, LinkedKind,
                    held: link.LinkedRoleId,
                    wanted: active ? linkedRole : null,
                    set: id => link.LinkedRoleId = id,
                    pass, ct)
                .ConfigureAwait(false);

            // A member found not to be in the server is not asked about a second role in the same pass.
            if (link.NotInServerAt != now)
            {
                await FixAsync(
                        gateway, guildId, link, EighteenPlusKind,
                        held: link.EighteenPlusRoleId,
                        wanted: active && row.EighteenPlus ? eighteenPlusRole : null,
                        set: id => link.EighteenPlusRoleId = id,
                        pass, ct)
                    .ConfigureAwait(false);
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new LinkedRolePass(pass.Given, pass.Removed, pass.NotInServer, pass.Problem);
    }

    private async Task FixAsync(
        IDiscordGateway gateway,
        string guildId,
        DiscordAccountLink link,
        string kind,
        string? held,
        string? wanted,
        Action<string?> set,
        Tally pass,
        CancellationToken ct)
    {
        if (string.Equals(held, wanted, StringComparison.Ordinal))
            return;

        if (held is not null)
        {
            var removed = await gateway.RemoveRoleAsync(guildId, link.DiscordUserId, held, ct).ConfigureAwait(false);

            if (removed.Done)
            {
                set(null);
                pass.Removed++;
                await RecordAsync(FactType.DiscordLinkRoleRemoved, link, held, kind, ct).ConfigureAwait(false);
            }
            else if (removed.NotInServer || removed.RoleGone)
            {
                // Either way they no longer hold it; there is nothing left to take away.
                set(null);
            }
            else
            {
                link.RoleError = removed.Error;
                pass.Problem = removed.Error;
                return;
            }
        }

        if (wanted is null)
            return;

        var added = await gateway.AddRoleAsync(guildId, link.DiscordUserId, wanted, ct).ConfigureAwait(false);

        if (added.Done)
        {
            set(wanted);
            link.RoleError = null;
            pass.Given++;
            await RecordAsync(FactType.DiscordLinkRoleGranted, link, wanted, kind, ct).ConfigureAwait(false);
        }
        else if (added.NotInServer)
        {
            link.NotInServerAt = _clock.UtcNow;
            pass.NotInServer++;
        }
        else
        {
            link.RoleError = added.Error;
            pass.Problem = added.Error;
        }
    }

    private async Task RecordAsync(string type, DiscordAccountLink link, string roleId, string kind, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var roleName = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.RoleId == roleId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = link.DiscordUserId,
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["roleId"] = roleId,
                    ["roleName"] = roleName,
                    ["kind"] = kind,
                    ["vrchatUserId"] = link.VRChatUserId,
                    ["discordUsername"] = link.DiscordUsername,
                },
            }, ct)
            .ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Tally
    {
        public int Given;
        public int Removed;
        public int NotInServer;
        public string? Problem;
    }
}
