using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Search;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Search;

/// <summary>
/// The one search the command palette asks: people, Discord people and worlds by name or id,
/// each list narrowed by the permission that gates the page it would otherwise be found on.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SearchTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public SearchTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;

        db.VRChatUsers.AddRange(
            new VRChatUser { UserId = "usr_alice", DisplayName = "Alice Wonder", FirstSeenAt = Day, LastSeenAt = Day },
            new VRChatUser { UserId = "usr_bob", DisplayName = "Bob_Builder", FirstSeenAt = Day, LastSeenAt = Day },
            new VRChatUser { UserId = "8JoV9XEdpo", DisplayName = null, FirstSeenAt = Day, LastSeenAt = Day });

        db.DiscordMembers.AddRange(
            new DiscordMember { GuildId = Guild, UserId = "1", Username = "ada", DisplayName = "Ada the Brave", Nickname = "Ada the Brave", FirstSeenAt = Day, UpdatedAt = Day, JoinedAt = Day },
            new DiscordMember { GuildId = Guild, UserId = "2", Username = "alice_d", DisplayName = "Alice D", FirstSeenAt = Day, UpdatedAt = Day, JoinedAt = Day, LeftAt = Day.AddHours(1) },
            new DiscordMember { GuildId = "777", UserId = "9", Username = "alice_elsewhere", DisplayName = "Alice Elsewhere", FirstSeenAt = Day, UpdatedAt = Day });

        db.VRChatWorlds.AddRange(
            new VRChatWorld { WorldId = "wrld_cat", Name = "The Black Cat", FirstSeenAt = Day, LastSeenAt = Day },
            new VRChatWorld { WorldId = "wrld_alice", Name = null, FirstSeenAt = Day, LastSeenAt = Day });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task FindsPeopleDiscordPeopleAndWorldsByNameOrId()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ViewAnalytics, Ct);
        var found = await host.GetJsonAsync<SearchResults>("/api/search?q=alice", cookie, Ct);

        Assert.Equal(["usr_alice"], found.People.Select(p => p.UserId));
        Assert.Equal("Alice Wonder", found.People[0].DisplayName);

        // Discord people from the server in settings only, current members first, left ones marked.
        Assert.Equal(["2"], found.DiscordPeople.Select(p => p.UserId));
        Assert.False(found.DiscordPeople[0].InServer);

        // A world with no name yet is still found by its id.
        Assert.Equal(["wrld_alice"], found.Worlds.Select(w => w.WorldId));
    }

    [Fact]
    public async Task ALegacyIdIsFoundByTypingPartOfIt()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var found = await host.GetJsonAsync<SearchResults>("/api/search?q=JoV9", cookie, Ct);

        Assert.Equal(["8JoV9XEdpo"], found.People.Select(p => p.UserId));
    }

    [Fact]
    public async Task AKindTheCallerMayNotSeeComesBackEmptyRatherThanRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var members = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var forMembers = await host.GetJsonAsync<SearchResults>("/api/search?q=cat", members, Ct);
        Assert.Empty(forMembers.Worlds);

        var analytics = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var forAnalytics = await host.GetJsonAsync<SearchResults>("/api/search?q=cat", analytics, Ct);
        Assert.Equal(["wrld_cat"], forAnalytics.Worlds.Select(w => w.WorldId));
        Assert.Empty(forAnalytics.People);
        Assert.Empty(forAnalytics.DiscordPeople);
    }

    [Fact]
    public async Task NothingTypedFindsNothing()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ViewAnalytics, Ct);
        var found = await host.GetJsonAsync<SearchResults>("/api/search?q=%20", cookie, Ct);

        Assert.Empty(found.People);
        Assert.Empty(found.DiscordPeople);
        Assert.Empty(found.Worlds);
    }
}
