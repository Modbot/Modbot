using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Time;

namespace Modbot.Analytics.Giveaways;

/// <summary>What the rules said about one person.</summary>
/// <param name="Met">Whether they pass.</param>
/// <param name="FromPolledData">A rule about them was answered from polled presence reports.</param>
/// <param name="CloseCall">A measurement of theirs sat within a whisker of a threshold.</param>
/// <param name="Because">The rule they failed, in plain words. Null when they passed.</param>
public readonly record struct GiveawayRuleAnswer(bool Met, bool FromPolledData, bool CloseCall, string? Because);

/// <summary>One person, as the preview and the snapshot both list them.</summary>
public sealed record GiveawayListing(
    string Key,
    string? VRChatUserId,
    string? DiscordUserId,
    string? Name,
    long Weight,
    decimal Measured,
    string KeptOut,
    string? Because,
    bool FromPolledData,
    bool CloseCall);

/// <summary>
/// Who would be in a giveaway right now, and why everybody else would not.
/// </summary>
/// <param name="People">The people, in snapshot order. Trimmed to the page asked for on a preview.</param>
/// <param name="Total">How many people were looked at.</param>
/// <param name="InDraw">How many of them would be in the hat.</param>
/// <param name="TotalWeight">Their weights added up.</param>
/// <param name="CloseCalls">How many sat within a whisker of a threshold on an approximate rule.</param>
/// <param name="FromPolledData">Any rule or weight here was answered from polled presence reports.</param>
/// <param name="Unanswerable">Why no answer could be given at all. Everything else is empty when set.</param>
/// <param name="Stopped">Evaluation gave up: more people than <see cref="GiveawayRuleChecker.MaxPeople"/>.</param>
public sealed record GiveawayMatch(
    IReadOnlyList<GiveawayListing> People,
    int Total,
    int InDraw,
    long TotalWeight,
    int CloseCalls,
    bool FromPolledData,
    string? Unanswerable = null,
    bool Stopped = false)
{
    public static GiveawayMatch Nothing { get; } = new([], 0, 0, 0, 0, false);

    public static GiveawayMatch CannotAnswer(string why) => new([], 0, 0, 0, 0, false, why);
}

/// <summary>
/// Reads the rules against the data and says who is in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The evaluation is bounded and cancellable</strong> (M7 §5). The candidate list is
/// capped at <see cref="MaxPeople"/> and the run stops and says so rather than holding a
/// connection until something times out; the token is passed into every query, so a moderator who
/// closes the page stops the work.
/// </para>
/// <para>
/// <strong>The count is worked out separately from the page.</strong> A preview says how many
/// people match without the page having to hold every one of them, which is what §5 asks for and
/// what keeps the rule builder usable on a group with thousands of members.
/// </para>
/// <para>
/// This class knows nothing about draws or prizes. It answers "who matches these rules", which is
/// the question M7 §2's segment builder asks too (giveaways design §2.6).
/// </para>
/// </remarks>
public sealed class GiveawayRuleChecker
{
    /// <summary>
    /// The most people one run will look at.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a guess at what is reasonable: a group large enough to pass it has an
    /// answer that no page could show anyway, and stopping with a sentence is better than a
    /// request that never comes back.
    /// </remarks>
    public const int MaxPeople = 50_000;

    /// <summary>How many people a preview page holds.</summary>
    public const int PageSize = 50;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public GiveawayRuleChecker(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Everybody who matches, for the preview. <paramref name="page"/> people come back; the
    /// counts are over everybody.
    /// </summary>
    public Task<GiveawayMatch> PreviewAsync(
        GiveawayRule rule,
        GiveawayExclusions exclusions,
        string weighting,
        long? weightCap,
        int page = PageSize,
        CancellationToken ct = default)
        => RunAsync(rule, exclusions, weighting, weightCap, entrants: null, page, ct);

    /// <summary>
    /// The whole list, for a draw's snapshot: everybody who was considered, in order, with their
    /// weight and the reason anybody is out.
    /// </summary>
    /// <param name="entrants">
    /// Discord ids of the people who reacted, for a giveaway entered by reacting. Null means
    /// everybody who matches is in, which is what automatic entry means.
    /// </param>
    public Task<GiveawayMatch> SnapshotAsync(
        GiveawayRule rule,
        GiveawayExclusions exclusions,
        string weighting,
        long? weightCap,
        IReadOnlyDictionary<string, bool>? entrants,
        CancellationToken ct = default)
        => RunAsync(rule, exclusions, weighting, weightCap, entrants, page: int.MaxValue, ct);

    /// <summary>
    /// Whether one person passes the rules right now — what a reaction is checked against as it
    /// arrives (giveaways design §4.2).
    /// </summary>
    public async Task<GiveawayRuleAnswer> CheckOneAsync(
        GiveawayRule rule, string discordUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrEmpty(discordUserId);

        var coverage = await GiveawayCoverage.ReadAsync(_db, ct);

        if (coverage.WhyUnanswerable(rule) is { } why)
            return new GiveawayRuleAnswer(false, false, false, why);

        var person = await OneAsync(discordUserId, ct);
        if (person is null)
            return new GiveawayRuleAnswer(false, false, false, "Modbot does not know that Discord account.");

        var now = _clock.UtcNow;
        var measures = new GiveawayMeasures();
        var needed = GiveawayMeasures.Needed(rule, GiveawayWeights.Uniform);
        await measures.CountAsync(_db, [person], needed, now, ct);

        return Check(rule, person, measures, now);
    }

    private async Task<GiveawayMatch> RunAsync(
        GiveawayRule rule,
        GiveawayExclusions exclusions,
        string weighting,
        long? weightCap,
        IReadOnlyDictionary<string, bool>? entrants,
        int page,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(exclusions);

        var coverage = await GiveawayCoverage.ReadAsync(_db, ct);

        if (coverage.WhyUnanswerable(rule) is { } ruleProblem)
            return GiveawayMatch.CannotAnswer(ruleProblem);

        if (coverage.WhyWeightUnanswerable(weighting) is { } weightProblem)
            return GiveawayMatch.CannotAnswer(weightProblem);

        var now = _clock.UtcNow;
        var people = await CandidatesAsync(entrants, exclusions, ct);

        if (people.Count > MaxPeople)
        {
            return new GiveawayMatch(
                [], people.Count, 0, 0, 0, false,
                $"That is {people.Count:N0} people, and Modbot looks at {MaxPeople:N0} at a time. Narrow the rules.",
                Stopped: true);
        }

        var measures = new GiveawayMeasures();
        await measures.CountAsync(_db, people, GiveawayMeasures.Needed(rule, weighting), now, ct);

        var listings = new List<GiveawayListing>(people.Count);
        var inDraw = 0;
        long totalWeight = 0;
        var closeCalls = 0;
        var polled = false;

        foreach (var person in people.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            var keptOut = KeptOutBy(person, exclusions, entrants);
            var answer = keptOut.Length == 0
                ? Check(rule, person, measures, now)
                : new GiveawayRuleAnswer(false, false, false, null);

            if (keptOut.Length == 0 && !answer.Met)
                keptOut = GiveawayKeptOut.Rules;

            var measured = Measured(weighting, person, measures);
            var weight = keptOut.Length == 0 ? GiveawayWeight.Of(measured, weightCap) : 0;

            var fromPolled = answer.FromPolledData || GiveawayWeights.IsApproximate(weighting);
            polled |= fromPolled;

            if (answer.CloseCall)
                closeCalls++;

            if (keptOut.Length == 0)
            {
                inDraw++;
                totalWeight += weight;
            }

            listings.Add(new GiveawayListing(
                person.Key,
                person.VRChatUserId,
                person.DiscordUserId,
                person.Name,
                weight,
                measured,
                keptOut,
                answer.Because,
                fromPolled,
                answer.CloseCall));
        }

        // The page is a window on the list; the counts above are over every one of them, which is
        // what lets a preview say "312 match" without holding 312 rows (M7 §5).
        var shown = page >= listings.Count
            ? listings
            : [.. listings.Where(l => l.KeptOut.Length == 0).Concat(listings.Where(l => l.KeptOut.Length > 0)).Take(page)];

        return new GiveawayMatch(shown, listings.Count, inDraw, totalWeight, closeCalls, polled);
    }

    /// <summary>
    /// Why this person cannot win, before the rules are asked. Empty means nothing keeps them out.
    /// </summary>
    /// <remarks>
    /// Exclusions are checked ahead of the rules so the snapshot says the real reason. "Staff" and
    /// "does not pass the rules" are different answers to "why am I not in this", and the first one
    /// is the one a person deserves.
    /// </remarks>
    private static string KeptOutBy(
        GiveawayCandidate person, GiveawayExclusions exclusions, IReadOnlyDictionary<string, bool>? entrants)
    {
        // A withdrawn entry stays in the snapshot rather than vanishing from it, because a list
        // that silently lost somebody who did enter is exactly the thing §5.1 is for.
        if (entrants is not null
            && (person.DiscordUserId is not { } id || !entrants.TryGetValue(id, out var standing) || !standing))
        {
            return GiveawayKeptOut.Withdrawn;
        }

        if (exclusions.Staff && person.Staff)
            return GiveawayKeptOut.Staff;

        if (exclusions.PastWinners && person.WonBefore)
            return GiveawayKeptOut.PastWinner;

        if (exclusions.BannedMembers && person.Banned)
            return GiveawayKeptOut.Banned;

        return exclusions.People.Any(person.Is) ? GiveawayKeptOut.Named : GiveawayKeptOut.No;
    }

    private static decimal Measured(string weighting, GiveawayCandidate person, GiveawayMeasures measures)
    {
        if (weighting == GiveawayWeights.Uniform)
            return 1m;

        var kind = weighting switch
        {
            GiveawayWeights.InstanceHours => GiveawayRuleKinds.InstanceHours,
            GiveawayWeights.VoiceHours => GiveawayRuleKinds.VoiceHours,
            GiveawayWeights.Messages => GiveawayRuleKinds.Messages,
            _ => GiveawayWeights.DaysSeen,
        };

        return measures.Of(new GiveawayMeasure(kind, null), person);
    }

    /// <summary>Walks the rule tree for one person.</summary>
    internal static GiveawayRuleAnswer Check(
        GiveawayRule rule, GiveawayCandidate person, GiveawayMeasures measures, DateTimeOffset now)
    {
        switch (rule.Kind)
        {
            case GiveawayRuleKinds.AllOf:
            {
                var polled = false;
                var close = false;

                foreach (var inner in rule.Rules)
                {
                    var answer = Check(inner, person, measures, now);
                    polled |= answer.FromPolledData;
                    close |= answer.CloseCall;

                    if (!answer.Met)
                        return new GiveawayRuleAnswer(false, polled, close, answer.Because ?? Describe(inner));
                }

                return new GiveawayRuleAnswer(true, polled, close, null);
            }

            case GiveawayRuleKinds.AnyOf:
            {
                // An empty "any of" lets nobody through, which is what "at least one of nothing"
                // means. The builder never makes one; a hand-written rule tree can.
                var polled = false;
                var close = false;

                foreach (var inner in rule.Rules)
                {
                    var answer = Check(inner, person, measures, now);
                    polled |= answer.FromPolledData;
                    close |= answer.CloseCall;

                    if (answer.Met)
                        return new GiveawayRuleAnswer(true, polled, close, null);
                }

                return new GiveawayRuleAnswer(
                    rule.Rules.Count == 0, polled, close, rule.Rules.Count == 0 ? null : Describe(rule));
            }

            case GiveawayRuleKinds.NoneOf:
            {
                var polled = false;
                var close = false;

                foreach (var inner in rule.Rules)
                {
                    var answer = Check(inner, person, measures, now);
                    polled |= answer.FromPolledData;
                    close |= answer.CloseCall;

                    if (answer.Met)
                        return new GiveawayRuleAnswer(false, polled, close, Describe(rule));
                }

                return new GiveawayRuleAnswer(true, polled, close, null);
            }
        }

        return One(rule, person, measures, now);
    }

    private static GiveawayRuleAnswer One(
        GiveawayRule rule, GiveawayCandidate person, GiveawayMeasures measures, DateTimeOffset now)
    {
        var threshold = rule.Amount ?? 0m;

        switch (rule.Kind)
        {
            case GiveawayRuleKinds.DiscordMemberDays:
                return Plain(person.InDiscord && DaysSince(person.DiscordJoinedAt, now) >= threshold, rule);

            case GiveawayRuleKinds.GroupMemberDays:
                return Plain(person.InGroup && DaysSince(person.GroupJoinedAt, now) >= threshold, rule);

            case GiveawayRuleKinds.InGroup:
                return Plain(person.InGroup, rule);

            case GiveawayRuleKinds.LinkedAccounts:
                return Plain(person.Linked, rule);

            case GiveawayRuleKinds.GroupRole:
                return Plain(rule.Id is { } groupRole && person.GroupRoles.Contains(groupRole, StringComparer.Ordinal), rule);

            case GiveawayRuleKinds.DiscordRole:
                return Plain(rule.Id is { } discordRole && person.DiscordRoles.Contains(discordRole, StringComparer.Ordinal), rule);

            case GiveawayRuleKinds.VRChatAccountDays:
            {
                if (person.VRChatJoined is not { } joined)
                    return new GiveawayRuleAnswer(false, false, false, Describe(rule));

                var age = (decimal)(now - new DateTimeOffset(joined.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)).TotalDays;
                return Plain(age >= threshold, rule);
            }

            case GiveawayRuleKinds.SeenWithinDays:
            {
                // The measurement is "how many days since they were last seen"; nought means the
                // presence log has nothing about them at all, which is not "seen today".
                var since = measures.Of(new GiveawayMeasure(rule.Kind, null), person);
                var ever = since > 0m;
                var met = ever && since <= threshold;

                return new GiveawayRuleAnswer(
                    met, true, ever && GiveawayMeasures.IsCloseCall(since, threshold), met ? null : Describe(rule));
            }

            case GiveawayRuleKinds.NoTrouble:
            {
                var trouble = measures.TroubleOf(new GiveawayMeasure(rule.Kind, rule.WithinDays), person);
                return Plain(trouble == 0m, rule);
            }

            case GiveawayRuleKinds.InstanceHours:
            case GiveawayRuleKinds.OneInstanceHours:
            case GiveawayRuleKinds.VoiceHours:
            case GiveawayRuleKinds.Messages:
            {
                var measure = new GiveawayMeasure(rule.Kind, rule.WithinDays);
                var value = measures.Of(measure, person);

                return new GiveawayRuleAnswer(
                    value >= threshold,
                    measure.FromPolledData,
                    measure.FromPolledData && GiveawayMeasures.IsCloseCall(value, threshold),
                    value >= threshold ? null : Describe(rule));
            }
        }

        // A kind that got past validation and has no check here lets nobody through, on purpose:
        // a rule Modbot does not understand must not quietly mean "everybody".
        return new GiveawayRuleAnswer(false, false, false, $"Modbot does not know the rule '{rule.Kind}'.");
    }

    private static GiveawayRuleAnswer Plain(bool met, GiveawayRule rule)
        => new(met, false, false, met ? null : Describe(rule));

    private static string Describe(GiveawayRule rule) => GiveawayRules.Describe(rule);

    private static decimal DaysSince(DateTimeOffset? at, DateTimeOffset now)
        => at is { } when ? (decimal)(now - when).TotalDays : -1m;

    // ── Who is even considered ───────────────────────────────────────────────────────────

    /// <summary>
    /// Everybody a giveaway could let in, merged across the two sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a giveaway entered by reacting, the candidates are the people who reacted and nobody
    /// else. For an automatic one they are the group's members and the server's members together,
    /// joined by whatever links exist — which is the smallest population that can answer a rule
    /// about either side.
    /// </para>
    /// <para>
    /// Members who have left are left out. A giveaway for people who are not here any more is not
    /// a thing anybody means; the <c>inGroup</c> rule is about the VRChat group specifically, and
    /// this is about being reachable at all.
    /// </para>
    /// </remarks>
    private async Task<List<GiveawayCandidate>> CandidatesAsync(
        IReadOnlyDictionary<string, bool>? entrants, GiveawayExclusions exclusions, CancellationToken ct)
    {
        var links = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.UnlinkedAt == null)
            .Select(l => new { l.DiscordUserId, l.VRChatUserId })
            .ToListAsync(ct);

        var vrchatByDiscord = links.ToDictionary(l => l.DiscordUserId, l => l.VRChatUserId, StringComparer.Ordinal);
        var discordByVRChat = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var link in links)
            discordByVRChat[link.VRChatUserId] = link.DiscordUserId;

        var byKey = new Dictionary<string, GiveawayCandidate>(StringComparer.Ordinal);

        // ── The Discord side ──
        var reacted = entrants?.Keys.ToArray();

        var discordMembers = await _db.DiscordMembers.AsNoTracking()
            .Where(m => !m.IsBot && (reacted != null ? reacted.Contains(m.UserId) : m.LeftAt == null))
            .Select(m => new { m.UserId, m.DisplayName, m.JoinedAt, m.Roles, m.LeftAt })
            .Take(MaxPeople + 1)
            .ToListAsync(ct);

        foreach (var member in discordMembers)
        {
            var vrchat = vrchatByDiscord.GetValueOrDefault(member.UserId);
            var person = new GiveawayCandidate
            {
                DiscordUserId = member.UserId,
                VRChatUserId = vrchat,
                Name = member.DisplayName,
                InDiscord = member.LeftAt is null,
                DiscordJoinedAt = member.JoinedAt,
                DiscordRoles = Ids(member.Roles),
                Linked = vrchat is not null,
            };

            byKey[person.Key] = person;
        }

        // Somebody reacted whom Modbot has no member row for. They still enter; whether they
        // qualify is up to the rules, and a rule needing VRChat data simply fails for them.
        if (reacted is not null)
        {
            foreach (var id in reacted)
            {
                var vrchat = vrchatByDiscord.GetValueOrDefault(id);
                var key = vrchat is null ? $"discord:{id}" : $"vrchat:{vrchat}";

                if (byKey.ContainsKey(key))
                    continue;

                byKey[key] = new GiveawayCandidate
                {
                    DiscordUserId = id,
                    VRChatUserId = vrchat,
                    Linked = vrchat is not null,
                };
            }
        }

        // ── The VRChat side ──
        if (reacted is null)
        {
            var members = await _db.GroupMembers.AsNoTracking()
                .Where(m => m.LeftAt == null)
                .Select(m => new { m.UserId, m.JoinedAt, m.Roles })
                .Take(MaxPeople + 1)
                .ToListAsync(ct);

            foreach (var member in members)
            {
                var key = $"vrchat:{member.UserId}";

                if (!byKey.TryGetValue(key, out var person))
                {
                    person = new GiveawayCandidate
                    {
                        VRChatUserId = member.UserId,
                        DiscordUserId = discordByVRChat.GetValueOrDefault(member.UserId),
                    };

                    person.Linked = person.DiscordUserId is not null;
                    byKey[key] = person;
                }

                person.InGroup = true;
                person.GroupJoinedAt = member.JoinedAt;
                person.GroupRoles = Ids(member.Roles);
            }
        }
        else
        {
            var wanted = byKey.Values.Where(p => p.VRChatUserId is not null).Select(p => p.VRChatUserId!).ToArray();

            var members = await _db.GroupMembers.AsNoTracking()
                .Where(m => m.LeftAt == null && wanted.Contains(m.UserId))
                .Select(m => new { m.UserId, m.JoinedAt, m.Roles })
                .ToListAsync(ct);

            foreach (var member in members)
            {
                if (!byKey.TryGetValue($"vrchat:{member.UserId}", out var person))
                    continue;

                person.InGroup = true;
                person.GroupJoinedAt = member.JoinedAt;
                person.GroupRoles = Ids(member.Roles);
            }
        }

        var people = byKey.Values.ToList();

        if (people.Count > MaxPeople)
            return people;

        await FillAsync(people, exclusions, ct);
        return people;
    }

    private async Task<GiveawayCandidate?> OneAsync(string discordUserId, CancellationToken ct)
    {
        var entrants = new Dictionary<string, bool>(StringComparer.Ordinal) { [discordUserId] = true };
        var people = await CandidatesAsync(entrants, GiveawayExclusions.None, ct);

        return people.FirstOrDefault(p => p.DiscordUserId == discordUserId);
    }

    /// <summary>The names, the account ages, the bans, and who is staff or has won before.</summary>
    private async Task FillAsync(
        List<GiveawayCandidate> people, GiveawayExclusions exclusions, CancellationToken ct)
    {
        var vrchatIds = people.Where(p => p.VRChatUserId is not null).Select(p => p.VRChatUserId!).Distinct().ToArray();

        if (vrchatIds.Length > 0)
        {
            var users = await _db.VRChatUsers.AsNoTracking()
                .Where(u => vrchatIds.Contains(u.UserId))
                .Select(u => new { u.UserId, u.DisplayName, u.DateJoined })
                .ToListAsync(ct);

            var byId = users.ToDictionary(u => u.UserId, StringComparer.Ordinal);

            foreach (var person in people)
            {
                if (person.VRChatUserId is { } id && byId.TryGetValue(id, out var user))
                {
                    person.VRChatJoined = user.DateJoined;

                    // The VRChat name wins: a giveaway is about the group, and that is the name
                    // the group knows them by.
                    if (!string.IsNullOrWhiteSpace(user.DisplayName))
                        person.Name = user.DisplayName;
                }
            }

            if (exclusions.BannedMembers)
            {
                var banned = await _db.GroupBans.AsNoTracking()
                    .Where(b => b.LiftedAt == null && vrchatIds.Contains(b.UserId))
                    .Select(b => b.UserId)
                    .ToListAsync(ct);

                var bannedSet = banned.ToHashSet(StringComparer.Ordinal);

                foreach (var person in people.Where(p => p.VRChatUserId is not null))
                    person.Banned = bannedSet.Contains(person.VRChatUserId!);
            }
        }

        if (exclusions.Staff)
        {
            var staff = await _db.Users.AsNoTracking()
                .Select(u => new { u.VRChatUserId, u.DiscordUserId })
                .ToListAsync(ct);

            var staffIds = staff
                .SelectMany(s => new[] { s.VRChatUserId, s.DiscordUserId })
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var person in people)
                person.Staff = (person.VRChatUserId is { } v && staffIds.Contains(v))
                    || (person.DiscordUserId is { } d && staffIds.Contains(d));
        }

        if (exclusions.PastWinners)
        {
            var keys = people.Select(p => p.Key).ToArray();

            var won = await _db.GiveawayEntrants.AsNoTracking()
                .Where(e => e.WinnerRank != null && keys.Contains(e.Key))
                .Select(e => e.Key)
                .Distinct()
                .ToListAsync(ct);

            var winners = won.ToHashSet(StringComparer.Ordinal);

            foreach (var person in people)
                person.WonBefore = winners.Contains(person.Key);
        }
    }

    /// <summary>The ids in a stored <c>jsonb</c> array of strings.</summary>
    private static IReadOnlyList<string> Ids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
