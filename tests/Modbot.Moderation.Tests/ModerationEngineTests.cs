using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.Moderation;
using Modbot.TestSupport;

namespace Modbot.Moderation.Tests;

/// <summary>
/// The engine on its own, over the real database, with fakes that count (AutoMod design §4 to
/// §6): term lists run with AI off and the AI is never asked; a switched-off tool is never
/// called; and a rule's actions go to the platform its match came from.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ModerationEngineTests
{
    private const string Guild = "900000000000000001";
    private const string Channel = "900000000000000002";

    private readonly PostgresFixture _db;

    public ModerationEngineTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── AI off ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithAiOff_TermRulesStillFlag_AndTheAiIsNeverAsked()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: false);

        await engine.ListAsync("Scams", "free nitro", targets: ModerationTargets.DiscordMessage);
        await engine.TopicAsync("Politics", targets: ModerationTargets.DiscordMessage);

        var flagged = await engine.Engine.CheckDiscordMessageAsync(Message("m1", "u1", "get FREE nitro now"), Ct);
        var clean = await engine.Engine.CheckDiscordMessageAsync(Message("m2", "u2", "vote for me"), Ct);

        Assert.Equal(1, flagged.FlagsWritten);
        Assert.Equal("free nitro", Assert.Single(flagged.Matches).Term);
        Assert.Empty(clean.Matches);
        Assert.Equal(NoAiRuleChecker.Reason, clean.AiSkipped);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task WithAiOffAndNoTopics_NothingSaysAiWasSkipped()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: false);

        await engine.ListAsync("Scams", "free nitro", targets: ModerationTargets.DiscordMessage);

        var outcome = await engine.Engine.CheckDiscordMessageAsync(Message("m1", "u1", "hello"), Ct);

        Assert.Empty(outcome.Matches);
        Assert.Null(outcome.AiSkipped);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task WithAutoModOff_NothingRunsAtAll()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: true, autoModOn: false);

        await engine.ListAsync("Scams", "free nitro", targets: ModerationTargets.DiscordMessage);
        await engine.TopicAsync("Politics", targets: ModerationTargets.DiscordMessage);

        var outcome = await engine.Engine.CheckDiscordMessageAsync(Message("m1", "u1", "free nitro, vote for me"), Ct);

        Assert.Empty(outcome.Matches);
        Assert.Equal(0, ai.Calls);
    }

    // ── The tool switches ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheTopicToolOff_TheAiIsNotAsked_AndTheOutcomeSaysWhy()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: true, tools: """{"classify_topics": false}""");

        await engine.TopicAsync("Politics", targets: ModerationTargets.DiscordMessage);

        var outcome = await engine.Engine.CheckDiscordMessageAsync(Message("m1", "u1", "vote for me"), Ct);

        Assert.Empty(outcome.Matches);
        Assert.Equal(ModerationEngine.ToolOff, outcome.AiSkipped);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task WithTheTopicToolOn_TheAiIsAskedOnce_AndItsHitBecomesAFlag()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: true);

        var topic = await engine.TopicAsync("Politics", targets: ModerationTargets.DiscordMessage);
        ai.Answer = (texts, _) => new AiRuleAnswer(
            [new AiRuleHit(texts[0].Key, topic.Id, "Campaigning.", "vote for me", Guid.NewGuid())],
            null, "m", new Dictionary<Guid, (string, string)>());

        var outcome = await engine.Engine.CheckDiscordMessageAsync(Message("m1", "u1", "please vote for me"), Ct);

        Assert.Equal(1, ai.Calls);
        Assert.Equal(1, outcome.FlagsWritten);
        Assert.Equal(ModerationRuleKind.Topic, Assert.Single(outcome.Matches).RuleKind);
        Assert.Empty(ai.Seen[0][0].Pictures);
    }

    [Fact]
    public async Task WithThePictureToolOff_NoPictureIsHandedOver_EvenWhenTheRuleAsks()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: true, tools: """{"check_pictures": false}""");

        await engine.TopicAsync("Avatars", targets: ModerationTargets.Bio, checkPictures: true);
        await engine.ProfileAsync("usr_1", bio: "look at me", picture: "https://cdn.example/me.png");

        await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_1", null, "look at me", null, null), Ct);

        Assert.Equal(1, ai.Calls);
        Assert.Empty(ai.Seen[0][0].Pictures);
    }

    [Fact]
    public async Task WithThePictureToolOn_TheRulesPicturesGoWithTheText()
    {
        var ai = new CountingAi();
        await using var engine = await StartAsync(ai: ai, aiOn: true);

        await engine.TopicAsync("Avatars", targets: ModerationTargets.Bio, checkPictures: true);
        await engine.ProfileAsync("usr_1", bio: "look at me", picture: "https://cdn.example/me.png");

        await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_1", null, "look at me", null, null), Ct);

        var picture = Assert.Single(ai.Seen[0][0].Pictures);
        Assert.Equal("VRChat profile picture", picture.Label);
        Assert.Equal("https://cdn.example/me.png", picture.Url);
    }

    // ── Actions ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AProfileMatchBansFromTheGroup_AndNeverTouchesDiscord()
    {
        var discord = new CountingDiscord();
        var vrchat = new CountingVRChat();
        await using var engine = await StartAsync(aiOn: false, discord: discord, vrchat: vrchat);

        await engine.ListAsync("Impersonation", "official modbot", targets: ModerationTargets.DisplayName, groupBan: true, acting: true);

        var outcome = await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_9", "Official Modbot", null, null, null), Ct);

        Assert.True(outcome.GroupBanned);
        Assert.False(outcome.GroupRemoved);
        Assert.False(outcome.MessageDeleted);
        Assert.Equal(["usr_9"], vrchat.Banned);
        Assert.Empty(vrchat.Removed);
        Assert.Empty(discord.Deleted);

        await using var db = _db.NewContext();
        var flag = await db.ModerationFlags.SingleAsync(Ct);
        Assert.True(flag.GroupBanned);
        Assert.True(flag.WouldGroupBan);

        var fact = await db.Events.SingleAsync(e => e.Type == FactType.AutoModGroupBan, Ct);
        Assert.Equal("usr_9", fact.SubjectId);
        // Parsed, not matched as a raw string: Postgres rewrites jsonb on the way in -- spaces
        // after colons, keys reordered -- so a literal substring asserts on Postgres's formatter.
        Assert.True(JsonDocument.Parse(fact.Data).RootElement.GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task ABanThatWorkedMeansNoRemovalIsSent_ButAFailedBanStillTriesTheRemoval()
    {
        var vrchat = new CountingVRChat();
        await using var engine = await StartAsync(aiOn: false, vrchat: vrchat);

        await engine.ListAsync("Both", "bad word", targets: ModerationTargets.Bio, groupBan: true, groupRemove: true, acting: true);

        var first = await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_1", null, "a bad word here", null, null), Ct);
        Assert.True(first.GroupBanned);
        Assert.False(first.GroupRemoved);
        Assert.Empty(vrchat.Removed);

        vrchat.BanFails = true;
        var second = await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_2", null, "another bad word", null, null), Ct);
        Assert.False(second.GroupBanned);
        Assert.True(second.GroupRemoved);
        Assert.Equal(["usr_2"], vrchat.Removed);
    }

    [Fact]
    public async Task AMessageMatchGoesToDiscord_AndAVRChatActionOnItIsNeverTaken()
    {
        var discord = new CountingDiscord();
        var vrchat = new CountingVRChat();
        await using var engine = await StartAsync(aiOn: false, discord: discord, vrchat: vrchat);

        // A rule on both kinds of target with both kinds of action. Saved directly, since the API
        // is what refuses a combination that could never act; the engine still has to sort it.
        await engine.ListAsync("Everything", "spam",
            targets: ModerationTargets.DiscordMessage | ModerationTargets.Bio,
            delete: true, groupBan: true, acting: true);

        var outcome = await engine.Engine.CheckDiscordMessageAsync(Message("m1", "u1", "spam spam"), Ct);

        Assert.True(outcome.MessageDeleted);
        Assert.False(outcome.GroupBanned);
        Assert.Equal([(Channel, "m1")], discord.Deleted);
        Assert.Empty(vrchat.Banned);

        var match = Assert.Single(outcome.Matches);
        Assert.True(match.DeleteMessage);
        Assert.False(match.GroupBan);
    }

    [Fact]
    public async Task DuringTheTrial_AGroupBanIsRecordedButNotTaken()
    {
        var vrchat = new CountingVRChat();
        await using var engine = await StartAsync(aiOn: false, vrchat: vrchat);

        await engine.ListAsync("Impersonation", "official modbot", targets: ModerationTargets.DisplayName, groupBan: true, acting: false);

        var outcome = await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_9", "Official Modbot", null, null, null), Ct);

        Assert.False(outcome.GroupBanned);
        Assert.Empty(vrchat.Banned);

        var match = Assert.Single(outcome.Matches);
        Assert.True(match.Trial);
        Assert.True(match.GroupBan);

        await using var db = _db.NewContext();
        var flag = await db.ModerationFlags.SingleAsync(Ct);
        Assert.True(flag.Trial);
        Assert.True(flag.WouldGroupBan);
        Assert.False(flag.GroupBanned);
    }

    [Fact]
    public async Task AFailedGroupActionIsAFactSayingSo_AndTheFlagRecordsNothingDone()
    {
        var vrchat = new CountingVRChat { BanFails = true };
        await using var engine = await StartAsync(aiOn: false, vrchat: vrchat);

        await engine.ListAsync("Impersonation", "official modbot", targets: ModerationTargets.DisplayName, groupBan: true, acting: true);

        var outcome = await engine.Engine.CheckProfileAsync(new ProfileToCheck("usr_9", "Official Modbot", null, null, null), Ct);

        Assert.False(outcome.GroupBanned);
        Assert.Equal(1, outcome.FlagsWritten);

        await using var db = _db.NewContext();
        Assert.False((await db.ModerationFlags.SingleAsync(Ct)).GroupBanned);

        var fact = await db.Events.SingleAsync(e => e.Type == FactType.AutoModGroupBan, Ct);
        // Parsed, not matched as a raw string: Postgres rewrites jsonb on the way in -- spaces
        // after colons, keys reordered -- so a literal substring asserts on Postgres's formatter.
        Assert.False(JsonDocument.Parse(fact.Data).RootElement.GetProperty("done").GetBoolean());
        Assert.Contains("VRChat said no", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryItNamesTheGroupActions_AndDoesNothing()
    {
        var vrchat = new CountingVRChat();
        await using var engine = await StartAsync(aiOn: false, vrchat: vrchat);

        await engine.ListAsync("Impersonation", "official modbot", targets: ModerationTargets.DisplayName, groupRemove: true, acting: true);

        var result = await engine.Engine.TryAsync("The Official Modbot", ModerationTargets.DisplayName, includeAi: false, ct: Ct);

        Assert.True(result.WouldGroupRemove);
        Assert.False(result.WouldGroupBan);
        Assert.Empty(vrchat.Removed);

        await using var db = _db.NewContext();
        Assert.Equal(0, await db.ModerationFlags.CountAsync(Ct));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<Harness> StartAsync(
        CountingAi? ai = null,
        bool aiOn = false,
        bool autoModOn = true,
        string tools = "{}",
        CountingDiscord? discord = null,
        CountingVRChat? vrchat = null)
    {
        await using (var reset = _db.NewContext())
        {
            await reset.ModerationFlags.ExecuteDeleteAsync(Ct);
            await reset.ModerationTermLists.ExecuteDeleteAsync(Ct);
            await reset.ModerationTopics.ExecuteDeleteAsync(Ct);
            await reset.Reviews.ExecuteDeleteAsync(Ct);
            await reset.VRChatUsers.Where(u => u.UserId.StartsWith("usr_")).ExecuteDeleteAsync(Ct);
            await reset.Database.ExecuteSqlRawAsync("DELETE FROM modbot_event", Ct);

            var settings = await reset.GetSettingsAsync(Ct);
            settings.AutoModEnabled = autoModOn;
            settings.AiEnabled = aiOn;
            settings.AutoModAiTools = tools;
            await reset.SaveChangesAsync(Ct);
        }

        var db = _db.NewContext();
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        var facts = new FactWriter(db, clock);
        var partitions = new EventPartitionMaintainer(db, clock);

        var engine = new ModerationEngine(
            db,
            ai ?? new CountingAi(),
            facts,
            partitions,
            clock,
            discord ?? new CountingDiscord(),
            vrchat ?? new CountingVRChat(),
            new CompiledTermLists(),
            new TextLanguage(),
            new ReviewFacts(facts, partitions, clock),
            new NoAiCallTexts());

        return new Harness(db, engine, clock);
    }

    private static DiscordMessageToCheck Message(string id, string author, string text)
        => new(Guild, Channel, id, author, author, text);

    /// <summary>The engine over one context, and the rows a test writes straight into the database.</summary>
    private sealed class Harness(ModbotContext db, ModerationEngine engine, FakeClock clock) : IAsyncDisposable
    {
        public ModerationEngine Engine { get; } = engine;

        public async Task<ModerationTermList> ListAsync(
            string name, string word, ModerationTargets targets,
            bool delete = false, bool groupBan = false, bool groupRemove = false, bool acting = false)
        {
            var now = clock.UtcNow;
            var list = new ModerationTermList
            {
                Id = Guid.CreateVersion7(now),
                Name = name,
                Enabled = true,
                Targets = (int)targets,
                Terms = StoredTerm.Serialize([new StoredTerm("t1", TermKind.Word, Text: word)]),
                DeleteMessage = delete,
                GroupBan = groupBan,
                GroupRemove = groupRemove,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // Past the trial when asked to act for real; in it otherwise (AI moderation design §13.1).
            if (delete || groupBan || groupRemove)
            {
                list.TrialStartedAt = now.AddDays(-8);
                list.TrialEndedAt = acting ? now.AddDays(-1) : null;
            }

            db.ModerationTermLists.Add(list);
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            return list;
        }

        public async Task<ModerationTopic> TopicAsync(string name, ModerationTargets targets, bool checkPictures = false)
        {
            var now = clock.UtcNow;
            var topic = new ModerationTopic
            {
                Id = Guid.CreateVersion7(now),
                Name = name,
                Instructions = "Anything about " + name.ToLowerInvariant() + ".",
                Enabled = true,
                Targets = (int)targets,
                CheckPictures = checkPictures,
                ContextMessages = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.ModerationTopics.Add(topic);
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            return topic;
        }

        public async Task ProfileAsync(string userId, string? bio, string? picture)
        {
            db.VRChatUsers.Add(new VRChatUser
            {
                UserId = userId,
                DisplayName = userId,
                Bio = bio,
                ProfilePictureUrl = picture,
            });
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    /// <summary>An AI that counts how often it was asked and keeps what it was handed.</summary>
    private sealed class CountingAi : IAiRuleChecker
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<AiRuleText>> Seen { get; } = [];

        public Func<IReadOnlyList<AiRuleText>, AiAsker, AiRuleAnswer> Answer { get; set; } =
            (_, _) => new AiRuleAnswer([], null, "m", new Dictionary<Guid, (string, string)>());

        public Task<AiRuleAnswer> CheckAsync(IReadOnlyList<AiRuleText> texts, AiAsker asker, CancellationToken ct)
        {
            Calls++;
            Seen.Add(texts);
            return Task.FromResult(Answer(texts, asker));
        }
    }

    private sealed class CountingDiscord : IDiscordModerationActions
    {
        public List<(string ChannelId, string MessageId)> Deleted { get; } = [];

        public List<(string GuildId, string UserId, TimeSpan Duration)> TimedOut { get; } = [];

        public Task<DiscordActionOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
        {
            Deleted.Add((channelId, messageId));
            return Task.FromResult(DiscordActionOutcome.Ok);
        }

        public Task<DiscordActionOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
        {
            TimedOut.Add((guildId, userId, duration));
            return Task.FromResult(DiscordActionOutcome.Ok);
        }
    }

    private sealed class CountingVRChat : IVRChatModerationActions
    {
        public List<string> Banned { get; } = [];

        public List<string> Removed { get; } = [];

        public bool BanFails { get; set; }

        public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        {
            if (BanFails)
                return Task.FromResult(VRChatActionOutcome.Failed("VRChat said no."));

            Banned.Add(userId);
            return Task.FromResult(VRChatActionOutcome.Ok);
        }

        public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        {
            Removed.Add(userId);
            return Task.FromResult(VRChatActionOutcome.Ok);
        }

        // Role and ban sync's half of the interface. No AutoMod rule reaches these.
        public Task<VRChatActionOutcome> UnbanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
            => Task.FromResult(VRChatActionOutcome.Ok);

        public Task<VRChatActionOutcome> GiveGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
            => Task.FromResult(VRChatActionOutcome.Ok);

        public Task<VRChatActionOutcome> TakeGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
            => Task.FromResult(VRChatActionOutcome.Ok);
    }
}
