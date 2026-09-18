using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests;

/// <summary>
/// The pieces the bot resolves from a scope, wired over one isolated database: a context, the
/// fact writer, the command handler and the poster. No hosted services are started.
/// </summary>
public sealed class TestServices : IAsyncDisposable
{
    private TestServices(IsolatedDatabase database, ServiceProvider provider, FakeClock clock, DiscordBotStatus status, RecordingChecker checker)
    {
        Database = database;
        Provider = provider;
        Clock = clock;
        Status = status;
        Checker = checker;
    }

    public IsolatedDatabase Database { get; }

    public ServiceProvider Provider { get; }

    public FakeClock Clock { get; }

    public DiscordBotStatus Status { get; }

    /// <summary>Every message the handler passed to AI moderation.</summary>
    public RecordingChecker Checker { get; }

    public FakeSecretProtector Protector { get; } = new();

    public static async Task<TestServices> CreateAsync(PostgresFixture fixture, CancellationToken ct)
    {
        var database = await IsolatedDatabase.CreateAsync(fixture, ct);
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero));
        var status = new DiscordBotStatus();
        var protector = new FakeSecretProtector();
        var checker = new RecordingChecker();

        var services = new ServiceCollection();
        services.AddDbContext<ModbotContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddSingleton<IModbotClock>(clock);
        services.AddSingleton<ISecretProtector>(protector);
        services.AddSingleton(status);
        services.AddScoped<IFactWriter, FactWriter>();
        services.AddScoped<EventPartitionMaintainer>();
        services.AddScoped<LookupQuery>();
        services.AddScoped<DiscordCommandHandler>();
        services.AddScoped<ModerationLogPoster>();
        services.AddScoped<Modbot.Discord.Instances.InstanceAnnouncer>();
        services.AddScoped<Modbot.Discord.Calendar.CalendarDiscordPublisher>();
        services.AddScoped<Modbot.Analytics.Giveaways.GiveawayRuleChecker>();
        services.AddScoped<Modbot.Analytics.Giveaways.GiveawayDrawer>();
        services.AddScoped<Modbot.Discord.Giveaways.GiveawayReactions>();
        services.AddScoped<Modbot.Discord.Giveaways.GiveawayDiscordPublisher>();
        services.AddScoped<Modbot.Discord.ServerIndex.DiscordServerIndex>();
        services.AddSingleton<Modbot.Core.Discord.DiscordLinkSignal>();
        services.AddScoped<Modbot.Discord.Linking.LinkedRoles>();
        services.AddScoped<Modbot.Discord.Linking.LinkPrompt>();
        services.AddScoped<Modbot.Analytics.Messages.MessagePartitionMaintainer>();
        services.AddScoped<Modbot.Discord.Messages.DiscordMessageStore>();
        services.AddScoped<Modbot.Discord.Messages.DiscordMessageHandler>();
        services.AddScoped<Modbot.Discord.Members.DiscordEventRecorder>();
        services.AddScoped<Modbot.Analytics.DailyTotals.IDailyTotalCounter, Modbot.Analytics.DailyTotals.DailyTotalCounter>();
        services.AddSingleton(checker);
        services.AddScoped<Modbot.Core.Moderation.IModerationChecker>(p => p.GetRequiredService<RecordingChecker>());

        var provider = services.BuildServiceProvider();
        var built = new TestServices(database, provider, clock, status, checker);

        // Facts in these tests all fall around the fake clock's month.
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureAsync(ct);

        return built;
    }

    public IServiceScope Scope() => Provider.CreateScope();

    public async Task ConfigureAsync(Action<Settings> change, CancellationToken ct)
    {
        await using var db = Database.NewContext();
        var settings = await db.GetSettingsAsync(ct);
        change(settings);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds a route sending to <paramref name="channelId"/>. Every moderation action unless told otherwise.</summary>
    public async Task<DiscordEventRoute> AddRouteAsync(
        string channelId,
        IEnumerable<string>? types = null,
        Action<DiscordEventRoute>? more = null,
        CancellationToken ct = default)
    {
        await using var db = Database.NewContext();
        var position = await db.DiscordEventRoutes.MaxAsync(r => (int?)r.Position, ct) ?? -1;

        var route = new DiscordEventRoute
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            EventTypes = (types ?? Modbot.Core.Discord.ModerationLogEvents.Allowed).ToList(),
            Position = position + 1,
        };
        more?.Invoke(route);

        db.DiscordEventRoutes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    public async Task ChangeRouteAsync(Guid id, Action<DiscordEventRoute> change, CancellationToken ct)
    {
        await using var db = Database.NewContext();
        var route = await db.DiscordEventRoutes.FirstAsync(r => r.Id == id, ct);
        change(route);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Where a channel has got to, or null when it has no row.</summary>
    public async Task<DiscordEventChannel?> ChannelPlaceAsync(string channelId, CancellationToken ct)
    {
        await using var db = Database.NewContext();
        return await db.DiscordEventChannels.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelId == channelId, ct);
    }

    public async Task<Settings> SettingsAsync(CancellationToken ct)
    {
        await using var db = Database.NewContext();
        return await db.Settings.AsNoTracking().FirstAsync(s => s.Id == 1, ct);
    }

    /// <summary>Writes one audit-log style fact, shaped like the ones the audit sync produces.</summary>
    public async Task<long> WriteAuditFactAsync(
        string type,
        string subjectId,
        string? actorId = null,
        string? actorDisplayName = null,
        string? description = null,
        DateTimeOffset? at = null,
        CancellationToken ct = default)
    {
        var data = new JsonObject
        {
            ["eventType"] = "group." + type[(type.LastIndexOf('.') + 1)..],
            ["auditEntryId"] = "gaud_" + Guid.NewGuid().ToString("n"),
        };

        if (actorDisplayName is not null)
            data["actorDisplayName"] = actorDisplayName;

        if (description is not null)
            data["description"] = description;

        return await WriteFactAsync(new FactRecord
        {
            Type = type,
            OccurredAt = at ?? Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subjectId,
            ActorPlatform = actorId is null ? null : FactPlatform.VRChat,
            ActorId = actorId,
            Source = FactSource.AuditLog,
            Data = data,
        }, ct);
    }

    public async Task<long> WriteFactAsync(FactRecord fact, CancellationToken ct)
    {
        using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(fact.OccurredAt, ct);
        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(fact, ct);
        return result.Id;
    }

    public async Task<IReadOnlyList<ModbotEvent>> FactsOfTypeAsync(string type, CancellationToken ct)
    {
        await using var db = Database.NewContext();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.Id).ToListAsync(ct);
    }

    public async Task<long> NewestFactIdAsync(CancellationToken ct)
    {
        await using var db = Database.NewContext();
        return await db.Events.AsNoTracking().MaxAsync(e => (long?)e.Id, ct) ?? 0;
    }

    public async Task AddProfileAsync(string userId, string displayName, Action<VRChatUser>? more = null, CancellationToken ct = default)
    {
        await using var db = Database.NewContext();
        var user = new VRChatUser
        {
            UserId = userId,
            DisplayName = displayName,
            FirstSeenAt = Clock.UtcNow,
            LastSeenAt = Clock.UtcNow,
            LastRefreshedAt = Clock.UtcNow,
        };
        more?.Invoke(user);
        db.VRChatUsers.Add(user);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A Modbot account linked to a Discord user id, holding these permissions.</summary>
    public async Task<ModbotUser> LinkedAccountAsync(
        string discordUserId, ModbotPermissions permissions, bool disabled = false, CancellationToken ct = default)
    {
        await using var db = Database.NewContext();
        var user = await TestAccounts.CreateAsync(db, "user_" + Guid.NewGuid().ToString("n")[..8], TestAccounts.Password, permissions, linked: true, ct);
        user.DiscordUserId = discordUserId;
        user.IsDisabled = disabled;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await Database.DisposeAsync();
    }
}
