using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Modbot.AI.Usage;
using Serilog;

namespace Modbot.AI.Moderation;

/// <summary>What "Try it" answers: every rule that matched, and what would happen.</summary>
/// <param name="Matches">Rules that are switched off are included, marked by <see cref="TryMatch.RuleEnabled"/>.</param>
public sealed record TryResult(
    IReadOnlyList<TryMatch> Matches,
    bool WouldDeleteMessage,
    int? WouldTimeOutMinutes,
    string? AiSkipped);

public sealed record TryMatch(ModerationMatch Match, bool RuleEnabled);

/// <summary>
/// Compiled term lists, kept between checks. A list is compiled again only when its row changes.
/// </summary>
/// <remarks>
/// Singleton. Compiling patterns on every Discord message would cost more than matching them.
/// </remarks>
public sealed class CompiledTermLists
{
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset UpdatedAt, CompiledTermList List)> _lists = new();

    public CompiledTermList For(ModerationTermList row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_lists.TryGetValue(row.Id, out var cached) && cached.UpdatedAt == row.UpdatedAt)
            return cached.List;

        var compiled = TermMatcher.Compile(StoredTerm.ParseList(row.Terms), StoredTerm.ParseIds(row.ExcludedTerms));
        _lists[row.Id] = (row.UpdatedAt, compiled);
        return compiled;
    }
}

/// <summary>
/// The AI moderation engine (AI moderation design §4 to §8, §12 to §14).
/// </summary>
public sealed class ModerationEngine : IModerationChecker
{
    private readonly ModbotContext _db;
    private readonly IAiClients _ai;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly IDiscordModerationActions _discord;
    private readonly CompiledTermLists _compiled;
    private readonly IAiUsage _usage;
    private readonly ILogger _log;

    public ModerationEngine(
        ModbotContext db,
        IAiClients ai,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        IDiscordModerationActions discord,
        CompiledTermLists compiled,
        IAiUsage usage)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(usage);

        _db = db;
        _ai = ai;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _discord = discord;
        _compiled = compiled;
        _usage = usage;
        _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);
    }

    public async Task<ModerationOutcome> CheckDiscordMessageAsync(DiscordMessageToCheck message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Text) || !await SwitchedOnAsync(ct).ConfigureAwait(false))
            return ModerationOutcome.Nothing;

        var rules = await RulesAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        var where = new Where(message.ChannelId, await RolesOfAsync(message.GuildId, message.AuthorId, ct).ConfigureAwait(false));

        var evaluation = await EvaluateAsync(rules, [(ModerationTargets.DiscordMessage, message.Text)], where, ct)
            .ConfigureAwait(false);

        if (evaluation.Matches.Count == 0)
            return new ModerationOutcome([], 0, false, null, evaluation.AiSkipped);

        return await RecordAsync(
            rules,
            evaluation.Matches,
            new Person(FactPlatform.Discord, message.AuthorId, message.AuthorName),
            message,
            evaluation.AiSkipped,
            ct).ConfigureAwait(false);
    }

    public async Task<ModerationOutcome> CheckProfileAsync(ProfileToCheck profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!await SwitchedOnAsync(ct).ConfigureAwait(false))
            return ModerationOutcome.Nothing;

        var texts = new List<(ModerationTargets, string)>();
        Add(ModerationTargets.DisplayName, profile.DisplayName);
        Add(ModerationTargets.Bio, profile.Bio);
        Add(ModerationTargets.Status, profile.Status);
        Add(ModerationTargets.Pronouns, profile.Pronouns);

        if (texts.Count == 0)
            return ModerationOutcome.Nothing;

        var rules = await RulesAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        var evaluation = await EvaluateAsync(rules, texts, Where.Nowhere, ct).ConfigureAwait(false);

        if (evaluation.Matches.Count == 0)
            return new ModerationOutcome([], 0, false, null, evaluation.AiSkipped);

        return await RecordAsync(
            rules,
            evaluation.Matches,
            new Person(FactPlatform.VRChat, profile.UserId, profile.DisplayName),
            message: null,
            evaluation.AiSkipped,
            ct).ConfigureAwait(false);

        void Add(ModerationTargets target, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add((target, text));
        }
    }

    /// <summary>
    /// "Try it" (design §10): every rule, on or off, against one text. Nothing is written and nothing
    /// is done, apart from an AI call counting towards the day's limit.
    /// </summary>
    public async Task<TryResult> TryAsync(string text, ModerationTargets target, bool includeAi, CancellationToken ct = default)
    {
        var rules = await RulesAsync(onlyEnabled: false, ct).ConfigureAwait(false);

        if (!includeAi)
            rules = rules with { Topics = [] };

        var evaluation = await EvaluateAsync(rules, [(target, text)], Where.Nowhere, ct).ConfigureAwait(false);

        var enabled = rules.Lists.Where(l => l.Enabled).Select(l => l.Id)
            .Concat(rules.Topics.Where(t => t.Enabled).Select(t => t.Id))
            .ToHashSet();

        var active = evaluation.Matches.Where(m => enabled.Contains(m.RuleId)).ToList();

        return new TryResult(
            [.. evaluation.Matches.Select(m => new TryMatch(m, enabled.Contains(m.RuleId)))],
            active.Any(m => m.DeleteMessage),
            active.Max(m => m.TimeoutMinutes),
            evaluation.AiSkipped);
    }

    /// <summary>
    /// Runs one rule's test set (design §12.2): every sample against the rule as it stands now,
    /// with the current model. Nothing is flagged and nothing is done; the run is stored.
    /// </summary>
    /// <returns>The stored run, or null when there is no such rule.</returns>
    public async Task<ModerationTestRun?> RunTestSetAsync(
        string ruleKind, Guid ruleId, Guid? userId, string? username, CancellationToken ct = default)
    {
        var list = ruleKind == ModerationRuleKind.TermList
            ? await _db.ModerationTermLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == ruleId, ct).ConfigureAwait(false)
            : null;
        var topic = ruleKind == ModerationRuleKind.Topic
            ? await _db.ModerationTopics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ruleId, ct).ConfigureAwait(false)
            : null;

        if (list is null && topic is null)
            return null;

        IModerationRule rule = (IModerationRule?)list ?? topic!;
        var rules = new Rules(list is null ? [] : [list], topic is null ? [] : [topic]);

        var samples = await _db.ModerationTestSamples.AsNoTracking()
            .Where(s => s.RuleId == ruleId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        var results = new JsonArray();
        var caught = 0;
        var wronglyFlagged = 0;
        var shouldFlag = 0;
        string? model = null;
        string? aiSkipped = null;

        foreach (var sample in samples)
        {
            // A sample whose target the rule does not check is still run, against the target the
            // sample names: a test set says what the rule should do with this text, and a rule
            // narrowed to display names should stop flagging the bio samples.
            var target = ModerationTargetNames.Parse(sample.Target) ?? ModerationTargets.DiscordMessage;
            var evaluation = await EvaluateAsync(rules, [(target, sample.Text)], Where.Nowhere, ct).ConfigureAwait(false);

            model ??= evaluation.Model;
            aiSkipped ??= evaluation.AiSkipped;

            var match = evaluation.Matches.FirstOrDefault();
            var flagged = match is not null;

            if (sample.ShouldFlag)
            {
                shouldFlag++;
                if (flagged) caught++;
            }
            else if (flagged)
            {
                wronglyFlagged++;
            }

            results.Add(new JsonObject
            {
                ["sampleId"] = sample.Id.ToString(),
                ["text"] = sample.Text,
                ["shouldFlag"] = sample.ShouldFlag,
                ["note"] = sample.Note,
                ["target"] = sample.Target,
                ["flagged"] = flagged,
                ["term"] = match?.Term,
                ["matched"] = match?.Matched,
                ["reason"] = match?.Reason,
            });
        }

        var now = _clock.UtcNow;
        var run = new ModerationTestRun
        {
            Id = Guid.CreateVersion7(now),
            RuleKind = ruleKind,
            RuleId = ruleId,
            RanAt = now,
            Model = model,
            RuleVersion = rule.Version,
            Samples = samples.Count,
            ShouldFlagCount = shouldFlag,
            Caught = caught,
            Missed = shouldFlag - caught,
            ShouldNotFlagCount = samples.Count - shouldFlag,
            WronglyFlagged = wronglyFlagged,
            AiSkipped = aiSkipped,
            Results = results.ToJsonString(),
            RanByUserId = userId,
            RanByUsername = username is null ? null : Clip(username, 64),
        };

        _db.ModerationTestRuns.Add(run);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.Information(
            "Test set for rule {RuleId} ran over {Samples} samples: {Caught} of {ShouldFlag} caught, {Wrong} wrongly flagged",
            ruleId, samples.Count, caught, shouldFlag, wronglyFlagged);

        return run;
    }

    // ── Rules ──────────────────────────────────────────────────────────────────────────────

    private sealed record Rules(IReadOnlyList<ModerationTermList> Lists, IReadOnlyList<ModerationTopic> Topics);

    private sealed record Person(FactPlatform Platform, string Id, string? Name);

    /// <summary>Where the text came from, for the scope checks (design §13.3).</summary>
    private sealed record Where(string? ChannelId, IReadOnlyCollection<string> RoleIds)
    {
        /// <summary>Profile text and "Try it": no channel and nobody's roles.</summary>
        public static Where Nowhere { get; } = new(null, []);
    }

    private sealed record Evaluation(List<ModerationMatch> Matches, string? AiSkipped, string? Model);

    private async Task<bool> SwitchedOnAsync(CancellationToken ct)
        => await _db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.AiModerationEnabled)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<Rules> RulesAsync(bool onlyEnabled, CancellationToken ct)
    {
        var lists = await _db.ModerationTermLists.AsNoTracking()
            .Where(l => !onlyEnabled || l.Enabled)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        var topics = await _db.ModerationTopics.AsNoTracking()
            .Where(t => !onlyEnabled || t.Enabled)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        return new Rules(lists, topics);
    }

    /// <summary>The message author's Discord roles, as the member store has them.</summary>
    /// <remarks>
    /// Read from the store rather than carried on the message, because an edit arrives without a
    /// member and because a role given a minute ago should count. A member Modbot has not stored
    /// yet has no roles, so no exemption applies, which is the safe direction.
    /// </remarks>
    private async Task<IReadOnlyCollection<string>> RolesOfAsync(string guildId, string userId, CancellationToken ct)
    {
        var roles = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .Select(m => m.Roles)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return roles is null ? [] : [.. RuleGuards.Ids(roles)];
    }

    /// <summary>Term lists first; AI topics only for text no term list matched (design §4.2).</summary>
    private async Task<Evaluation> EvaluateAsync(
        Rules rules, IReadOnlyList<(ModerationTargets Target, string Text)> texts, Where where, CancellationToken ct)
    {
        var matches = new List<ModerationMatch>();
        var forAi = new List<(ModerationTargets Target, string Text, List<ModerationTopic> Topics)>();

        foreach (var (target, text) in texts)
        {
            var found = false;

            foreach (var list in Applicable(rules.Lists, target, where))
            {
                foreach (var hit in TermMatcher.Check(_compiled.For(list), text, target))
                {
                    found = true;
                    if (Build(list, ModerationRuleKind.TermList, hit.TermKey, hit.Term, target, hit.Matched, hit.Reason, where) is { } match)
                        matches.Add(match);
                }
            }

            var topics = Applicable(rules.Topics, target, where).ToList();
            if (!found && topics.Count > 0)
                forAi.Add((target, text, topics));
        }

        if (forAi.Count == 0)
            return new Evaluation(matches, null, null);

        var chat = await _ai.GetChatAsync(ct).ConfigureAwait(false);
        if (chat is null)
            return new Evaluation(matches, "AI is off.", null);

        string? model = null;

        foreach (var (target, text, topics) in forAi)
        {
            // The spend limits are moderation's share of the AI bill and the bill as a whole; the daily
            // call limit is this screen's own brake. Any of them stops AI topics, and term lists carry on.
            if (await _usage.LimitReachedAsync(AiFeatures.Moderation, ct).ConfigureAwait(false) is { } reached)
                return new Evaluation(matches, reached.Message, model);

            if (!await AiCallAllowance.TryUseAsync(_db, _clock.UtcNow, ct).ConfigureAwait(false))
                return new Evaluation(matches, "The daily AI call limit is reached.", model);

            var keyed = topics.Select((t, i) => new TopicToCheck($"t{i + 1}", t.Id, t.Name, t.Instructions, t.Sensitivity)).ToList();
            var check = await TopicClassifier.CheckAsync(chat, keyed, text, target, ct).ConfigureAwait(false);

            model ??= check.Model ?? chat.Model;

            await _usage.RecordAsync(AiFeatures.Moderation, userId: null, check.Model ?? chat.Model, chat.Provider, check.Usage, ct)
                .ConfigureAwait(false);

            if (check.Error is not null)
            {
                _log.Warning("AI topic check failed: {Error}", check.Error);
                return new Evaluation(matches, check.Error, model);
            }

            foreach (var hit in check.Hits)
            {
                var topic = topics.First(t => t.Id == hit.Topic.Id);
                if (Build(topic, ModerationRuleKind.Topic, string.Empty, topic.Name, target, hit.Quote, hit.Why, where) is { } match)
                    matches.Add(match);
            }
        }

        return new Evaluation(matches, null, model);
    }

    /// <summary>The rules that look at this target, in this channel (design §3 and §13.3).</summary>
    private static IEnumerable<TRule> Applicable<TRule>(IReadOnlyList<TRule> rules, ModerationTargets target, Where where)
        where TRule : IModerationRule
        => rules.Where(r => ((ModerationTargets)r.Targets).HasFlag(target) && RuleGuards.RunsIn(r, where.ChannelId));

    /// <summary>
    /// One match, with everything that stands between the rule and the action already decided
    /// (design §13). Null when an exempt role means the rule does not even flag.
    /// </summary>
    private static ModerationMatch? Build(
        IModerationRule rule,
        string kind,
        string termKey,
        string term,
        ModerationTargets target,
        string matched,
        string? reason,
        Where where)
    {
        var exempt = RuleGuards.ExemptBy(rule, where.RoleIds) is not null;

        if (exempt && rule.ExemptRolesSkipFlag)
            return null;

        // Profile targets never act (design §6). Neither does anything outside Discord chat.
        var asks = target == ModerationTargets.DiscordMessage && RuleGuards.WantsAction(rule);
        var trial = asks && RuleGuards.InTrial(rule);
        var paused = asks && rule.PausedAt is not null;

        return new ModerationMatch(
            kind,
            rule.Id,
            rule.Name,
            rule.Version,
            termKey,
            term,
            target,
            matched,
            reason,
            asks && rule.DeleteMessage,
            asks ? rule.TimeoutMinutes : null,
            Acting: asks && !exempt && !trial && !paused,
            Trial: trial,
            Exempt: exempt,
            Paused: paused);
    }

    // ── Flags, actions, facts ──────────────────────────────────────────────────────────────

    private async Task<ModerationOutcome> RecordAsync(
        Rules rules,
        List<ModerationMatch> matches,
        Person person,
        DiscordMessageToCheck? message,
        string? aiSkipped,
        CancellationToken ct)
    {
        var ruleIds = matches.Select(m => m.RuleId).Distinct().ToList();

        var earlier = await _db.ModerationFlags.AsNoTracking()
            .Where(f => ruleIds.Contains(f.RuleId) && f.SubjectPlatform == person.Platform && f.SubjectId == person.Id)
            .Select(f => new { f.RuleId, f.TermKey, f.State, f.MessageId, f.Target, f.Matched })
            .ToListAsync(ct).ConfigureAwait(false);

        var results = new List<ModerationMatch>();
        var toFlag = new List<ModerationMatch>();

        foreach (var match in matches)
        {
            var target = ModerationTargetNames.NameOf(match.Target);
            var same = earlier.Where(f => f.RuleId == match.RuleId && f.TermKey == match.TermKey).ToList();

            if (same.Any(f => f.State == ModerationFlagState.Dismissed))
            {
                results.Add(match with { Suppressed = true, Acting = false, DeleteMessage = false, TimeoutMinutes = null });
                continue;
            }

            var repeat = message is not null
                ? same.Any(f => f.MessageId == message.MessageId)
                : same.Any(f => f.Target == target && string.Equals(f.Matched, match.Matched, StringComparison.Ordinal));

            // Also a repeat inside this one check: two terms of a list are two flags, one term
            // matching twice is one.
            if (repeat || toFlag.Any(m => m.RuleId == match.RuleId && m.TermKey == match.TermKey && m.Target == match.Target))
            {
                results.Add(match with { Acting = false, DeleteMessage = false, TimeoutMinutes = null });
                continue;
            }

            results.Add(match);
            toFlag.Add(match);
        }

        if (toFlag.Count == 0)
            return new ModerationOutcome(results, 0, false, null, aiSkipped);

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var flags = toFlag.Select(m => new ModerationFlag
        {
            Id = Guid.CreateVersion7(now),
            FlaggedAt = now,
            RuleKind = m.RuleKind,
            RuleId = m.RuleId,
            RuleName = Clip(m.RuleName, 100),
            RuleVersion = m.RuleVersion,
            TermKey = Clip(m.TermKey, 200),
            Term = Clip(m.Term, 2000),
            Target = ModerationTargetNames.NameOf(m.Target),
            SubjectPlatform = person.Platform,
            SubjectId = person.Id,
            SubjectName = person.Name is null ? null : Clip(person.Name, 200),
            ChannelId = message?.ChannelId,
            MessageId = message?.MessageId,
            Matched = Clip(m.Matched, 1000),
            Reason = m.Reason is null ? null : Clip(m.Reason, 2000),
            Trial = m.Trial,
            WouldDeleteMessage = m.DeleteMessage,
            WouldTimeOutMinutes = m.TimeoutMinutes,
        }).ToList();

        // The actions, before anything is saved, so the flag rows can say what happened. Only
        // matches the guards let through act; the rest have recorded what they would have done.
        var deleted = false;
        int? timedOut = null;
        DiscordActionOutcome? deleteOutcome = null;
        DiscordActionOutcome? timeoutOutcome = null;
        var deleters = toFlag.Where(m => m.Acting && m.DeleteMessage).ToList();
        var timers = toFlag.Where(m => m.Acting && m.TimeoutMinutes is > 0).ToList();

        if (message is not null && (deleters.Count > 0 || timers.Count > 0))
        {
            var reason = Clip("Modbot moderation rule: " + string.Join(", ", toFlag.Select(m => m.RuleName).Distinct()), 400);

            if (deleters.Count > 0)
            {
                deleteOutcome = await _discord.DeleteMessageAsync(message.ChannelId, message.MessageId, reason, ct)
                    .ConfigureAwait(false);
                deleted = deleteOutcome.Done;
            }

            if (timers.Count > 0)
            {
                var minutes = timers.Max(m => m.TimeoutMinutes!.Value);
                timeoutOutcome = await _discord.TimeOutAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(minutes), reason, ct)
                    .ConfigureAwait(false);
                if (timeoutOutcome.Done)
                    timedOut = minutes;
            }
        }

        foreach (var flag in flags)
        {
            var asked = toFlag.First(m => m.RuleId == flag.RuleId && m.TermKey == flag.TermKey);
            flag.MessageDeleted = deleted && asked.Acting && asked.DeleteMessage;
            flag.TimedOutMinutes = asked.Acting && asked.TimeoutMinutes is > 0 ? timedOut : null;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        _db.ModerationFlags.AddRange(flags);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var flag in flags)
            await _facts.WriteAsync(FlagFact(flag, now), ct).ConfigureAwait(false);

        if (message is not null && deleteOutcome is not null)
        {
            await _facts.WriteAsync(
                ActionFact(FactType.AiModerationMessageDeleted, rules, deleters, flags, person, message, deleteOutcome, now, minutes: null),
                ct).ConfigureAwait(false);
        }

        if (message is not null && timeoutOutcome is not null)
        {
            await _facts.WriteAsync(
                ActionFact(FactType.AiModerationTimeout, rules, timers, flags, person, message, timeoutOutcome, now,
                    minutes: timers.Max(m => m.TimeoutMinutes)),
                ct).ConfigureAwait(false);
        }

        if (deleted || timedOut is not null)
            await PauseRunawayRulesAsync(rules, [.. deleters, .. timers], now, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _log.Information(
            "Moderation rules flagged {Count} match(es) for {Platform} user {Subject}; message deleted: {Deleted}; timed out: {Minutes}",
            flags.Count, person.Platform, person.Id, deleted, timedOut);

        return new ModerationOutcome(results, flags.Count, deleted, timedOut, aiSkipped);
    }

    /// <summary>
    /// A rule that has just acted far more in an hour than it usually does stops itself
    /// (design §13.2) and waits for an operator to look at it.
    /// </summary>
    private async Task PauseRunawayRulesAsync(
        Rules rules, IReadOnlyList<ModerationMatch> acted, DateTimeOffset now, CancellationToken ct)
    {
        var anHourAgo = now.AddHours(-1);
        var sevenDaysAgo = now.AddDays(-7);

        foreach (var ruleId in acted.Select(m => m.RuleId).Distinct())
        {
            var list = rules.Lists.FirstOrDefault(l => l.Id == ruleId);
            var topic = rules.Topics.FirstOrDefault(t => t.Id == ruleId);
            IModerationRule? rule = (IModerationRule?)list ?? topic;

            if (rule is null || rule.PausedAt is not null)
                continue;

            var hour = await ActionsSinceAsync(ruleId, anHourAgo, ct).ConfigureAwait(false);
            if (hour <= RunawayGuard.ActionsInAnHour)
                continue;

            var week = await ActionsSinceAsync(ruleId, sevenDaysAgo, ct).ConfigureAwait(false);
            if (!RunawayGuard.ShouldPause(hour, week))
                continue;

            var reason = RunawayGuard.Reason(hour, week);

            if (list is not null)
            {
                await _db.ModerationTermLists.Where(l => l.Id == ruleId && l.PausedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(l => l.PausedAt, now).SetProperty(l => l.PausedReason, reason), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await _db.ModerationTopics.Where(t => t.Id == ruleId && t.PausedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(t => t.PausedAt, now).SetProperty(t => t.PausedReason, reason), ct)
                    .ConfigureAwait(false);
            }

            await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.AiModerationRulePaused,
                OccurredAt = now,
                // The subject is the rule: nobody did this, and it is not about the person who was
                // acted on. The operator who set the rule to act is named in the data.
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = ruleId.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["ruleKind"] = list is not null ? ModerationRuleKind.TermList : ModerationRuleKind.Topic,
                    ["ruleId"] = ruleId.ToString(),
                    ["ruleName"] = rule.Name,
                    ["ruleVersion"] = rule.Version,
                    ["reason"] = reason,
                    ["actionsInTheLastHour"] = hour,
                    ["actionsInTheLastSevenDays"] = week,
                    ["setToActByUserId"] = rule.ActSetByUserId?.ToString(),
                    ["setToActByUsername"] = rule.ActSetByUsername,
                },
            }, ct).ConfigureAwait(false);

            _log.Warning("Moderation rule {RuleName} paused itself: {Reason}", rule.Name, reason);
        }
    }

    private Task<int> ActionsSinceAsync(Guid ruleId, DateTimeOffset since, CancellationToken ct)
        => _db.ModerationFlags
            .Where(f => f.RuleId == ruleId && f.FlaggedAt >= since && (f.MessageDeleted || f.TimedOutMinutes != null))
            .CountAsync(ct);

    private static FactRecord FlagFact(ModerationFlag flag, DateTimeOffset now) => new()
    {
        Type = FactType.AiModerationFlag,
        OccurredAt = now,
        SubjectPlatform = flag.SubjectPlatform,
        SubjectId = flag.SubjectId,
        Source = FactSource.Modbot,
        Data = new JsonObject
        {
            ["flagId"] = flag.Id.ToString(),
            ["ruleKind"] = flag.RuleKind,
            ["ruleId"] = flag.RuleId.ToString(),
            ["ruleName"] = flag.RuleName,
            ["ruleVersion"] = flag.RuleVersion,
            ["termKey"] = flag.TermKey,
            ["term"] = flag.Term,
            ["target"] = flag.Target,
            ["matched"] = flag.Matched,
            ["reason"] = flag.Reason,
            ["subjectName"] = flag.SubjectName,
            ["channelId"] = flag.ChannelId,
            ["messageId"] = flag.MessageId,
            ["trial"] = flag.Trial,
            ["wouldDeleteMessage"] = flag.WouldDeleteMessage,
            ["wouldTimeOutMinutes"] = flag.WouldTimeOutMinutes,
        },
    };

    /// <summary>
    /// One action and every rule that asked for it, each with the operator who set it to act and
    /// the rule version it acted on (M8 §2, design §14).
    /// </summary>
    private static FactRecord ActionFact(
        string type,
        Rules rules,
        List<ModerationMatch> askedBy,
        List<ModerationFlag> flags,
        Person person,
        DiscordMessageToCheck message,
        DiscordActionOutcome outcome,
        DateTimeOffset now,
        int? minutes)
    {
        var ruleNodes = new JsonArray();

        foreach (var ruleId in askedBy.Select(m => m.RuleId).Distinct())
        {
            var list = rules.Lists.FirstOrDefault(l => l.Id == ruleId);
            var topic = rules.Topics.FirstOrDefault(t => t.Id == ruleId);

            ruleNodes.Add(new JsonObject
            {
                ["ruleKind"] = list is not null ? ModerationRuleKind.TermList : ModerationRuleKind.Topic,
                ["ruleId"] = ruleId.ToString(),
                ["ruleName"] = list?.Name ?? topic?.Name,
                ["ruleVersion"] = list?.Version ?? topic?.Version,
                ["setToActByUserId"] = (list?.ActSetByUserId ?? topic?.ActSetByUserId)?.ToString(),
                ["setToActByUsername"] = list?.ActSetByUsername ?? topic?.ActSetByUsername,
                ["setToActAt"] = (list?.ActSetAt ?? topic?.ActSetAt)?.ToString("O"),
                ["trialEndedByUsername"] = list?.TrialEndedByUsername ?? topic?.TrialEndedByUsername,
                ["trialEndedAt"] = (list?.TrialEndedAt ?? topic?.TrialEndedAt)?.ToString("O"),
            });
        }

        return new FactRecord
        {
            Type = type,
            OccurredAt = now,
            SubjectPlatform = person.Platform,
            SubjectId = person.Id,
            Source = FactSource.Modbot,
            Data = new JsonObject
            {
                ["guildId"] = message.GuildId,
                ["channelId"] = message.ChannelId,
                ["messageId"] = message.MessageId,
                ["minutes"] = minutes,
                ["done"] = outcome.Done,
                ["error"] = outcome.Error,
                ["flagIds"] = new JsonArray([.. flags.Where(f => askedBy.Any(m => m.RuleId == f.RuleId)).Select(f => JsonValue.Create(f.Id.ToString()))]),
                ["rules"] = ruleNodes,
            },
        };
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>The daily AI call limit (design §4.2).</summary>
public static class AiCallAllowance
{
    /// <summary>
    /// Takes one call from today's allowance, or answers false when there is none left.
    /// </summary>
    /// <remarks>
    /// One statement that checks and counts, so two checks running at once cannot both take the
    /// last call. A new UTC day starts the count again at one.
    /// </remarks>
    public static async Task<bool> TryUseAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var updated = await db.Settings
            .Where(s => s.Id == 1
                        && s.AiModerationDailyCallLimit > 0
                        && (s.AiModerationCallsDay != today || s.AiModerationCallsUsed < s.AiModerationDailyCallLimit))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.AiModerationCallsUsed, s => s.AiModerationCallsDay == today ? s.AiModerationCallsUsed + 1 : 1)
                .SetProperty(s => s.AiModerationCallsDay, today), ct)
            .ConfigureAwait(false);

        return updated == 1;
    }

    /// <summary>How many calls today has used.</summary>
    public static int UsedToday(Settings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AiModerationCallsDay == DateOnly.FromDateTime(now.UtcDateTime) ? settings.AiModerationCallsUsed : 0;
    }
}
