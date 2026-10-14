using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Features.DiscordInvites;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordInvites;

/// <summary>
/// Invite tracking on the Modbot page: invites with one name are one source, the vanity link and joins
/// nobody could trace are their own, naming is for somebody who can change settings, and a list of
/// names is checked before Discord is asked to make anything.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordInviteTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public DiscordInviteTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeInviteActions : IDiscordInviteActions
    {
        public InviteSiteOutcome ReadResult { get; set; } = InviteSiteOutcome.Ok;

        public InviteSiteOutcome CreateResult { get; set; } = InviteSiteOutcome.Ok;

        public int Reads { get; private set; }

        public int Starts { get; private set; }

        public List<(string ChannelId, IReadOnlyList<string> Names)> Created { get; } = [];

        public Task<InviteSiteOutcome> ReadAsync(CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(ReadResult);
        }

        public Task<InviteSiteOutcome> StartCountingAsync(CancellationToken ct = default)
        {
            Starts++;
            return Task.FromResult(ReadResult);
        }

        public Task<InviteCreateOutcome> CreateAsync(string channelId, IReadOnlyList<string> names, CancellationToken ct = default)
        {
            Created.Add((channelId, names));

            var made = CreateResult.Done
                ? names.Select((n, i) => new InviteMade(n, $"code{i}", null)).ToList()
                : [];

            return Task.FromResult(new InviteCreateOutcome(CreateResult, made));
        }
    }

    private static async Task<(ReadSurfaceTestHost Host, FakeInviteActions Discord)> StartAsync(PostgresFixture db, bool trackingOn = true)
    {
        var discord = new FakeInviteActions();
        var host = await ReadSurfaceTestHost.StartAsync(db, configure: s => s.AddSingleton<IDiscordInviteActions>(discord));
        await host.ResetAsync(Ct);

        if (trackingOn)
        {
            using var scope = host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await context.GetSettingsAsync(Ct)).DiscordInviteTrackingOn = true;
            await context.SaveChangesAsync(Ct);
        }

        return (host, discord);
    }

    /// <summary>Four invites (two of them named "Linktree"), the vanity link and a join through each, plus one nobody could trace.</summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var at = host.Clock.UtcNow;

        var first = Invite("aaa", "Linktree", at);
        var second = Invite("bbb", "linktree", at);
        var partner = Invite("ccc", "Partner: Example", at);
        var vanity = Invite("thy-kingdom", null, at);
        vanity.IsVanity = true;

        context.DiscordInviteLinks.AddRange(first, second, partner, vanity);

        context.DiscordInviteJoins.AddRange(
            Join("1", first.Id, at.AddHours(-1)),
            Join("2", second.Id, at.AddHours(-2)),
            Join("3", partner.Id, at.AddHours(-3)),
            Join("4", vanity.Id, at.AddHours(-4)),
            Join("5", null, at.AddHours(-5)));

        await context.SaveChangesAsync(Ct);
    }

    private static DiscordInviteLink Invite(string code, string? name, DateTimeOffset at) => new()
    {
        Code = code,
        Name = name,
        FirstSeenAt = at.AddDays(-5),
    };

    private static DiscordInviteJoin Join(string user, Guid? invite, DateTimeOffset at) => new()
    {
        UserId = user,
        InviteId = invite,
        JoinedAt = at,
    };

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("error").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task InvitesWithTheSameName_AreOneSource_AndTheVanityLinkAndUntracedJoinsAreTheirOwn()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        await SeedAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var report = await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", cookie, Ct);

        Assert.Equal(5, report.Joins);

        var linktree = Assert.Single(report.Sources, s => s.Name.Equals("Linktree", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, linktree.Invites);
        Assert.Equal(2, linktree.JoinsInRange);

        Assert.Equal(1, Assert.Single(report.Sources, s => s.Name == "Partner: Example").JoinsInRange);
        Assert.Equal(1, Assert.Single(report.Sources, s => s.Name == DiscordInviteEndpoints.Vanity).JoinsInRange);

        var unknown = Assert.Single(report.Sources, s => s.Unknown);
        Assert.Equal(DiscordInviteEndpoints.UnknownSource, unknown.Name);
        Assert.Equal(1, unknown.JoinsInRange);

        // One column of the chart per day of the range, for every source drawn.
        Assert.All(report.Series, s => Assert.Equal(30, s.Days.Count));
    }

    [Fact]
    public async Task SomebodyWhoMayOnlySeeAnalytics_IsNotToldAboutDiscordsPermissions_AndDiscordIsNotAsked()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        discord.ReadResult = InviteSiteOutcome.Failed("The bot needs Manage Server to read the server's invites.", missingPermission: true);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var seen = await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", viewer, Ct);

        Assert.Equal(0, discord.Reads);
        Assert.Null(seen.ReadError);
        Assert.False(seen.MissingPermission);

        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var told = await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", staff, Ct);

        Assert.Equal(1, discord.Reads);
        Assert.True(told.MissingPermission);
        Assert.Contains("Manage Server", told.ReadError);
    }

    [Fact]
    public async Task WhileTrackingIsOff_TheReportSaysSo_DiscordIsNotAsked_AndNothingIsMade()
    {
        var (host, discord) = await StartAsync(_db, trackingOn: false);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        var report = await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", staff, Ct);

        Assert.False(report.TrackingOn);
        Assert.Equal(0, discord.Reads);
        Assert.Null(report.ReadError);

        Assert.Equal("Invite tracking is off.", await ErrorAsync(
            await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = new[] { "Linktree" } }, staff, Ct)));
        Assert.Empty(discord.Created);
    }

    [Fact]
    public async Task TheTrackingSwitch_IsOffToStart_IsSavedAndAudited_AndIsForSomebodyWhoCanChangeSettings()
    {
        var (host, discord) = await StartAsync(_db, trackingOn: false);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        const string Url = "/api/settings/discord-invite-tracking";

        Assert.False((await host.GetJsonAsync<DiscordInviteTrackingSettingsResponse>(Url, staff, Ct)).On);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Url, new { on = true }, viewer, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.PutJsonAsync(Url, new { on = true }, staff, Ct)).StatusCode);
        Assert.True((await host.GetJsonAsync<DiscordInviteTrackingSettingsResponse>(Url, staff, Ct)).On);

        // Turning it on starts the counts from Discord's own; turning it off asks nothing.
        Assert.Equal(1, discord.Starts);

        // Saving the same again records nothing more.
        Assert.Equal(HttpStatusCode.OK, (await host.PutJsonAsync(Url, new { on = true }, staff, Ct)).StatusCode);
        Assert.Equal(1, discord.Starts);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var facts = await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.SettingsChanged)
            .ToListAsync(Ct);

        var change = Assert.Single(facts, f => ApiTestHost.DataOf(f).TryGetProperty("setting", out var name) && name.GetString() == "discordInviteTracking");
        var on = ApiTestHost.DataOf(change).GetProperty("changed").GetProperty("on");
        Assert.False(on.GetProperty("old").GetBoolean());
        Assert.True(on.GetProperty("new").GetBoolean());
    }

    [Fact]
    public async Task SomebodyWithoutSeeAnalytics_CannotOpenThePage()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/invites", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnInvite_IsNamedAndItsNameCleared_OnlyBySomebodyWhoCanChangeSettings()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        await SeedAsync(host);
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var invites = (await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", staff, Ct)).Invites;
        var partner = Assert.Single(invites, i => i.Code == "ccc");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync($"/api/discord/invites/{partner.Id}", new { name = "Nope" }, viewer, Ct)).StatusCode);

        var named = await host.PutJsonAsync($"/api/discord/invites/{partner.Id}", new { name = "  Linktree  " }, staff, Ct);
        Assert.Equal(HttpStatusCode.NoContent, named.StatusCode);

        var after = await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", staff, Ct);
        Assert.Equal("Linktree", Assert.Single(after.Invites, i => i.Code == "ccc").Name);
        Assert.Equal(3, Assert.Single(after.Sources, s => s.Name.Equals("Linktree", StringComparison.OrdinalIgnoreCase)).JoinsInRange);

        var cleared = await host.PutJsonAsync($"/api/discord/invites/{partner.Id}", new { name = (string?)null }, staff, Ct);
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        var last = await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", staff, Ct);
        Assert.Null(Assert.Single(last.Invites, i => i.Code == "ccc").Name);
        Assert.Contains(last.Sources, s => s.Name == "ccc");
    }

    [Fact]
    public async Task AName_ThatIsTooLongOrNotOneLine_AndAnInviteThatIsNotThere_AreRefused()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        await SeedAsync(host);
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var id = Assert.Single((await host.GetJsonAsync<DiscordInvitesResponse>("/api/discord/invites", staff, Ct)).Invites, i => i.Code == "ccc").Id;

        await ErrorAsync(await host.PutJsonAsync($"/api/discord/invites/{id}", new { name = new string('x', DiscordInviteLimits.MostNameLength + 1) }, staff, Ct));
        await ErrorAsync(await host.PutJsonAsync($"/api/discord/invites/{id}", new { name = "two\nlines" }, staff, Ct));

        Assert.Equal(HttpStatusCode.NotFound, (await host.PutJsonAsync($"/api/discord/invites/{Guid.NewGuid()}", new { name = "Ghost" }, staff, Ct)).StatusCode);
    }

    [Fact]
    public async Task AListOfNames_IsCheckedBeforeDiscordIsAsked()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        await ErrorAsync(await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "", names = new[] { "Linktree" } }, staff, Ct));
        await ErrorAsync(await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = new[] { "  ", "" } }, staff, Ct));

        var tooMany = Enumerable.Range(0, DiscordInviteLimits.MostCreatedAtOnce + 1).Select(i => $"Source {i}").ToArray();
        await ErrorAsync(await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = tooMany }, staff, Ct));

        var tooLong = new[] { new string('x', DiscordInviteLimits.MostNameLength + 1) };
        await ErrorAsync(await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = tooLong }, staff, Ct));

        Assert.Empty(discord.Created);
    }

    [Fact]
    public async Task AListOfNames_MakesOneInvitePerName_AndRepeatedNamesAreMadeOnce()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        var response = await host.PostJsonAsync(
            "/api/discord/invites/create",
            new { channelId = "900", names = new[] { "Linktree", " linktree ", "Partner: Example" } },
            staff,
            Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var result = (await response.Content.ReadFromJsonAsync<DiscordInviteCreateResponse>(Json, Ct))!;

        Assert.Equal(["Linktree", "Partner: Example"], result.Made.Select(m => m.Name));
        Assert.All(result.Made, m => Assert.NotNull(m.Code));
        Assert.Equal("900", Assert.Single(discord.Created).ChannelId);
    }

    [Fact]
    public async Task AMissingCreateInvitePermission_IsReportedToThePerson_AndNothingIsReportedMade()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        discord.CreateResult = InviteSiteOutcome.Failed("The bot needs Create Invite in that channel.", missingPermission: true);
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        var response = await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = new[] { "Linktree" } }, staff, Ct);
        var result = (await response.Content.ReadFromJsonAsync<DiscordInviteCreateResponse>(Json, Ct))!;

        Assert.True(result.MissingPermission);
        Assert.Contains("Create Invite", result.Error);
        Assert.Empty(result.Made);
    }

    [Fact]
    public async Task MakingInvites_IsRefusedForSomebodyWhoCannotChangeSettings_AndWhileTheBotIsOffline()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = new[] { "Linktree" } }, viewer, Ct)).StatusCode);

        discord.CreateResult = InviteSiteOutcome.Offline;
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await host.PostJsonAsync("/api/discord/invites/create", new { channelId = "900", names = new[] { "Linktree" } }, staff, Ct)).StatusCode);
    }
}
