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

    public bool IsFlagged => Reasons.Count > 0;

    /// <summary>Every reason on one line, for a join card.</summary>
    public string? Reason => IsFlagged ? string.Join(" · ", Reasons) : null;
}

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
/// </remarks>
public static class FlagRules
{
    /// <summary>The facts that count as a kick or ban.</summary>
    private static readonly string[] KicksAndBans = [FactType.MemberKicked, FactType.MemberBanned];

    /// <summary>Reads the settings and decides every one of these people.</summary>
    /// <param name="ranks">The stored trust ranks the caller already read; a person missing from it
    /// has no rank on record.</param>
    public static async Task<IReadOnlyDictionary<string, FlagMatch>> ReadAsync(
        ModbotContext db,
        IReadOnlyCollection<string> subjectIds,
        IReadOnlyDictionary<string, TrustRank?> ranks,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(subjectIds);
        ArgumentNullException.ThrowIfNull(ranks);

        if (subjectIds.Count == 0)
            return new Dictionary<string, FlagMatch>(StringComparer.Ordinal);

        var settings = FlagRuleSettings.Read(
            await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.FlagRules).FirstOrDefaultAsync(ct));

        return await ReadAsync(db, settings, subjectIds, ranks, ct);
    }

    /// <summary>The same, under settings the caller already has.</summary>
    public static async Task<IReadOnlyDictionary<string, FlagMatch>> ReadAsync(
        ModbotContext db,
        FlagRuleSettings settings,
        IReadOnlyCollection<string> subjectIds,
        IReadOnlyDictionary<string, TrustRank?> ranks,
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

        foreach (var row in counts)
        {
            var into = row.Type == FactType.GroupInstanceWarn ? warns : priorActions;
            into[row.SubjectId] = into.GetValueOrDefault(row.SubjectId) + row.Count;
        }

        var autoMod = settings.AutoMod
            ? await AutoModRulesAsync(db, settings.AutoModRules, ids, ct)
            : new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var actions = priorActions.GetValueOrDefault(id);
            var reasons = new List<string>();

            if (settings.KicksAndBans && actions > 0)
                reasons.Add(actions == 1 ? "1 kick or ban" : $"{actions} kicks or bans");

            if (settings.Warns && warns.GetValueOrDefault(id) is var warned && warned >= settings.WarnsAtLeast)
                reasons.Add(warned == 1 ? "1 warn" : $"{warned} warns");

            if (settings.Nuisance && ranks.GetValueOrDefault(id) == TrustRank.Nuisance)
                reasons.Add(TrustRanks.Name(TrustRank.Nuisance));

            if (autoMod.TryGetValue(id, out var rules) && rules.Count > 0)
                reasons.Add("AutoMod: " + string.Join(", ", rules));

            result[id] = new FlagMatch(actions, reasons);
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
        CancellationToken ct)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        if (onlyRules is { Count: 0 })
            return result;

        var links = await db.DiscordAccountLinks
            .AsNoTracking()
            .Where(l => l.UnlinkedAt == null && ids.Contains(l.VRChatUserId))
            .Select(l => new { l.DiscordUserId, l.VRChatUserId })
            .ToListAsync(ct);

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
}
