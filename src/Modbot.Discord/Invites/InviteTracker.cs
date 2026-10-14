using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Invites;

/// <summary>
/// Finds which invite each new Discord member used, by comparing the invites' use counts before and
/// after they joined (invite tracking).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discord does not say which invite a person used.</strong> It only keeps a count on each
/// invite, so Modbot keeps the count it last accounted for (<see cref="DiscordInviteLink.Uses"/>), reads
/// the invites when somebody joins, and the invite whose count went up is the one. It counts one use
/// at a time, so two people joining before the first is looked at are each given an invite.
/// </para>
/// <para>
/// <strong>When it cannot tell.</strong> No invite went up (the join came through a link Modbot does
/// not see, such as one used while the bot was offline) or more than one did (two people joined
/// through different links at once): the join is kept with no invite. An invite used up on this
/// very join is already gone from Discord's list, so one that allowed exactly one more use and was
/// marked gone a moment ago counts.
/// </para>
/// <para>
/// Only while invite tracking is on in the settings: while it is off every entry point does nothing,
/// so no join, invite or count is recorded.
/// </para>
/// <para>
/// One at a time: every change to the counts goes through <see cref="OneAtATime"/>, since two joins
/// read together would each see the same increase.
/// </para>
/// </remarks>
public sealed class InviteTracker(ModbotContext db, IModbotClock clock)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// An invite Modbot has not seen before that was made this recently is taken to have no uses
    /// before the join being looked at; an older one is taken to be as used as Discord says.
    /// </summary>
    public static readonly TimeSpan NewInvite = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long an invite that has just been marked gone still counts as the one a join used up.
    /// An invite that Modbot has not heard was deleted is not taken for it, since it may have
    /// expired long ago: that join is kept with no invite.
    /// </summary>
    public static readonly TimeSpan JustGone = TimeSpan.FromMinutes(1);

    /// <summary>Records one person joining, with the invite they used when that is clear. Bots are left out.</summary>
    public async Task JoinedAsync(IDiscordGateway gateway, DiscordMemberJoin join, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(join);

        // A bot is added from the developer site, never by an invite link.
        if (join.IsBot)
            return;

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!await IsOnAsync(ct).ConfigureAwait(false))
                return;

            var now = clock.UtcNow;
            var read = await gateway.ReadInvitesAsync(join.GuildId, ct).ConfigureAwait(false);
            var used = read.Ok ? await UsedAsync(read.Invites, now, ct).ConfigureAwait(false) : null;

            db.DiscordInviteJoins.Add(new DiscordInviteJoin
            {
                UserId = join.UserId,
                JoinedAt = join.Member?.JoinedAt ?? now,
                InviteId = used?.Id,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>
    /// Stores the invites Modbot has not seen. With <paramref name="resetCounts"/> the counts it
    /// accounts for become Discord's own, which is what a new session needs: whoever joined while
    /// the bot was away cannot be told apart afterwards.
    /// </summary>
    public async Task ReadAsync(IReadOnlyList<DiscordInviteSnapshot> invites, bool resetCounts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invites);

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!await IsOnAsync(ct).ConfigureAwait(false))
                return;

            var now = clock.UtcNow;
            var known = await db.DiscordInviteLinks.ToDictionaryAsync(i => i.Code, ct).ConfigureAwait(false);
            var live = invites.Select(i => i.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var invite in invites)
            {
                if (!known.TryGetValue(invite.Code, out var row))
                {
                    Add(invite, invite.Uses, now);
                    continue;
                }

                Refresh(row, invite);

                if (resetCounts)
                    row.Uses = invite.Uses;
            }

            foreach (var row in known.Values.Where(r => r.GoneAt is null && !live.Contains(r.Code)))
                row.GoneAt = now;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Stores an invite Discord says was just made, with no uses yet.</summary>
    public async Task MadeAsync(DiscordInviteSnapshot invite, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invite);

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (await IsOnAsync(ct).ConfigureAwait(false)
                && !await db.DiscordInviteLinks.AnyAsync(i => i.Code == invite.Code, ct).ConfigureAwait(false))
            {
                Add(invite, invite.Uses, clock.UtcNow);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Marks an invite gone. The row stays, since its name and joins are still wanted.</summary>
    public async Task RemovedAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (await IsOnAsync(ct).ConfigureAwait(false)
                && await db.DiscordInviteLinks.FirstOrDefaultAsync(i => i.Code == code, ct).ConfigureAwait(false) is { GoneAt: null } row)
            {
                row.GoneAt = clock.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>
    /// Stores an invite Modbot just made for a name. Discord's own report of the new invite may
    /// have come first, so the row that is there is named rather than a second one made.
    /// </summary>
    public async Task NamedAsync(DiscordInviteSnapshot invite, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!await IsOnAsync(ct).ConfigureAwait(false))
                return;

            var row = await db.DiscordInviteLinks.FirstOrDefaultAsync(i => i.Code == invite.Code, ct).ConfigureAwait(false)
                ?? Add(invite, invite.Uses, clock.UtcNow);

            row.Name = name;
            row.MadeByModbot = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<bool> IsOnAsync(CancellationToken ct)
        => await db.Settings.AsNoTracking()
            .Select(s => s.DiscordInviteTrackingOn)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private async Task<DiscordInviteLink?> UsedAsync(IReadOnlyList<DiscordInviteSnapshot> invites, DateTimeOffset now, CancellationToken ct)
    {
        var known = await db.DiscordInviteLinks.ToDictionaryAsync(i => i.Code, ct).ConfigureAwait(false);
        var live = invites.Select(i => i.Code).ToHashSet(StringComparer.Ordinal);

        // Each candidate is an invite whose count went up, with the count it should be left at.
        var candidates = new List<(DiscordInviteLink Row, int Count, bool UsedUp)>();

        foreach (var invite in invites)
        {
            if (known.TryGetValue(invite.Code, out var row))
            {
                Refresh(row, invite);

                if (invite.Uses > row.Uses)
                    candidates.Add((row, invite.Uses, false));

                continue;
            }

            // Not seen before. One made a moment ago is a new link that has just been used; an older
            // one has uses nobody can place, so its count is taken as it stands.
            var fresh = invite.CreatedAt is { } created && now - created <= NewInvite;
            row = Add(invite, fresh ? 0 : invite.Uses, now);

            if (fresh && invite.Uses > 0)
                candidates.Add((row, invite.Uses, false));
        }

        // An invite with one use left is deleted by Discord as it is used, so it is missing from the list.
        // Only one marked gone a moment ago: one Modbot never heard was deleted may have expired long ago.
        foreach (var row in known.Values.Where(r => !live.Contains(r.Code) && r.MaxUses > 0 && r.Uses + 1 == r.MaxUses
            && r.GoneAt is { } gone && now - gone <= JustGone))
        {
            candidates.Add((row, row.MaxUses, true));
        }

        // Whatever Discord no longer lists is gone, used up or not.
        foreach (var row in known.Values.Where(r => r.GoneAt is null && !live.Contains(r.Code)))
            row.GoneAt = now;

        if (candidates.Count == 1)
        {
            var (row, count, usedUp) = candidates[0];

            // One use only: others still unplaced are left for the joins that follow.
            row.Uses = usedUp ? count : row.Uses + 1;
            return row;
        }

        // Two went up at once: who used which cannot be told, and counting again from here is
        // better than blaming the next person.
        foreach (var (row, count, _) in candidates)
            row.Uses = count;

        return null;
    }

    private DiscordInviteLink Add(DiscordInviteSnapshot invite, int uses, DateTimeOffset now)
    {
        var row = new DiscordInviteLink { FirstSeenAt = now, Uses = uses };
        Refresh(row, invite);
        db.DiscordInviteLinks.Add(row);
        return row;
    }

    private static void Refresh(DiscordInviteLink row, DiscordInviteSnapshot invite)
    {
        row.Code = invite.Code;
        row.MaxUses = invite.MaxUses;
        row.ChannelId = invite.ChannelId;
        row.InviterId = invite.InviterId;
        row.IsVanity = invite.IsVanity;
        row.GoneAt = null;
    }
}
