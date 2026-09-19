using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Security;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Giveaways;

/// <summary>Reversible and obviously not encryption. What matters is that the drawer reads through it.</summary>
public sealed class PlainProtector : ISecretProtector
{
    private const string Prefix = "protected:";

    public string Protect(string plaintext) => Prefix + plaintext;

    public string? Unprotect(string? ciphertext)
        => ciphertext is not null && ciphertext.StartsWith(Prefix, StringComparison.Ordinal)
            ? ciphertext[Prefix.Length..]
            : null;
}

/// <summary>
/// A private database and the shorthand for filling it with the people a giveaway's rules ask
/// about: group members, Discord members, links, presence, voice and messages.
/// </summary>
/// <remarks>
/// Its own database rather than the shared one, like the daily totals suite's: the checker sums
/// over every presence fact in the table, so another test's facts would change its answers.
/// </remarks>
public abstract class GiveawayTestBase : IAsyncLifetime
{
    /// <summary>
    /// Fixed. A suite that quietly depends on today's date starts failing at a month boundary for
    /// reasons nobody will connect to it.
    /// </summary>
    protected static readonly DateTimeOffset Now = new(2029, 3, 10, 12, 0, 0, TimeSpan.Zero);

    protected const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";
    protected const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    protected GiveawayTestBase(PostgresFixture fixture) => Fixture = fixture;

    protected PostgresFixture Fixture { get; }

    protected IsolatedDatabase Database { get; private set; } = null!;

    protected FakeClock Clock { get; } = new(Now);

    protected PlainProtector Protector { get; } = new();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Database = await IsolatedDatabase.CreateAsync(Fixture, Ct);

        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        await context.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    protected GiveawayRuleChecker NewChecker(ModbotContext context) => new(context, Clock);

    protected GiveawayDrawer NewDrawer(ModbotContext context) => new(
        context,
        Clock,
        Protector,
        NewChecker(context),
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    // ── The people ───────────────────────────────────────────────────────────────────────

    /// <summary>A person in the group, in the server, or both, with the dates a rule asks for.</summary>
    protected async Task AddPersonAsync(
        string? vrchat = null,
        string? discord = null,
        string? name = null,
        int? inGroupDays = null,
        int? inDiscordDays = null,
        int? accountDays = null,
        IEnumerable<string>? groupRoles = null,
        IEnumerable<string>? discordRoles = null,
        bool link = false,
        bool banned = false,
        bool leftGroup = false,
        bool leftDiscord = false,
        TrustRank? trustRank = null,
        bool verified18Plus = false)
    {
        await using var context = Database.NewContext();

        if (vrchat is not null)
        {
            context.VRChatUsers.Add(new VRChatUser
            {
                UserId = vrchat,
                DisplayName = name ?? vrchat,
                DateJoined = accountDays is { } days ? DateOnly.FromDateTime(Now.AddDays(-days).UtcDateTime) : null,
                TrustRank = trustRank,
                Is18PlusVerified = verified18Plus,
                FirstSeenAt = Now,
                LastSeenAt = Now,
            });

            if (inGroupDays is { } groupDays)
            {
                context.GroupMembers.Add(new GroupMember
                {
                    GroupId = Group,
                    UserId = vrchat,
                    Roles = JsonSerializer.Serialize(groupRoles?.ToList() ?? []),
                    JoinedAt = Now.AddDays(-groupDays),
                    FirstSeenAt = Now,
                    LastSeenAt = Now,
                    LeftAt = leftGroup ? Now.AddDays(-1) : null,
                });
            }

            if (banned)
            {
                context.GroupBans.Add(new GroupBan
                {
                    GroupId = Group,
                    UserId = vrchat,
                    BannedAt = Now.AddDays(-2),
                    FirstSeenAt = Now,
                    LastSeenAt = Now,
                });
            }
        }

        if (discord is not null)
        {
            context.DiscordMembers.Add(new DiscordMember
            {
                GuildId = "guild",
                UserId = discord,
                Username = name ?? discord,
                DisplayName = name ?? discord,
                Roles = JsonSerializer.Serialize(discordRoles?.ToList() ?? []),
                JoinedAt = inDiscordDays is { } discordDays ? Now.AddDays(-discordDays) : null,
                FirstSeenAt = Now,
                LeftAt = leftDiscord ? Now.AddDays(-1) : null,
                UpdatedAt = Now,
            });
        }

        if (link && vrchat is not null && discord is not null)
        {
            context.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                Id = Guid.CreateVersion7(),
                DiscordUserId = discord,
                DiscordUsername = name ?? discord,
                VRChatUserId = vrchat,
                VRChatDisplayName = name,
                LinkedAt = Now.AddDays(-10),
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Presence: one arrival and one leave, so the session is exactly <paramref name="hours"/> long.
    /// </summary>
    /// <param name="daysAgo">How long ago the session ended.</param>
    protected async Task SeenAsync(string vrchat, double hours, int daysAgo = 1, string instance = "12345")
    {
        var ended = Now.AddDays(-daysAgo);
        var started = ended.AddHours(-hours);

        await WriteAsync(
            Fact(FactType.InstanceJoined, started, vrchat, instance),
            Fact(FactType.InstanceLeft, ended, vrchat, instance));
    }

    protected async Task VoiceMinutesAsync(string discord, decimal minutes, int daysAgo = 1)
        => await DailyTotalAsync(
            DailyTotalMetrics.DiscordMemberVoiceMinutes,
            DailyTotalDimensions.ForUser(FactPlatform.Discord, discord),
            minutes,
            daysAgo);

    protected async Task MessagesAsync(string discord, decimal messages, int daysAgo = 1)
        => await DailyTotalAsync(
            DailyTotalMetrics.DiscordMemberMessages,
            DailyTotalDimensions.ForUser(FactPlatform.Discord, discord),
            messages,
            daysAgo);

    private async Task DailyTotalAsync(string metric, string dimension, decimal value, int daysAgo)
    {
        await using var context = Database.NewContext();

        context.DailyTotals.Add(new DailyTotal
        {
            Day = DateOnly.FromDateTime(Now.AddDays(-daysAgo).UtcDateTime),
            Metric = metric,
            Dimension = dimension,
            Value = value,
            Origin = DailyTotalOrigin.Computed,
        });

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>A ban, a kick or an instance kick about somebody.</summary>
    protected async Task TroubleAsync(string subjectId, FactPlatform platform, int daysAgo = 1)
    {
        await using var context = Database.NewContext();
        var partitions = new EventPartitionMaintainer(context, Clock);
        var at = Now.AddDays(-daysAgo);
        await partitions.EnsureForAsync(at, Ct);

        await new FactWriter(context, Clock).WriteAsync(
            new FactRecord
            {
                Type = platform == FactPlatform.VRChat ? FactType.MemberBanned : FactType.DiscordMemberBanned,
                OccurredAt = at,
                SubjectPlatform = platform,
                SubjectId = subjectId,
                Source = FactSource.AuditLog,
            },
            Ct);
    }

    protected async Task RetentionAsync(int moderationDays, int presenceDays)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.ModerationFactRetentionDays = moderationDays;
        settings.PresenceFactRetentionDays = presenceDays;
        await context.SaveChangesAsync(Ct);
    }

    // ── Giveaways ────────────────────────────────────────────────────────────────────────

    protected async Task<Giveaway> AddGiveawayAsync(
        GiveawayRule? rules = null,
        GiveawayExclusions? exclusions = null,
        string weighting = GiveawayWeights.Uniform,
        long? weightCap = null,
        int winners = 1,
        string entryWay = GiveawayEntryWays.Automatic,
        string state = GiveawayStates.Closed)
    {
        await using var context = Database.NewContext();

        var giveaway = new Giveaway
        {
            Id = Guid.CreateVersion7(),
            Name = "A prize",
            Prize = "A hat",
            OpensAt = Now.AddDays(-7),
            ClosesAt = Now.AddHours(-1),
            WinnerCount = winners,
            EntryWay = entryWay,
            Rules = GiveawayRules.Store(rules ?? GiveawayRule.Everyone),
            Exclusions = (exclusions ?? GiveawayExclusions.None).Store(),
            Weighting = weighting,
            WeightCap = weightCap,
            State = state,
            CreatedAt = Now.AddDays(-7),
            UpdatedAt = Now.AddDays(-7),
        };

        // The same promise the API makes when a giveaway is created.
        NewDrawer(context).Promise(giveaway);

        context.Giveaways.Add(giveaway);
        await context.SaveChangesAsync(Ct);
        return giveaway;
    }

    protected async Task ReactedAsync(Guid giveawayId, string discordUserId, bool withdrawn = false)
    {
        await using var context = Database.NewContext();

        context.GiveawayEntries.Add(new GiveawayEntry
        {
            GiveawayId = giveawayId,
            DiscordUserId = discordUserId,
            EnteredAt = Now.AddDays(-2),
            WithdrawnAt = withdrawn ? Now.AddDays(-1) : null,
            QualifiedOnEntry = true,
        });

        await context.SaveChangesAsync(Ct);
    }

    protected async Task<GiveawayMatch> MatchAsync(Giveaway giveaway)
    {
        await using var context = Database.NewContext();

        return await NewChecker(context).SnapshotAsync(
            GiveawayRules.ReadStored(giveaway.Rules),
            GiveawayExclusions.ReadStored(giveaway.Exclusions),
            giveaway.Weighting,
            giveaway.WeightCap,
            entrants: null,
            Ct);
    }

    /// <summary>Who a rule lets through, by key, in snapshot order.</summary>
    protected async Task<IReadOnlyList<string>> WhoMatchesAsync(GiveawayRule rule)
    {
        await using var context = Database.NewContext();

        var match = await NewChecker(context).SnapshotAsync(
            rule, GiveawayExclusions.None, GiveawayWeights.Uniform, null, entrants: null, Ct);

        Assert.Null(match.Unanswerable);

        return [.. match.People.Where(p => p.KeptOut.Length == 0).Select(p => p.Key)];
    }

    protected async Task<IReadOnlyList<GiveawayEntrant>> EntrantsAsync(Guid drawId)
    {
        await using var context = Database.NewContext();

        return await context.GiveawayEntrants.AsNoTracking()
            .Where(e => e.DrawId == drawId)
            .OrderBy(e => e.Position)
            .ToListAsync(Ct);
    }

    private static FactRecord Fact(string type, DateTimeOffset at, string subjectId, string instance) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subjectId,
        WorldId = World,
        InstanceId = instance,
        Source = FactSource.Client,
    };

    private async Task WriteAsync(params FactRecord[] facts)
    {
        await using var context = Database.NewContext();
        var partitions = new EventPartitionMaintainer(context, Clock);

        foreach (var fact in facts)
            await partitions.EnsureForAsync(fact.OccurredAt, Ct);

        await new FactWriter(context, Clock).WriteManyAsync(facts, Ct);
    }
}
