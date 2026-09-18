using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// The Discord member list paged by cursor, in each of the three orders it offers.
/// </summary>
/// <remarks>
/// This list is the one with orderings that run opposite ways -- newest first, oldest first, by
/// name -- and the id that breaks ties runs the same way in all three. Reading backwards turns
/// the ordering round but not the tie-break's place in it, which is exactly the thing that is
/// easy to get wrong and impossible to notice without a page boundary landing on a tie.
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

    [Fact]
    public async Task NewestFirstPagesForwardsAndBackWithoutLosingARow()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day.AddDays(-5)),
            ("2", "Bo", day.AddDays(-4)),
            ("3", "Cy", day.AddDays(-3)),
            ("4", "Di", day.AddDays(-2)));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<DiscordMemberListResponse>(
            "/api/discord/members?pageSize=2", cookie, Ct);

        Assert.Equal(["4", "3"], first.Members.Select(m => m.UserId));

        var second = await host.GetJsonAsync<DiscordMemberListResponse>(At(first.Next), cookie, Ct);
        Assert.Equal(["2", "1"], second.Members.Select(m => m.UserId));

        var back = await host.GetJsonAsync<DiscordMemberListResponse>(At(second.Previous), cookie, Ct);
        Assert.Equal(["4", "3"], back.Members.Select(m => m.UserId));
        Assert.Null(back.Previous);
    }

    [Fact]
    public async Task OldestFirstPagesTheOtherWayRound()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day.AddDays(-5)),
            ("2", "Bo", day.AddDays(-4)),
            ("3", "Cy", day.AddDays(-3)),
            ("4", "Di", day.AddDays(-2)));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<DiscordMemberListResponse>(
            "/api/discord/members?sort=oldest&pageSize=2", cookie, Ct);

        Assert.Equal(["1", "2"], first.Members.Select(m => m.UserId));

        var second = await host.GetJsonAsync<DiscordMemberListResponse>(At(first.Next, "oldest"), cookie, Ct);
        Assert.Equal(["3", "4"], second.Members.Select(m => m.UserId));

        var back = await host.GetJsonAsync<DiscordMemberListResponse>(At(second.Previous, "oldest"), cookie, Ct);
        Assert.Equal(["1", "2"], back.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task EverybodyJoiningAtOnceStillPagesThroughAllOfThem()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // What the first read of a server that was imported looks like: one moment for everybody.
        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day),
            ("2", "Bo", day),
            ("3", "Cy", day),
            ("4", "Di", day),
            ("5", "Ed", day));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var seen = new List<string>();
        var url = "/api/discord/members?pageSize=2";

        for (var read = 0; read < 6; read++)
        {
            var page = await host.GetJsonAsync<DiscordMemberListResponse>(url, cookie, Ct);
            seen.AddRange(page.Members.Select(m => m.UserId));

            if (page.Next is null)
                break;

            url = At(page.Next);
        }

        Assert.Equal(["1", "2", "3", "4", "5"], seen);
    }

    [Fact]
    public async Task ByNameReadsBackAsWellAsForwards()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day),
            ("2", "Bo", day),
            ("3", "Cy", day),
            ("4", "Di", day));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<DiscordMemberListResponse>(
            "/api/discord/members?sort=name&pageSize=2", cookie, Ct);

        Assert.Equal(["Ada", "Bo"], first.Members.Select(m => m.DisplayName));

        var second = await host.GetJsonAsync<DiscordMemberListResponse>(At(first.Next, "name"), cookie, Ct);
        Assert.Equal(["Cy", "Di"], second.Members.Select(m => m.DisplayName));

        var back = await host.GetJsonAsync<DiscordMemberListResponse>(At(second.Previous, "name"), cookie, Ct);
        Assert.Equal(["Ada", "Bo"], back.Members.Select(m => m.DisplayName));
    }

    [Fact]
    public async Task ACursorFromAnotherOrderingShowsTheFirstPageRatherThanNonsense()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var day = host.Clock.UtcNow;
        await SeedAsync(host,
            ("1", "Ada", day.AddDays(-3)),
            ("2", "Bo", day.AddDays(-2)),
            ("3", "Cy", day.AddDays(-1)));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var byName = await host.GetJsonAsync<DiscordMemberListResponse>(
            "/api/discord/members?sort=name&pageSize=2", cookie, Ct);

        // Somebody changes the sort with a cursor still in the address: "Bo" is not a join date,
        // and the rows it would pick out would be arbitrary. The first page is the honest answer.
        var muddled = await host.GetJsonAsync<DiscordMemberListResponse>(At(byName.Next), cookie, Ct);

        Assert.Equal(["3", "2"], muddled.Members.Select(m => m.UserId));
        Assert.Null(muddled.Previous);
    }

    [Fact]
    public async Task ThePageNumberStillWorksForWhoeverWasAlreadySendingOne()
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

        var second = await host.GetJsonAsync<DiscordMemberListResponse>(
            "/api/discord/members?pageSize=2&page=2", cookie, Ct);

        Assert.Equal(["2", "1"], second.Members.Select(m => m.UserId));
        Assert.Equal(2, second.Page);
        Assert.Equal(4, second.Total);
    }

    private static string At(string? cursor, string sort = "joined") =>
        $"/api/discord/members?pageSize=2&cursor={Uri.EscapeDataString(cursor!)}"
        + (sort == "joined" ? "" : $"&sort={sort}");
}
