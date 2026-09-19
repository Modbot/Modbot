using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// The Discord member list read a numbered page at a time, in each of the three orders it offers.
/// </summary>
/// <remarks>
/// This is the list whose orderings run opposite ways -- newest first, oldest first, by name --
/// while the id that breaks ties runs the same way in all three. The Discord bot reads the whole
/// member list again on every sign-in and stamps a batch of rows with one moment, so a page
/// boundary landing on a tie is the ordinary case rather than a contrived one.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class DiscordMemberPagingTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public DiscordMemberPagingTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="people">Id, shown name, and when they joined.</param>
    private static async Task SeedAsync(
        ReadSurfaceTestHost host, params (string Id, string Name, DateTimeOffset Joined)[] people)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var at = host.Clock.UtcNow;
        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;

        if (!context.DiscordServers.Local.Any() && !context.DiscordServers.Any(s => s.GuildId == Guild))
        {
            context.DiscordServers.Add(new DiscordServer
            {
                GuildId = Guild, Name = "The Black Cat", RefreshedAt = at, UpdatedAt = at, MembersListedAt = at,
            });
        }

        foreach (var (id, name, joined) in people)
        {
            context.DiscordMembers.Add(new DiscordMember
            {
                GuildId = Guild,
                UserId = id,
                Username = name.ToLowerInvariant(),
                DisplayName = name,
                GlobalName = name,
                Roles = "[]",
                JoinedAt = joined,
                FirstSeenAt = joined,
                UpdatedAt = at,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    private static async Task<List<string>> WalkAsync(ReadSurfaceTestHost host, string cookie, string sort, int size)
    {
        var read = new List<string>();

        var first = await host.GetJsonAsync<DiscordMemberListResponse>(
            $"/api/discord/members?sort={sort}&page=1&pageSize={size}", cookie, Ct);

        var pages = (first.Total + size - 1) / size;
        read.AddRange(first.Members.Select(m => m.UserId));

        for (var page = 2; page <= pages; page++)
        {
            var next = await host.GetJsonAsync<DiscordMemberListResponse>(
                $"/api/discord/members?sort={sort}&page={page}&pageSize={size}", cookie, Ct);

            Assert.Equal(page, next.Page);
            read.AddRange(next.Members.Select(m => m.UserId));
        }

        return read;
    }

    [Theory]
    [InlineData("joined")]
    [InlineData("oldest")]
    [InlineData("name")]
    public async Task ThePagesJoinUpInEveryOrderTheListOffers(string sort)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day.AddDays(-5)),
            ("2", "Bo", day.AddDays(-4)),
            ("3", "Cy", day.AddDays(-3)),
            ("4", "Di", day.AddDays(-2)),
            ("5", "Eve", day.AddDays(-1)));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var whole = await WalkAsync(host, cookie, sort, 5);
        var inTwos = await WalkAsync(host, cookie, sort, 2);

        Assert.Equal(5, whole.Count);
        Assert.Equal(whole, inTwos);
        Assert.Equal(whole.Distinct(), whole);
    }

    [Fact]
    public async Task NewestFirstAndOldestFirstAreTheSameListTheOtherWayRound()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day.AddDays(-4)),
            ("2", "Bo", day.AddDays(-3)),
            ("3", "Cy", day.AddDays(-2)),
            ("4", "Di", day.AddDays(-1)));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var newest = await WalkAsync(host, cookie, "joined", 2);
        var oldest = await WalkAsync(host, cookie, "oldest", 2);

        Assert.Equal(["4", "3", "2", "1"], newest);
        Assert.Equal(["1", "2", "3", "4"], oldest);
    }

    [Fact]
    public async Task EverybodyJoiningAtOnceStillPagesThroughWithoutRepeatingAnybody()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // One read of the whole server stamps every row with the same moment, so the id is the
        // only thing left deciding where a page boundary falls.
        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day),
            ("2", "Bo", day),
            ("3", "Cy", day),
            ("4", "Di", day));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var read = await WalkAsync(host, cookie, "joined", 2);

        Assert.Equal(["1", "2", "3", "4"], read);
    }

    [Fact]
    public async Task APagePastTheEndIsEmptyAndStillSaysHowBigTheListIs()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host, ("1", "Ada", day.AddDays(-2)), ("2", "Bo", day.AddDays(-1)));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var far = await host.GetJsonAsync<DiscordMemberListResponse>(
            "/api/discord/members?page=40&pageSize=2", cookie, Ct);

        Assert.Empty(far.Members);
        Assert.Equal(2, far.Total);
        Assert.Equal(40, far.Page);
    }
}
