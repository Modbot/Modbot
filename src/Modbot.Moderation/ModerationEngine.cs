using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Moderation;

/// <summary>What "Try it" answers: every rule that matched, and what would happen.</summary>
/// <param name="Matches">Rules that are switched off are included, marked by <see cref="TryMatch.RuleEnabled"/>.</param>
/// <param name="CallId">The AI call behind it, in the call log. Null when no AI call was made.</param>
public sealed record TryResult(
    IReadOnlyList<TryMatch> Matches,
    bool WouldDeleteMessage,
    int? WouldTimeOutMinutes,
    string? AiSkipped,
    Guid? CallId = null,
    bool WouldGroupBan = false,
    bool WouldGroupRemove = false);

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
/// The AutoMod engine (AutoMod design §4; AI moderation design §4 to §8, §12 to §14).
/// </summary>
/// <remarks>
/// Term lists are matched here and cost nothing. AI topics go through <see cref="IAiRuleChecker"/>,
/// and only when AI is on and the group has left that tool switched on (AutoMod design §6): with
/// AI off, or without <c>Modbot.AI</c> in the process, every check is term lists alone.
/// </remarks>
public sealed class ModerationEngine : IModerationChecker
{
    private readonly ModbotContext _db;
    private readonly IAiRuleChecker _ai;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly IDiscordModerationActions _discord;
    private readonly IVRChatModerationActions _vrchat;
    private readonly CompiledTermLists _compiled;
    private readonly TextLanguage _language;
    private readonly ReviewFacts _reviewFacts;
    private readonly IAiCallTexts _callTexts;
    private readonly ILogger _log;

    public ModerationEngine(
        ModbotContext db,
        IAiRuleChecker ai,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        IDiscordModerationActions discord,
        IVRChatModerationActions vrchat,
        CompiledTermLists compiled,
        TextLanguage language,
        ReviewFacts reviewFacts,
        IAiCallTexts callTexts)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(vrchat);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(reviewFacts);
        ArgumentNullException.ThrowIfNull(callTexts);

        _db = db;
        _ai = ai;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _discord = discord;
        _vrchat = vrchat;
        _compiled = compiled;
        _language = language;
        _reviewFacts = reviewFacts;
        _callTexts = callTexts;
        _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);
    }

    public async Task<ModerationOutcome> CheckDiscordMessageAsync(DiscordMessageToCheck message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Text))
            return ModerationOutcome.Nothing;

        var switches = await SwitchesAsync(ct).ConfigureAwait(false);
        if (!switches.AutoModOn)
            return ModerationOutcome.Nothing;

        var rules = await RulesAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        var where = new Where(message.ChannelId, await RolesOfAsync(message.GuildId, message.AuthorId, ct).ConfigureAwait(false));

        var evaluation = await EvaluateAsync(
            rules, [new TextItem(0, ModerationTargets.DiscordMessage, message.Text)], where, AiAsker.Nobody,
            [CheckSubject.Of(message)], switches, ct)
            .ConfigureAwait(false);

        if (evaluation.Matches.Count == 0)
            return new ModerationOutcome([], 0, false, null, evaluation.AiSkipped);

        return await RecordAsync(
            rules,
            evaluation,
            subject: 0,
            new Person(FactPlatform.Discord, message.AuthorId, message.AuthorName),
            message,
            ct).ConfigureAwait(false);
    }

    public async Task<ModerationOutcome> CheckProfileAsync(ProfileToCheck profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var outcomes = await CheckProfilesAsync([profile], ct).ConfigureAwait(false);
        return outcomes[0];
    }

    /// <summary>
    /// Several profiles in one pass. The AI topics of all of them go in as few calls as the
    /// operator's batch size allows (design §4.2); term lists are matched here and cost nothing.
    /// </summary>
    public async Task<IReadOnlyList<ModerationOutcome>> CheckProfilesAsync(
        IReadOnlyList<ProfileToCheck> profiles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var nothing = (IReadOnlyList<ModerationOutcome>)[.. profiles.Select(_ => ModerationOutcome.Nothing)];

        if (profiles.Count == 0)
            return nothing;

        var switches = await SwitchesAsync(ct).ConfigureAwait(false);
        if (!switches.AutoModOn)
            return nothing;

        var texts = new List<TextItem>();

        for (var i = 0; i < profiles.Count; i++)
        {
            Add(i, ModerationTargets.DisplayName, profiles[i].DisplayName);
            Add(i, ModerationTargets.Bio, profiles[i].Bio);
            Add(i, ModerationTargets.Status, profiles[i].Status);
            Add(i, ModerationTargets.Pronouns, profiles[i].Pronouns);
        }

        if (texts.Count == 0)
            return nothing;

        var rules = await RulesAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        var evaluation = await EvaluateAsync(
            rules, texts, Where.Nowhere, AiAsker.Nobody, [.. profiles.Select(CheckSubject.Of)], switches, ct).ConfigureAwait(false);

        var outcomes = new List<ModerationOutcome>(profiles.Count);

        for (var i = 0; i < profiles.Count; i++)
        {
            if (!evaluation.Matches.Any(m => m.Subject == i))
            {
                outcomes.Add(new ModerationOutcome([], 0, false, null, evaluation.AiSkipped));
                continue;
            }

            outcomes.Add(await RecordAsync(
                rules,
                evaluation,
                i,
                new Person(FactPlatform.VRChat, profiles[i].UserId, profiles[i].DisplayName),
                message: null,
                ct).ConfigureAwait(false));
        }

        return outcomes;

        void Add(int subject, ModerationTargets target, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(new TextItem(subject, target, text));
        }
    }

    /// <summary>
    /// "Try it" (design §10): every rule, on or off, against one text. Nothing is written and nothing
    /// is done, apart from an AI call counting towards the day's limit and appearing in the call log.
    /// </summary>
    /// <remarks>
    /// Somebody pressed a button, so the call keeps what the model was sent and what it answered:
    /// this is the screen whose whole purpose is showing what the model saw.
    /// </remarks>
    public async Task<TryResult> TryAsync(
        string text,
        ModerationTargets target,
        bool includeAi,
        Guid? userId = null,
        string? username = null,
        CancellationToken ct = default)
    {
        var rules = await RulesAsync(onlyEnabled: false, ct).ConfigureAwait(false);

        if (!includeAi)
            rules = rules with { Topics = [] };

        var switches = await SwitchesAsync(ct).ConfigureAwait(false);
        var evaluation = await EvaluateAsync(
            rules, [new TextItem(0, target, text)], Where.Nowhere, new AiAsker(userId, username, KeepText: true),
            [CheckSubject.None], switches, ct)
            .ConfigureAwait(false);

        var enabled = rules.Lists.Where(l => l.Enabled).Select(l => l.Id)
            .Concat(rules.Topics.Where(t => t.Enabled).Select(t => t.Id))
            .ToHashSet();

        var matches = evaluation.Matches.Select(m => m.Match).ToList();
        var active = matches.Where(m => enabled.Contains(m.RuleId)).ToList();

        return new TryResult(
            [.. matches.Select(m => new TryMatch(m, enabled.Contains(m.RuleId)))],
            active.Any(m => m.DeleteMessage),
            active.Max(m => m.TimeoutMinutes),
            evaluation.AiSkipped,
            evaluation.Matches.Select(m => m.CallId).FirstOrDefault(id => id is not null),
            active.Any(m => m.GroupBan),
            active.Any(m => m.GroupRemove));
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
        var switches = await SwitchesAsync(ct).ConfigureAwait(false);

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
            var evaluation = await EvaluateAsync(
                rules, [new TextItem(0, target, sample.Text)], Where.Nowhere, new AiAsker(userId, username, KeepText: true),
                [CheckSubject.None], switches, ct)
                .ConfigureAwait(false);

            model ??= evaluation.Model;
            aiSkipped ??= evaluation.AiSkipped;

            var match = evaluation.Matches.Select(m => m.Match).FirstOrDefault();
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

    /// <summary>One piece of text to check, and which of the people being checked it belongs to.</summary>
    private sealed record TextItem(int Subject, ModerationTargets Target, string Text);

    /// <summary>
    /// Who one piece of text belongs to, for the parts of a check that are not the text itself: the
    /// messages around it (design §16) and the pictures that belong to it (§17).
    /// </summary>
    private sealed record CheckSubject(DiscordMessageToCheck? Message, string? VRChatUserId)
    {
        /// <summary>"Try it" and a test run: a piece of text with nothing around it.</summary>
        public static CheckSubject None { get; } = new(null, null);

        public static CheckSubject Of(DiscordMessageToCheck message) => new(message, null);

        public static CheckSubject Of(ProfileToCheck profile) => new(null, profile.UserId);
    }

    /// <summary>One rule match, and the AI call that found it. Null for a term list, which makes none.</summary>
    private sealed record SubjectMatch(int Subject, ModerationMatch Match, Guid? CallId);

    private sealed record Evaluation(
        List<SubjectMatch> Matches,
        string? AiSkipped,
        string? Model,
        IReadOnlyDictionary<Guid, (string Prompt, string Answer)> CallTexts);

    /// <summary>The switches a check reads once: AutoMod itself, AI, and the AI tools (AutoMod design §6).</summary>
    public sealed record AutoModSwitches(bool AutoModOn, bool AiOn, IReadOnlyDictionary<string, bool> Tools)
    {
        public bool Classifies => AiOn && AutoModAiTools.IsOn(Tools, AutoModAiTools.ClassifyTopics);

        public bool SendsPictures => AiOn && AutoModAiTools.IsOn(Tools, AutoModAiTools.CheckPictures);
    }

    private async Task<AutoModSwitches> SwitchesAsync(CancellationToken ct)
    {
        var row = await _db.Settings.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => new { s.AutoModEnabled, s.AiEnabled, s.AutoModAiTools })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row is null
            ? new AutoModSwitches(false, false, AutoModAiTools.Parse(null))
            : new AutoModSwitches(row.AutoModEnabled, row.AiEnabled, AutoModAiTools.Parse(row.AutoModAiTools));
    }

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
        Rules rules,
        IReadOnlyList<TextItem> texts,
        Where where,
        AiAsker asker,
        IReadOnlyList<CheckSubject> subjects,
        AutoModSwitches switches,
        CancellationToken ct)
    {
        var matches = new List<SubjectMatch>();
        var forAi = new List<(TextItem Item, List<ModerationTopic> Topics)>();

        // Worked out once for each piece of text, not once per match: the language is the text's,
        // not the rule's (design §18).
        var languages = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var item in texts)
        {
            var language = _language.Of(item.Text);
            languages[Key(item)] = language;
            var found = false;

            foreach (var list in Applicable(rules.Lists, item.Target, where))
            {
                foreach (var hit in TermMatcher.Check(_compiled.For(list), item.Text, item.Target))
                {
                    found = true;
                    if (Build(list, ModerationRuleKind.TermList, hit.TermKey, hit.Term, item.Target, hit.Matched, hit.Reason, where, language) is { } match)
                        matches.Add(new SubjectMatch(item.Subject, match, null));
                }
            }

            var topics = Applicable(rules.Topics, item.Target, where).ToList();
            if (!found && topics.Count > 0)
                forAi.Add((item, topics));
        }

        if (forAi.Count == 0)
            return new Evaluation(matches, null, null, new Dictionary<Guid, (string, string)>());

        // The AI is not asked while AI is off, and not asked about topics while that tool is off
        // (AutoMod design §6). Both are decided here, before anything is built to send.
        if (!switches.AiOn)
            return new Evaluation(matches, NoAiRuleChecker.Reason, null, new Dictionary<Guid, (string, string)>());
        if (!switches.Classifies)
            return new Evaluation(matches, ToolOff, null, new Dictionary<Guid, (string, string)>());

        var asks = new List<AiRuleText>();
        var askedFor = new List<(TextItem Item, IReadOnlyList<ContextMessage> Context)>();

        foreach (var (item, topics) in forAi)
        {
            var subject = item.Subject < subjects.Count ? subjects[item.Subject] : CheckSubject.None;

            // Topics are grouped by what they ask for rather than all asked at once, because
            // context and pictures change the answer: a topic whose operator asked for no context
            // must not be judged on a conversation, and one that does not check pictures must not
            // be shown them. Rules left on the defaults share a group (§16.3).
            foreach (var group in topics.GroupBy(t => (Context: ContextFor(t, item.Target), Pictures: t.CheckPictures && switches.SendsPictures)))
            {
                var context = subject.Message is { } message && group.Key.Context > 0
                    ? await MessageContext.BeforeAsync(_db, message.ChannelId, message.MessageId, group.Key.Context, ct)
                        .ConfigureAwait(false)
                    : [];

                var pictures = group.Key.Pictures
                    ? await PictureSourcesAsync(subject, item.Target, ct).ConfigureAwait(false)
                    : [];

                asks.Add(new AiRuleText($"x{asks.Count + 1}", item.Target, item.Text, [.. group], context, pictures));
                askedFor.Add((item, context));
            }
        }

        var answer = await _ai.CheckAsync(asks, asker, ct).ConfigureAwait(false);

        foreach (var hit in answer.Hits)
        {
            var index = asks.FindIndex(a => a.Key == hit.TextKey);
            if (index < 0)
                continue;

            var (item, context) = askedFor[index];
            var topic = asks[index].Topics.FirstOrDefault(t => t.Id == hit.TopicId);
            if (topic is null)
                continue;

            var contextIds = context.Count == 0 ? null : context.Select(c => c.MessageId).ToList();

            var built = Build(
                topic, ModerationRuleKind.Topic, string.Empty, topic.Name, item.Target,
                hit.Quote, hit.Why, where, languages.GetValueOrDefault(Key(item)), contextIds);

            if (built is null)
                continue;

            matches.Add(new SubjectMatch(
                item.Subject,
                hit.Picture is { } picture ? built with { Picture = picture, PictureUrl = hit.PictureUrl } : built,
                hit.CallId));
        }

        return new Evaluation(matches, answer.Skipped, answer.Model, answer.CallTexts);

        static string Key(TextItem item) => $"{item.Subject}:{(int)item.Target}";
    }

    /// <summary>What "Try it" and a flag say when the topic tool is switched off on the AutoMod tab.</summary>
    public const string ToolOff = "AI topics are switched off in AutoMod.";

    /// <summary>
    /// How much context this rule wants. Only Discord chat has any: a profile has no conversation
    /// around it (design §16).
    /// </summary>
    private static int ContextFor(IModerationRule rule, ModerationTargets target)
        => target == ModerationTargets.DiscordMessage && ContextMessageCounts.IsCount(rule.ContextMessages)
            ? rule.ContextMessages
            : 0;

    /// <summary>
    /// The pictures that belong to what is being checked: a Discord message's image attachments
    /// and its author's avatar, or a VRChat profile picture and avatar picture (design §17).
    /// </summary>
    private async Task<IReadOnlyList<PictureSource>> PictureSourcesAsync(
        CheckSubject subject, ModerationTargets target, CancellationToken ct)
    {
        if (target == ModerationTargets.DiscordMessage && subject.Message is { } message)
        {
            var row = await _db.DiscordMessages.AsNoTracking()
                .Where(m => m.MessageId == message.MessageId)
                .Select(m => m.Attachments)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            var sources = new List<PictureSource>(PictureAttachments.Of(row));

            var avatar = await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == message.GuildId && m.UserId == message.AuthorId)
                .Select(m => m.AvatarUrl)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (avatar is { Length: > 0 })
                sources.Add(new PictureSource("Discord avatar", avatar));

            return sources;
        }

        if (subject.VRChatUserId is not { Length: > 0 } userId)
            return [];

        var profile = await _db.VRChatUsers.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new { u.ProfilePictureUrl, u.IconUrl, u.BannerUrl, u.CurrentAvatarThumbnailImageUrl })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (profile is null)
            return [];

        // Every picture the profile shows, not only the best one: a rule about pictures should
        // see the banner and the icon as well as the picture a member list would pick.
        var pictures = new List<PictureSource>();
        if (profile.ProfilePictureUrl is { Length: > 0 } picture)
            pictures.Add(new PictureSource("VRChat profile picture", picture));
        if (profile.IconUrl is { Length: > 0 } icon)
            pictures.Add(new PictureSource("VRChat user icon", icon));
        if (profile.BannerUrl is { Length: > 0 } banner)
            pictures.Add(new PictureSource("VRChat profile banner", banner));
        if (profile.CurrentAvatarThumbnailImageUrl is { Length: > 0 } thumbnail)
            pictures.Add(new PictureSource("VRChat avatar picture", thumbnail));

        return pictures;
    }

    /// <summary>The rules that look at this target, in this channel (design §3 and §13.3).</summary>
    private static IEnumerable<TRule> Applicable<TRule>(IReadOnlyList<TRule> rules, ModerationTargets target, Where where)
        where TRule : IModerationRule
        => rules.Where(r => ((ModerationTargets)r.Targets).HasFlag(target) && RuleGuards.RunsIn(r, where.ChannelId));

    /// <summary>
    /// One match, with everything that stands between the rule and the action already decided
    /// (design §13). Null when an exempt role means the rule does not even flag.
    /// </summary>
    /// <remarks>
    /// Discord actions apply to a Discord message; VRChat actions apply to a VRChat profile
    /// (AutoMod design §5). A rule never acts across platforms: a bio is not a message to delete,
    /// and a Discord author is not a group member to ban.
    /// </remarks>
    private static ModerationMatch? Build(
        IModerationRule rule,
        string kind,
        string termKey,
        string term,
        ModerationTargets target,
        string matched,
        string? reason,
        Where where,
        string? language = null,
        IReadOnlyList<string>? contextMessageIds = null)
    {
        var exempt = RuleGuards.ExemptBy(rule, where.RoleIds) is not null;

        if (exempt && rule.ExemptRolesSkipFlag)
            return null;

        var chat = target == ModerationTargets.DiscordMessage;
        var asks = chat ? RuleGuards.WantsDiscordAction(rule) : RuleGuards.WantsVRChatAction(rule);
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
            asks && chat && rule.DeleteMessage,
            asks && chat ? rule.TimeoutMinutes : null,
            Acting: asks && !exempt && !trial && !paused,
            Trial: trial,
            Exempt: exempt,
            Paused: paused,
            Language: language,
            ContextMessageIds: contextMessageIds,
            GroupBan: asks && !chat && rule.GroupBan,
            GroupRemove: asks && !chat && rule.GroupRemove);
    }

    // ── Flags, actions, facts ──────────────────────────────────────────────────────────────

    private async Task<ModerationOutcome> RecordAsync(
        Rules rules,
        Evaluation evaluation,
        int subject,
        Person person,
        DiscordMessageToCheck? message,
        CancellationToken ct)
    {
        var aiSkipped = evaluation.AiSkipped;
        var mine = evaluation.Matches.Where(m => m.Subject == subject).ToList();
        var matches = mine.Select(m => m.Match).ToList();

        // Two identical matches are the same rule finding the same words twice, so the later one
        // simply replaces the earlier: both came from the same call.
        var callOf = new Dictionary<ModerationMatch, Guid?>();
        foreach (var m in mine)
            callOf[m.Match] = m.CallId;

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
                results.Add(match.WithoutActions(suppressed: true));
                continue;
            }

            var repeat = message is not null
                ? same.Any(f => f.MessageId == message.MessageId)
                : same.Any(f => f.Target == target && string.Equals(f.Matched, match.Matched, StringComparison.Ordinal));

            // Also a repeat inside this one check: two terms of a list are two flags, one term
            // matching twice is one.
            if (repeat || toFlag.Any(m => m.RuleId == match.RuleId && m.TermKey == match.TermKey && m.Target == match.Target))
            {
                results.Add(match.WithoutActions());
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
            Language = m.Language,
            ContextMessageIds = JsonSerializer.Serialize(m.ContextMessageIds ?? []),
            Picture = m.Picture is null ? null : Clip(m.Picture, 300),
            PictureUrl = m.PictureUrl is null ? null : Clip(m.PictureUrl, 2000),
            Reason = m.Reason is null ? null : Clip(m.Reason, 2000),
            Trial = m.Trial,
            WouldDeleteMessage = m.DeleteMessage,
            WouldTimeOutMinutes = m.TimeoutMinutes,
            WouldGroupBan = m.GroupBan,
            WouldGroupRemove = m.GroupRemove,
            CallId = callOf.GetValueOrDefault(m),
        }).ToList();

        // The actions, before anything is saved, so the flag rows can say what happened. Only
        // matches the guards let through act; the rest have recorded what they would have done.
        var actions = await ActAsync(toFlag, person, message, ct).ConfigureAwait(false);

        foreach (var flag in flags)
        {
            var asked = toFlag.First(m => m.RuleId == flag.RuleId && m.TermKey == flag.TermKey);
            flag.MessageDeleted = actions.Deleted && asked.Acting && asked.DeleteMessage;
            flag.TimedOutMinutes = asked.Acting && asked.TimeoutMinutes is > 0 ? actions.TimedOutMinutes : null;
            flag.GroupBanned = actions.GroupBanned && asked.Acting && asked.GroupBan;
            flag.GroupRemoved = actions.GroupRemoved && asked.Acting && asked.GroupRemove;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        _db.ModerationFlags.AddRange(flags);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await OpenReviewsAsync(rules, flags, now, ct).ConfigureAwait(false);

        foreach (var flag in flags)
            await _facts.WriteAsync(FlagFact(flag, now), ct).ConfigureAwait(false);

        foreach (var done in actions.Facts)
            await _facts.WriteAsync(ActionFact(done, rules, flags, person, message, now), ct).ConfigureAwait(false);

        if (actions.Any)
            await PauseRunawayRulesAsync(rules, actions.ActedBy, now, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        // A call that produced a flag keeps what it was sent and what it answered, so a moderator
        // looking at the flag can see exactly what the model saw. Every other call keeps counts only.
        foreach (var callId in flags.Select(f => f.CallId).OfType<Guid>().Distinct())
        {
            if (evaluation.CallTexts.TryGetValue(callId, out var texts))
                await _callTexts.KeepAsync(callId, texts.Prompt, texts.Answer, ct).ConfigureAwait(false);
        }

        _log.Information(
            "Moderation rules flagged {Count} match(es) for {Platform} user {Subject}; message deleted: {Deleted}; timed out: {Minutes}; group ban: {Banned}; group remove: {Removed}",
            flags.Count, person.Platform, person.Id, actions.Deleted, actions.TimedOutMinutes, actions.GroupBanned, actions.GroupRemoved);

        return new ModerationOutcome(
            results, flags.Count, actions.Deleted, actions.TimedOutMinutes, aiSkipped, actions.GroupBanned, actions.GroupRemoved);
    }

    /// <summary>One action that was tried, and every rule that asked for it.</summary>
    private sealed record ActionDone(string FactType, List<ModerationMatch> AskedBy, bool Done, string? Error, int? Minutes);

    /// <summary>Everything the actions did, for the flag rows and the facts.</summary>
    private sealed record Actions(List<ActionDone> Facts)
    {
        public bool Deleted => Facts.Any(f => f.FactType == Core.Data.Entities.FactType.AutoModMessageDeleted && f.Done);

        public int? TimedOutMinutes => Facts.FirstOrDefault(f => f.FactType == Core.Data.Entities.FactType.AutoModTimeout && f.Done)?.Minutes;

        public bool GroupBanned => Facts.Any(f => f.FactType == Core.Data.Entities.FactType.AutoModGroupBan && f.Done);

        public bool GroupRemoved => Facts.Any(f => f.FactType == Core.Data.Entities.FactType.AutoModGroupRemove && f.Done);

        public bool Any => Facts.Any(f => f.Done);

        /// <summary>The matches whose action really happened, for the runaway check.</summary>
        public IReadOnlyList<ModerationMatch> ActedBy => [.. Facts.Where(f => f.Done).SelectMany(f => f.AskedBy)];
    }

    /// <summary>
    /// Carries out what the acting matches ask for: on Discord, for a message; in VRChat, for a
    /// profile (AutoMod design §5). Each action is tried once and its answer recorded, whatever it
    /// was: a failed action is a fact saying it did not happen, never a retry.
    /// </summary>
    private async Task<Actions> ActAsync(
        List<ModerationMatch> toFlag, Person person, DiscordMessageToCheck? message, CancellationToken ct)
    {
        var done = new List<ActionDone>();
        var acting = toFlag.Where(m => m.Acting).ToList();

        if (acting.Count == 0)
            return new Actions(done);

        var reason = Clip("Modbot moderation rule: " + string.Join(", ", toFlag.Select(m => m.RuleName).Distinct()), 400);

        if (message is not null)
        {
            var deleters = acting.Where(m => m.DeleteMessage).ToList();
            var timers = acting.Where(m => m.TimeoutMinutes is > 0).ToList();

            if (deleters.Count > 0)
            {
                var outcome = await _discord.DeleteMessageAsync(message.ChannelId, message.MessageId, reason, ct).ConfigureAwait(false);
                done.Add(new ActionDone(FactType.AutoModMessageDeleted, deleters, outcome.Done, outcome.Error, null));
            }

            if (timers.Count > 0)
            {
                // The longest any rule asked for (design §6).
                var minutes = timers.Max(m => m.TimeoutMinutes!.Value);
                var outcome = await _discord.TimeOutAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(minutes), reason, ct)
                    .ConfigureAwait(false);
                done.Add(new ActionDone(FactType.AutoModTimeout, timers, outcome.Done, outcome.Error, minutes));
            }

            return new Actions(done);
        }

        if (person.Platform != FactPlatform.VRChat)
            return new Actions(done);

        var banners = acting.Where(m => m.GroupBan).ToList();
        var removers = acting.Where(m => m.GroupRemove).ToList();

        if (banners.Count > 0)
        {
            var outcome = await _vrchat.BanFromGroupAsync(person.Id, reason, ct).ConfigureAwait(false);
            done.Add(new ActionDone(FactType.AutoModGroupBan, banners, outcome.Done, outcome.Error, null));

            // A ban takes the person out of the group as well, so a removal on top of one that
            // worked would be a second request for something VRChat has already done.
            if (outcome.Done)
                removers = [];
        }

        if (removers.Count > 0)
        {
            var outcome = await _vrchat.RemoveFromGroupAsync(person.Id, reason, ct).ConfigureAwait(false);
            done.Add(new ActionDone(FactType.AutoModGroupRemove, removers, outcome.Done, outcome.Error, null));
        }

        return new Actions(done);
    }

    /// <summary>
    /// Opens a review for each flag whose rule asks for one (design §19).
    /// </summary>
    /// <remarks>
    /// Inside the same transaction as the flags: a flag whose rule says "open a review for each
    /// flag" and has none is a flag nobody will be asked about.
    /// </remarks>
    private async Task OpenReviewsAsync(
        Rules rules, IReadOnlyList<ModerationFlag> flags, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var flag in flags)
        {
            IModerationRule? rule = (IModerationRule?)rules.Lists.FirstOrDefault(l => l.Id == flag.RuleId)
                                   ?? rules.Topics.FirstOrDefault(t => t.Id == flag.RuleId);

            if (rule is null || !rule.OpenReviewForEachFlag)
                continue;

            var review = FlagReviews.For(flag, now);
            _db.Reviews.Add(review);
            flag.ReviewId = review.Id;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await _reviewFacts.OpenedAsync(review, ct).ConfigureAwait(false);
        }
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
                Type = FactType.AutoModRulePaused,
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
            .Where(f => f.RuleId == ruleId && f.FlaggedAt >= since
                        && (f.MessageDeleted || f.TimedOutMinutes != null || f.GroupBanned || f.GroupRemoved))
            .CountAsync(ct);

    private static FactRecord FlagFact(ModerationFlag flag, DateTimeOffset now) => new()
    {
        Type = FactType.AutoModFlag,
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
            ["language"] = flag.Language,
            ["picture"] = flag.Picture,
            ["pictureUrl"] = flag.PictureUrl,
            ["contextMessageIds"] = JsonNode.Parse(flag.ContextMessageIds),
            ["reviewId"] = flag.ReviewId?.ToString(),
            ["subjectName"] = flag.SubjectName,
            ["channelId"] = flag.ChannelId,
            ["messageId"] = flag.MessageId,
            ["trial"] = flag.Trial,
            ["wouldDeleteMessage"] = flag.WouldDeleteMessage,
            ["wouldTimeOutMinutes"] = flag.WouldTimeOutMinutes,
            ["wouldGroupBan"] = flag.WouldGroupBan,
            ["wouldGroupRemove"] = flag.WouldGroupRemove,
        },
    };

    /// <summary>
    /// One action and every rule that asked for it, each with the operator who set it to act and
    /// the rule version it acted on (M8 §2, design §14).
    /// </summary>
    private static FactRecord ActionFact(
        ActionDone action,
        Rules rules,
        List<ModerationFlag> flags,
        Person person,
        DiscordMessageToCheck? message,
        DateTimeOffset now)
    {
        var ruleNodes = new JsonArray();

        foreach (var ruleId in action.AskedBy.Select(m => m.RuleId).Distinct())
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

        var data = new JsonObject
        {
            ["minutes"] = action.Minutes,
            ["done"] = action.Done,
            ["error"] = action.Error,
            ["flagIds"] = new JsonArray([.. flags.Where(f => action.AskedBy.Any(m => m.RuleId == f.RuleId)).Select(f => JsonValue.Create(f.Id.ToString()))]),
            ["rules"] = ruleNodes,
        };

        if (message is not null)
        {
            data["guildId"] = message.GuildId;
            data["channelId"] = message.ChannelId;
            data["messageId"] = message.MessageId;
        }

        return new FactRecord
        {
            Type = action.FactType,
            OccurredAt = now,
            SubjectPlatform = person.Platform,
            SubjectId = person.Id,
            Source = FactSource.Modbot,
            Data = data,
        };
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>
/// Keeps what an AI call was sent and what it answered, once a flag came of it. The call log lives
/// in <c>Modbot.AI</c>; a process without it keeps nothing.
/// </summary>
public interface IAiCallTexts
{
    Task KeepAsync(Guid callId, string prompt, string answer, CancellationToken ct);
}

public sealed class NoAiCallTexts : IAiCallTexts
{
    public Task KeepAsync(Guid callId, string prompt, string answer, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// A Discord message's image attachments, from the stored <c>jsonb</c> array (AI moderation
/// design §17).
/// </summary>
/// <remarks>
/// Here rather than beside the picture fetching in <c>Modbot.AI</c> because deciding which
/// attachments are pictures needs no AI: it is what the engine hands over, not what the model
/// gets. Discord gives each attachment a content type, and only the ones it calls a picture are
/// taken: an attachment named <c>cat.png</c> that Discord says is a zip is a zip.
/// </remarks>
public static class PictureAttachments
{
    /// <summary>The picture types Modbot sends. Anything else is skipped.</summary>
    public static IReadOnlyList<string> Types { get; } = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    public static IReadOnlyList<PictureSource> Of(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var found = new List<PictureSource>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = Text(item, "type");
                var url = Text(item, "url");
                var name = Text(item, "name") ?? "picture";

                if (url is null || type is null || !Types.Contains(Before(type, ';'), StringComparer.OrdinalIgnoreCase))
                    continue;

                found.Add(new PictureSource($"Attachment {name}", url));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Before(string text, char stop)
    {
        var at = text.IndexOf(stop);
        return (at < 0 ? text : text[..at]).Trim();
    }
}
