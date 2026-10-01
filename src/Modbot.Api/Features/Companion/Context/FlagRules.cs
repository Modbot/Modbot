using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Companion.Context;

/// <summary>
/// Which rules make a person Flagged, as this deployment has set them (flagged rules design §2-3).
/// </summary>
/// <remarks>
/// <para>
/// Stored on the settings row as sparse JSON (<c>Settings.FlagRules</c>), the way
/// <c>ReviewThresholds</c> is: a field that is absent takes the default below, so a deployment
/// that never opened the card is flagged by every rule at its default, and a later default reaches
/// it on upgrade.
/// </para>
/// <para>
/// Kicks and bans are on by default because they were the whole rule before this existed; a
/// deployment that upgrades sees every red row it saw yesterday, plus the new ones.
/// </para>
/// </remarks>
public sealed record FlagRuleSettings
{
    /// <summary>At least one kick from the group or ban.</summary>
    public bool KicksAndBans { get; init; } = true;

    /// <summary>
    /// How many days a ban still counts after it was lifted. Null means always, which is what the
    /// rule did before this setting existed and is still the default.
    /// </summary>
    /// <remarks>
    /// Only a lifted ban stops counting. A ban that stands, and a kick, count for as long as the
    /// rule is on: nobody undid them.
    /// </remarks>
    public int? LiftedBansForDays { get; init; }

    /// <summary>At least <see cref="WarnsAtLeast"/> instance warns.</summary>
    public bool Warns { get; init; } = true;

    public int WarnsAtLeast { get; init; } = 5;

    /// <summary>VRChat marks the account as a nuisance.</summary>
    public bool Nuisance { get; init; } = true;

    /// <summary>An AutoMod flag that nobody dismissed.</summary>
    public bool AutoMod { get; init; } = true;

    /// <summary>
    /// The AutoMod rules (term lists and topics, by id) whose flags count. Null means every rule,
    /// so a rule added later counts without anybody coming back to this card.
    /// </summary>
    public IReadOnlyList<Guid>? AutoModRules { get; init; }

    public const int MinWarns = 1;

    public const int MaxWarns = 99;

    public const int MinLiftedBanDays = 1;

    /// <summary>Ten years. Past this, "always" is the honest setting.</summary>
    public const int MaxLiftedBanDays = 3650;

    public static FlagRuleSettings Default { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads the stored document, defaults for anything missing, and clamps.</summary>
    public static FlagRuleSettings Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Default;

        try
        {
            return (JsonSerializer.Deserialize<FlagRuleSettings>(json, Json) ?? Default).Clamped();
        }
        catch (JsonException)
        {
            // A malformed row must not take out every roster; the defaults are the safe answer.
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public FlagRuleSettings Clamped() => this with
    {
        WarnsAtLeast = Math.Clamp(WarnsAtLeast, MinWarns, MaxWarns),
        LiftedBansForDays = LiftedBansForDays is { } days ? Math.Clamp(days, MinLiftedBanDays, MaxLiftedBanDays) : null,
        AutoModRules = AutoModRules?.Distinct().ToList(),
    };
}

/// <summary>
/// What is known against one person, and which of it the rules count.
/// </summary>
/// <param name="PriorActions">Kicks from the group and bans, counted whether or not that rule is on,
/// because the wire has always carried the number.</param>
/// <param name="Reasons">Each matched rule in plain words, in the order the settings card lists
/// them. Empty means not flagged.</param>
public sealed record FlagMatch(int PriorActions, IReadOnlyList<string> Reasons)
{
    public static FlagMatch None { get; } = new(0, []);

    /// <summary>
    /// The watch that stands on them, on their VRChat account or a Discord account linked to it.
    /// Null when nobody is watching them. What a watched person's arrival is told with.
    /// </summary>
    public WatchHit? Watch { get; init; }

    public bool IsFlagged => Reasons.Count > 0;

    /// <summary>Every reason on one line, for a join card.</summary>
    public string? Reason => IsFlagged ? string.Join(" · ", Reasons) : null;
}

/// <summary>A standing watch, as the flag rules found it.</summary>
/// <param name="Platform">Which account the watch is on. Discord when it was found through a link.</param>
/// <param name="SubjectId">That account's id.</param>
public sealed record WatchHit(Guid Id, FactPlatform Platform, string SubjectId, string Reason);

/// <summary>
/// Decides who is Flagged, for everybody who shows it: the companion roster and person card, the
/// live stream's flagged joins, the Live page and the old alert long poll.
/// </summary>
/// <remarks>
/// <para><strong>One place.</strong> Five readers used to count kicks and bans each for themselves.
/// With several rules and settings behind them, a roster that disagreed with the join card that
/// sent a moderator to it would be worse than either being wrong.</para>
/// <para><strong>From Modbot's own records only.</strong> No VRChat call is made: this runs for
/// every roster read and every page of the live stream, and a moderator glancing at a roster must
/// not be able to spend the group's shared API budget.</para>
/// <para><strong>One query per rule, for everybody at once.</strong> An instance can hold two
/// hundred and forty people.</para>
/// <para><strong>A watch is always a rule.</strong> It has no switch on the settings card: a watch
/// only exists because a moderator asked for exactly this, and a switch that ignored every watch
/// would be a second way of stopping them that nobody could see from the person.</para>
/// </remarks>
public static class FlagRules
{
    /// <summary>The facts that count as a kick or ban.</summary>
    private static readonly string[] KicksAndBans = [FactType.MemberKicked, FactType.MemberBanned];

    /// <summary>How much of a watch's reason a roster chip carries. The whole of it is on the popup.</summary>
    public const int WatchReasonOnAChip = 60;

    /// <summary>Reads the settings and decides every one of these people.</summary>
    /// <param name="ranks">The stored trust ranks the caller already read; a person missing from it
    /// has no rank on record.</param>
    /// <param name="now">Modbot's clock: a watch past its end day, and a ban lifted long enough
    /// ago, no longer count.</param>
    public static async Task<IReadOnlyDictionary<string, FlagMatch>> ReadAsync(
        ModbotContext db,
        IReadOnlyCollection<string> subjectIds,
        IReadOnlyDictionary<string, TrustRank?> ranks,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(subjectIds);
        ArgumentNullException.ThrowIfNull(ranks);

        if (subjectIds.Count == 0)
            return new Dictionary<string, FlagMatch>(StringComparer.Ordinal);

        var settings = FlagRuleSettings.Read(
            await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.FlagRules).FirstOrDefaultAsync(ct));

        return await ReadAsync(db, settings, subjectIds, ranks, now, ct);
    }

    /// <summary>The same, under settings the caller already has.</summary>
    public static async Task<IReadOnlyDictionary<string, FlagMatch>> ReadAsync(
        ModbotContext db,
        FlagRuleSettings settings,
        IReadOnlyCollection<string> subjectIds,
        IReadOnlyDictionary<string, TrustRank?> ranks,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(subjectIds);
        ArgumentNullException.ThrowIfNull(ranks);

        var result = new Dictionary<string, FlagMatch>(StringComparer.Ordinal);

        if (subjectIds.Count == 0)
            return result;

        var ids = subjectIds.Distinct(StringComparer.Ordinal).ToList();

        // Kicks, bans and warns in one grouped pass over the subject index. Kicks and bans are read
        // even with their rule off, because priorActions has always been on the wire.
        string[] counted = settings.Warns
            ? [.. KicksAndBans, FactType.GroupInstanceWarn]
            : KicksAndBans;

        var counts = await db.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                     && ids.Contains(e.SubjectId)
                     && counted.Contains(e.Type))
            .GroupBy(e => new { e.SubjectId, e.Type })
            .Select(g => new { g.Key.SubjectId, g.Key.Type, Count = g.Count() })
            .ToListAsync(ct);

        var priorActions = new Dictionary<string, int>(StringComparer.Ordinal);
        var warns = new Dictionary<string, int>(StringComparer.Ordinal);
        var banned = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in counts)
        {
            var into = row.Type == FactType.GroupInstanceWarn ? warns : priorActions;
            into[row.SubjectId] = into.GetValueOrDefault(row.SubjectId) + row.Count;

            if (row.Type == FactType.MemberBanned)
                banned.Add(row.SubjectId);
        }

        // Only asked when the window is set and somebody here was ever banned, so a deployment that
        // never touched the setting pays nothing for it.
        var liftedTooLongAgo = settings.KicksAndBans && settings.LiftedBansForDays is { } days && banned.Count > 0
            ? await LiftedBansOutsideAsync(db, banned.ToList(), now - TimeSpan.FromDays(days), ct)
            : new Dictionary<string, int>(StringComparer.Ordinal);

        // The Discord accounts linked to these people, read once for the two rules that follow a
        // link: most of what AutoMod reads is Discord messages, and a watch may be set on somebody
        // Modbot first met in Discord.
        var links = await db.DiscordAccountLinks
            .AsNoTracking()
            .Where(l => l.UnlinkedAt == null && ids.Contains(l.VRChatUserId))
            .Select(l => new Link(l.DiscordUserId, l.VRChatUserId))
            .ToListAsync(ct);

        var watches = await WatchesAsync(db, ids, links, now, ct);

        var autoMod = settings.AutoMod
            ? await AutoModRulesAsync(db, settings.AutoModRules, ids, links, ct)
            : new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var actions = priorActions.GetValueOrDefault(id);
            var countedActions = actions - liftedTooLongAgo.GetValueOrDefault(id);
            var reasons = new List<string>();
            var watch = watches.GetValueOrDefault(id);

            // First, because a moderator chose to say it, and a roster chip or a join card cut
            // short must still show it.
            if (watch is not null)
                reasons.Add("Watched: " + Shortened(watch.Reason, WatchReasonOnAChip));

            if (settings.KicksAndBans && countedActions > 0)
                reasons.Add(countedActions == 1 ? "1 kick or ban" : $"{countedActions} kicks or bans");

            if (settings.Warns && warns.GetValueOrDefault(id) is var warned && warned >= settings.WarnsAtLeast)
                reasons.Add(warned == 1 ? "1 warn" : $"{warned} warns");

            if (settings.Nuisance && ranks.GetValueOrDefault(id) == TrustRank.Nuisance)
                reasons.Add(TrustRanks.Name(TrustRank.Nuisance));

            if (autoMod.TryGetValue(id, out var rules) && rules.Count > 0)
                reasons.Add("AutoMod: " + string.Join(", ", rules));

            result[id] = new FlagMatch(actions, reasons) { Watch = watch };
        }

        return result;
    }

    /// <summary>One active link between a Discord account and a VRChat one.</summary>
    private sealed record Link(string DiscordUserId, string VRChatUserId);

    /// <summary>
    /// How many of each person's bans were lifted before <paramref name="cutoff"/>, and so no
    /// longer count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the ban and unban facts, in order: an unban lifts every ban before it that is not
    /// lifted yet. A ban with no unban after it stands, and always counts.
    /// </para>
    /// <para>
    /// Facts rather than the ban list's own lifted day, because the rule counts facts. The ban list
    /// keeps one row per person, so a person banned, unbanned and banned again would be one row
    /// against two counted bans.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<string, int>> LiftedBansOutsideAsync(
        ModbotContext db,
        List<string> ids,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        var rows = await db.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                     && ids.Contains(e.SubjectId)
                     && (e.Type == FactType.MemberBanned || e.Type == FactType.MemberUnbanned))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new { e.SubjectId, e.Type, e.OccurredAt })
            .ToListAsync(ct);

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var waiting = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.Type == FactType.MemberBanned)
            {
                waiting[row.SubjectId] = waiting.GetValueOrDefault(row.SubjectId) + 1;
                continue;
            }

            var lifted = waiting.GetValueOrDefault(row.SubjectId);
            waiting[row.SubjectId] = 0;

            if (lifted > 0 && row.OccurredAt < cutoff)
                result[row.SubjectId] = result.GetValueOrDefault(row.SubjectId) + lifted;
        }

        return result;
    }

    /// <summary>
    /// The watch that stands on each person: on their VRChat account, or on a Discord account
    /// linked to it. Where there are both, the VRChat one, as the account the roster is about.
    /// </summary>
    private static async Task<Dictionary<string, WatchHit>> WatchesAsync(
        ModbotContext db,
        List<string> ids,
        List<Link> links,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var discordIds = links.Select(l => l.DiscordUserId).Distinct(StringComparer.Ordinal).ToList();

        var found = await db.PersonWatches
            .AsNoTracking()
            .Where(w => w.EndedAt == null && (w.EndsAt == null || w.EndsAt > now))
            .Where(w => (w.SubjectPlatform == FactPlatform.VRChat && ids.Contains(w.SubjectId))
                     || (w.SubjectPlatform == FactPlatform.Discord && discordIds.Contains(w.SubjectId)))
            .Select(w => new WatchHit(w.Id, w.SubjectPlatform, w.SubjectId, w.Reason))
            .ToListAsync(ct);

        var result = new Dictionary<string, WatchHit>(StringComparer.Ordinal);

        foreach (var watch in found.OrderBy(w => w.Platform == FactPlatform.VRChat ? 0 : 1))
        {
            var people = watch.Platform == FactPlatform.VRChat
                ? [watch.SubjectId]
                : links.Where(l => l.DiscordUserId == watch.SubjectId).Select(l => l.VRChatUserId);

            foreach (var person in people)
                result.TryAdd(person, watch);
        }

        return result;
    }

    /// <summary>
    /// The names of the AutoMod rules with a flag that still stands against each person: on their
    /// VRChat account, or on a Discord account linked to it.
    /// </summary>
    /// <remarks>
    /// A dismissed flag is a moderator saying the rule was wrong, so it never counts. A flag from a
    /// rule on trial is the rule being tried out, not trusted yet, so it does not either. Most of
    /// what AutoMod reads is Discord messages, which is why a linked Discord account is followed.
    /// </remarks>
    private static async Task<Dictionary<string, SortedSet<string>>> AutoModRulesAsync(
        ModbotContext db,
        IReadOnlyList<Guid>? onlyRules,
        List<string> ids,
        List<Link> links,
        CancellationToken ct)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        if (onlyRules is { Count: 0 })
            return result;

        var discordIds = links.Select(l => l.DiscordUserId).Distinct(StringComparer.Ordinal).ToList();

        var flags = db.ModerationFlags
            .AsNoTracking()
            .Where(f => f.State != ModerationFlagState.Dismissed && !f.Trial)
            .Where(f => (f.SubjectPlatform == FactPlatform.VRChat && ids.Contains(f.SubjectId))
                     || (f.SubjectPlatform == FactPlatform.Discord && discordIds.Contains(f.SubjectId)));

        if (onlyRules is not null)
            flags = flags.Where(f => onlyRules.Contains(f.RuleId));

        var found = await flags
            .Select(f => new { f.SubjectPlatform, f.SubjectId, f.RuleName })
            .Distinct()
            .ToListAsync(ct);

        foreach (var flag in found)
        {
            var people = flag.SubjectPlatform == FactPlatform.VRChat
                ? [flag.SubjectId]
                : links.Where(l => l.DiscordUserId == flag.SubjectId).Select(l => l.VRChatUserId);

            foreach (var person in people)
            {
                if (!result.TryGetValue(person, out var names))
                    result[person] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                names.Add(flag.RuleName);
            }
        }

        return result;
    }

    /// <summary>The first <paramref name="limit"/> characters, with an ellipsis when there were more.</summary>
    private static string Shortened(string text, int limit)
        => text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";
}
