using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Sync;

/// <summary>
/// Copies bans and unbans between the VRChat group and the Discord server (M5 §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It reads the fact log, not events.</strong> Every ban Modbot knows about becomes a fact
/// — from VRChat's group audit log, from Discord's server audit log, or from the gateway when the
/// bot may not read that. Working from the log rather than from live events means one code path
/// for a ban seen this second and a ban seen after three days offline, it means what the sync acts
/// on is exactly what a moderator reads, and it is where M5 §4.2 asks for the loop check to live:
/// "at the fact layer, so it holds regardless of which code path triggered the action".
/// </para>
/// <para>
/// <strong>Each direction is its own switch and both start off</strong> (§4.1). A Discord ban is
/// often issued for chat behaviour by somebody with no VRChat authority, and turning that into
/// removal from the group is a real widening of a punishment. Unbans follow the direction their
/// bans do, with no switch of their own: a group that copies bans but not unbans quietly
/// accumulates people who are forgiven in one place and banned forever in the other.
/// </para>
/// <para>
/// <strong>Two guards against the loop, because one of them cannot work in both directions.</strong>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <em>The copy record.</em> Written before every outbound ban, and checked against every incoming
/// one. This works in both directions and is the guard that matters. See <see cref="CopyRecords"/>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>The actor, on the Discord side only.</em> Discord's audit log names whoever banned, and a
/// ban Modbot performed names the bot's own account. So a ban a person issued and a ban Modbot
/// copied look different in the permanent record, and only the person's one is ever copied
/// onwards. The same trick is worthless in the other direction: VRChat attributes everything
/// Modbot does to Modbot's own account, so a ban a moderator pressed in Modbot and a ban Modbot
/// copied from Discord are indistinguishable by actor, and only the copy record tells them apart.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>Somebody with no link is left alone.</strong> Most people on either platform have no
/// account tied to them on the other, and there is nothing to copy for them. They are counted, so
/// an operator can see how much of their server the sync does not reach, and otherwise untouched.
/// </para>
/// </remarks>
public sealed class BanSync
{
    /// <summary>How many facts one pass reads.</summary>
    public const int MaxFactsPerPass = 200;

    /// <summary>
    /// How many bans one pass actually sends.
    /// </summary>
    /// <remarks>
    /// Small on purpose. Discord queues the bot's requests behind one another, so a burst of bans
    /// would delay every announcement, card and command the bot owes anybody. The facts that are
    /// left stay unread and the next pass takes them.
    /// </remarks>
    public const int MaxCopiesPerPass = 20;

    /// <summary>How many differences one catch-up run copies. The rest wait for the next press.</summary>
    public const int MaxCatchUpPerRun = 100;

    /// <summary>How many differences a dry run describes before it stops listing and only counts.</summary>
    public const int MaxListed = 500;

    /// <summary>
    /// How many days of the person's messages Discord deletes with a copied ban.
    /// </summary>
    /// <remarks>
    /// None. Modbot stores the server's messages so a moderator can read what somebody said, and a
    /// copied ban wiping that is the opposite of what the record is for. Somebody who wants the
    /// messages gone deletes them in Discord.
    /// </remarks>
    public const int KeepsMessages = 0;

    private static readonly string[] WatchedTypes =
    [
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberUnbanned,
    ];

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly CopyRecords _copies;
    private readonly IVRChatModerationActions _vrchat;

    public BanSync(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        CopyRecords copies,
        IVRChatModerationActions vrchat)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(vrchat);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _copies = copies;
        _vrchat = vrchat;
    }

    /// <summary>
    /// Reads the bans and unbans recorded since the last pass and copies the ones that should
    /// cross over.
    /// </summary>
    public async Task<BanSyncPass> RunAsync(IDiscordGateway? gateway, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new
            {
                s.DiscordGuildId,
                s.ManagedGroupId,
                s.DiscordBanSyncToDiscord,
                s.DiscordBanSyncToVRChat,
                s.DiscordBanCopyAction,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings is null || (!settings.DiscordBanSyncToDiscord && !settings.DiscordBanSyncToVRChat))
            return Empty();

        if (Blank(settings.DiscordGuildId) is not { } guildId)
            return Empty();

        var state = await StateAsync(ct).ConfigureAwait(false);

        var facts = await _db.Events.AsNoTracking()
            .Where(e => e.Id > state.BansReadThrough && WatchedTypes.Contains(e.Type))
            .OrderBy(e => e.Id)
            .Take(MaxFactsPerPass)
            .Select(e => new { e.Id, e.Type, e.SubjectId, e.SubjectPlatform, e.ActorId, e.OccurredAt, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (facts.Count == 0)
            return Empty();

        var botUserId = gateway?.BotUserId;
        var tally = new Tally();
        var changes = new List<PlannedChange>();

        foreach (var fact in facts)
        {
            if (tally.Copied >= MaxCopiesPerPass || tally.Stop)
            {
                tally.Left++;
                continue;
            }

            // The marker moves only once this fact has been dealt with. A copy stopped by a
            // missing permission leaves it where it is, so the ban is picked up again the moment
            // an operator fixes the permission rather than being lost to a setup mistake.
            var fromDiscord = fact.SubjectPlatform == FactPlatform.Discord;
            var banning = fact.Type is FactType.MemberBanned or FactType.DiscordMemberBanned;

            var wanted = fromDiscord ? settings.DiscordBanSyncToVRChat : settings.DiscordBanSyncToDiscord;

            // Guard two: on Discord, a ban the bot's own account issued is Modbot's own work.
            var wasTheBot = fromDiscord
                            && botUserId is not null
                            && string.Equals(fact.ActorId, botUserId, StringComparison.Ordinal);

            // Guard one: a copy Modbot sent this way, not yet answered for.
            var wasOurCopy = wanted
                             && !wasTheBot
                             && await _copies.WasOursAsync(
                                 fromDiscord ? CopyDirections.ToDiscord : CopyDirections.ToVRChat,
                                 banning ? CopyKinds.Ban : CopyKinds.Unban,
                                 fact.SubjectId,
                                 null,
                                 fact.OccurredAt,
                                 ct).ConfigureAwait(false);

            // A removal cannot be undone, so a copy sent as a removal leaves no ban in Discord to
            // lift, and a later VRChat unban has nothing to copy.
            var nothingToLift = !fromDiscord
                                && !banning
                                && settings.DiscordBanCopyAction == DiscordBanCopyActions.Remove;

            if (wasTheBot || wasOurCopy)
                tally.Dropped++;

            if (wanted && !wasTheBot && !wasOurCopy && !nothingToLift)
            {
                var link = await LinkAsync(fromDiscord, fact.SubjectId, ct).ConfigureAwait(false);

                if (link is null)
                {
                    tally.NotLinked++;
                }
                else
                {
                    Add(changes, Plan(fromDiscord, banning, settings.DiscordBanCopyAction, link.Value, NameIn(fact.Data) ?? link.Value.Name, fact.Type));

                    await CopyAsync(
                            gateway, guildId, fromDiscord, banning, settings.DiscordBanCopyAction,
                            link.Value, fact.Id, fact.Type, tally, ct)
                        .ConfigureAwait(false);
                }
            }

            // Left where it was when a missing permission stopped the copy, so it is picked up
            // again the moment an operator fixes the permission.
            if (!tally.Stop)
                state.BansReadThrough = fact.Id;
        }

        state.BansReadAt = _clock.UtcNow;
        state.BansProblem = tally.Problem;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new BanSyncPass(tally.Copied, tally.Dropped, tally.NotLinked, tally.Left, tally.Problem, changes);
    }

    // ── The first run: what the two ban lists already disagree about ───────────────────────

    /// <summary>
    /// Compares the two ban lists and lists what copying them would do.
    /// </summary>
    /// <remarks>
    /// This is the blast radius M5 §7 asks to be shown before ban sync is switched on, and it is
    /// also the only way the backlog is ever dealt with: the pass above starts from the moment a
    /// direction was switched on, deliberately, so that turning a switch on cannot replay years of
    /// history as a flood of bans. Nothing here runs on a timer — an operator presses it.
    /// </remarks>
    /// <param name="apply">False changes nothing at all.</param>
    public async Task<SyncPreview> CatchUpAsync(IDiscordGateway? gateway, bool apply, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new
            {
                s.DiscordGuildId,
                s.ManagedGroupId,
                s.DiscordBanSyncToDiscord,
                s.DiscordBanSyncToVRChat,
                s.DiscordBanCopyAction,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (Blank(settings?.DiscordGuildId) is not { } guildId || Blank(settings?.ManagedGroupId) is not { } groupId)
            return new SyncPreview(0, [], null);

        var bannedInDiscord = gateway is null ? null : await gateway.ReadBansAsync(guildId, ct).ConfigureAwait(false);

        if (bannedInDiscord is null)
        {
            return new SyncPreview(0, [], gateway is null
                ? "The Discord bot is not connected, so its ban list could not be read."
                : "The bot could not read the Discord ban list. It needs Ban Members.");
        }

        var inDiscord = bannedInDiscord.ToHashSet(StringComparer.Ordinal);

        var links = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.UnlinkedAt == null)
            .Select(l => new { l.VRChatUserId, l.DiscordUserId, l.VRChatDisplayName, l.DiscordUsername })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bannedInGroup = await _db.GroupBans.AsNoTracking()
            .Where(b => b.GroupId == groupId && b.LiftedAt == null)
            .Select(b => b.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var inGroup = bannedInGroup.ToHashSet(StringComparer.Ordinal);

        var changes = new List<PlannedChange>();
        var total = 0;
        var done = 0;
        string? problem = null;

        foreach (var link in links)
        {
            var groupHas = inGroup.Contains(link.VRChatUserId);
            var serverHas = inDiscord.Contains(link.DiscordUserId);

            if (groupHas == serverHas)
                continue;

            // Only ever in the direction that is switched on, and only ever as a ban. A first run
            // never lifts anybody's ban: "this side has no ban" and "this side lifted the ban" look
            // identical before there is any history, and guessing would quietly free people.
            var toDiscord = groupHas && settings!.DiscordBanSyncToDiscord;
            var toVRChat = serverHas && settings!.DiscordBanSyncToVRChat;

            if (!toDiscord && !toVRChat)
                continue;

            total++;

            var pair = new Linked(link.VRChatUserId, link.DiscordUserId, link.VRChatDisplayName ?? link.DiscordUsername);
            var name = pair.Name;

            Add(changes, Plan(fromDiscord: toVRChat, banning: true, settings!.DiscordBanCopyAction, pair, name, cause: null));

            if (!apply || done >= MaxCatchUpPerRun)
                continue;

            var tally = new Tally();

            await CopyAsync(
                    gateway, guildId, fromDiscord: toVRChat, banning: true, settings.DiscordBanCopyAction,
                    pair, causedByFactId: null, causeType: null, tally, ct)
                .ConfigureAwait(false);

            done += tally.Copied;
            problem ??= tally.Problem;

            // A missing permission will refuse every remaining ban the same way. Stopping and
            // saying so once is more use than a hundred identical failures (M5 §7).
            if (tally.Stop)
                break;
        }

        return new SyncPreview(total, changes, problem);
    }

    // ── One copy ───────────────────────────────────────────────────────────────────────────

    private async Task CopyAsync(
        IDiscordGateway? gateway,
        string guildId,
        bool fromDiscord,
        bool banning,
        string copyAction,
        Linked link,
        long? causedByFactId,
        string? causeType,
        Tally tally,
        CancellationToken ct)
    {
        var removing = !fromDiscord && banning && copyAction == DiscordBanCopyActions.Remove;

        var kind = removing ? CopyKinds.Remove : banning ? CopyKinds.Ban : CopyKinds.Unban;
        var subjectId = fromDiscord ? link.VRChatUserId : link.DiscordUserId;
        var otherSideId = fromDiscord ? link.DiscordUserId : link.VRChatUserId;
        var direction = fromDiscord ? CopyDirections.ToVRChat : CopyDirections.ToDiscord;

        var reason = fromDiscord
            ? "Modbot: copied from a Discord ban"
            : "Modbot: copied from a ban in the VRChat group";

        // Written before the call, never after: this row is what stops the ban Modbot is about to
        // send from coming back round as a reason to ban again (M5 §4.2).
        var record = await _copies.StartAsync(direction, kind, subjectId, otherSideId, null, causedByFactId, ct)
            .ConfigureAwait(false);

        bool done;
        string? error;
        var nothingHappened = false;

        if (fromDiscord)
        {
            var outcome = banning
                ? await _vrchat.BanFromGroupAsync(link.VRChatUserId, reason, ct).ConfigureAwait(false)
                : await _vrchat.UnbanFromGroupAsync(link.VRChatUserId, reason, ct).ConfigureAwait(false);

            done = outcome.Done;
            error = outcome.Error;
        }
        else if (gateway is null)
        {
            done = false;
            error = "The Discord bot is not connected.";
        }
        else
        {
            var outcome = removing
                ? await gateway.RemoveAsync(guildId, link.DiscordUserId, reason, ct).ConfigureAwait(false)
                : banning
                    ? await gateway.BanAsync(guildId, link.DiscordUserId, reason, KeepsMessages, ct).ConfigureAwait(false)
                    : await gateway.UnbanAsync(guildId, link.DiscordUserId, reason, ct).ConfigureAwait(false);

            done = outcome.Done;
            error = outcome.Error;
            nothingHappened = outcome.NothingToDo;

            // A permission the bot does not hold refuses every one of these the same way, so the
            // caller is told to stop rather than earn a hundred identical refusals.
            if (outcome.NotAllowed)
                tally.Stop = true;
        }

        await _copies.FinishAsync(record, done, error, nothingHappened, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["direction"] = direction,
            ["kind"] = kind,
            ["vrchatUserId"] = link.VRChatUserId,
            ["discordUserId"] = link.DiscordUserId,
            ["reason"] = reason,
            ["causedBy"] = causedByFactId,
            ["causedByType"] = causeType,
            ["description"] = Describe(fromDiscord, banning, removing),
        };

        var platform = fromDiscord ? FactPlatform.VRChat : FactPlatform.Discord;

        if (done && !nothingHappened)
        {
            tally.Copied++;

            await WriteAsync(
                    removing ? FactType.CopiedRemove : banning ? FactType.CopiedBan : FactType.CopiedUnban,
                    platform, subjectId, data, ct)
                .ConfigureAwait(false);
        }
        else if (!done)
        {
            tally.Problem = error;
            data["error"] = error;
            await WriteAsync(FactType.CopyFailed, platform, subjectId, data, ct).ConfigureAwait(false);
        }
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private readonly record struct Linked(string VRChatUserId, string DiscordUserId, string? Name);

    private async Task<Linked?> LinkAsync(bool fromDiscord, string subjectId, CancellationToken ct)
    {
        var row = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.UnlinkedAt == null
                        && (fromDiscord ? l.DiscordUserId == subjectId : l.VRChatUserId == subjectId))
            .Select(l => new { l.VRChatUserId, l.DiscordUserId, l.VRChatDisplayName, l.DiscordUsername })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new Linked(row.VRChatUserId, row.DiscordUserId, row.VRChatDisplayName ?? row.DiscordUsername);
    }

    private static PlannedChange Plan(
        bool fromDiscord, bool banning, string copyAction, Linked link, string? name, string? cause)
    {
        var removing = !fromDiscord && banning && copyAction == DiscordBanCopyActions.Remove;

        return new PlannedChange(
            removing ? CopyKinds.Remove : banning ? CopyKinds.Ban : CopyKinds.Unban,
            fromDiscord ? SyncPlatforms.VRChat : SyncPlatforms.Discord,
            link.VRChatUserId,
            link.DiscordUserId,
            name,
            null,
            cause is null ? FirstRun(fromDiscord) : Describe(fromDiscord, banning, removing));
    }

    private static string FirstRun(bool fromDiscord)
        => fromDiscord
            ? "Banned in Discord and not in the group."
            : "Banned in the group and not in Discord.";

    private static string Describe(bool fromDiscord, bool banning, bool removing) => (fromDiscord, banning, removing) switch
    {
        (true, true, _) => "Banned in Discord, so banned in the group too.",
        (true, false, _) => "Unbanned in Discord, so unbanned in the group too.",
        (false, true, true) => "Banned in the group, so removed from the Discord server.",
        (false, true, false) => "Banned in the group, so banned in Discord too.",
        _ => "Unbanned in the group, so unbanned in Discord too.",
    };

    private static string? NameIn(string? data)
    {
        if (data is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<DiscordSyncState> StateAsync(CancellationToken ct)
    {
        var state = await _db.DiscordSyncState.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (state is null)
        {
            state = new DiscordSyncState { Id = 1 };
            _db.DiscordSyncState.Add(state);
        }

        return state;
    }

    private async Task WriteAsync(string type, FactPlatform platform, string subjectId, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = now,
            SubjectPlatform = platform,
            SubjectId = subjectId,
            Source = FactSource.Modbot,
            Data = data,
        }, ct).ConfigureAwait(false);
    }

    private static void Add(List<PlannedChange> changes, PlannedChange change)
    {
        if (changes.Count < MaxListed)
            changes.Add(change);
    }

    private static BanSyncPass Empty() => new(0, 0, 0, 0, null, []);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Tally
    {
        public int Copied;
        public int Dropped;
        public int NotLinked;
        public int Left;
        public string? Problem;

        /// <summary>Set when a permission is missing: every further copy would fail the same way.</summary>
        public bool Stop;
    }
}
