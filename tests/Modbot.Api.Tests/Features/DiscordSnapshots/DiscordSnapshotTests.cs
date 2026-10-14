using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordSnapshots;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordSnapshots;

/// <summary>
/// Server snapshots: taken by hand or by the schedule, compared, downloaded, deleted, and one role's or
/// channel's permissions put back only while restoring is on, only after the change was shown, and only
/// while the server still gives that change.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordSnapshotTests
{
    private const long ViewChannel = 1L << 10;
    private const long SendMessages = 1L << 11;
    private const long ManageRoles = 1L << 28;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public DiscordSnapshotTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSnapshotActions : IDiscordSnapshotActions
    {
        public DiscordStructureOutcome Server { get; set; } = DiscordStructureOutcome.Offline;

        public List<(string RoleId, long Permissions)> RoleEdits { get; } = [];

        public List<(string ChannelId, IReadOnlyList<SnapshotOverwriteSet> Set, IReadOnlyList<string> Remove)> ChannelEdits { get; } = [];

        public Task<DiscordStructureOutcome> ReadAsync(CancellationToken ct = default) => Task.FromResult(Server);

        public Task<DiscordEditResult> SetRolePermissionsAsync(string roleId, long permissions, string reason, CancellationToken ct = default)
        {
            RoleEdits.Add((roleId, permissions));
            return Task.FromResult(DiscordEditResult.Ok(1));
        }

        public Task<DiscordEditResult> SetChannelOverwritesAsync(
            string channelId,
            IReadOnlyList<SnapshotOverwriteSet> set,
            IReadOnlyList<string> removeRoleIds,
            string reason,
            CancellationToken ct = default)
        {
            ChannelEdits.Add((channelId, set, removeRoleIds));
            return Task.FromResult(DiscordEditResult.Ok(set.Count + removeRoleIds.Count));
        }
    }

    private static DiscordStructureOutcome Structure(long modPermissions, long? staffAllow = null)
    {
        var roles = new[]
        {
            new SnapshotRole("100", "@everyone", 0, 0, ViewChannel, false, false, false, true),
            new SnapshotRole("200", "Mod", 0, 5, modPermissions, false, false, false, false),
        };

        var channels = new[]
        {
            new SnapshotChannel(
                "300",
                "staff",
                "text",
                null,
                0,
                false,
                staffAllow is { } allow ? [new SnapshotOverwrite("200", allow, 0)] : []),
        };

        var content = new ServerSnapshotContent(ServerSnapshotContent.CurrentVersion, "1", "Test", roles, channels);

        return new DiscordStructureOutcome(new ServerStructure(content, true, ManageRoles, 10), null);
    }

    private static async Task<(ReadSurfaceTestHost Host, FakeSnapshotActions Discord)> StartAsync(
        PostgresFixture db, bool restoreOn = false, string schedule = DiscordSnapshotSchedules.Off)
    {
        var discord = new FakeSnapshotActions { Server = Structure(ViewChannel) };
        var host = await ReadSurfaceTestHost.StartAsync(db, configure: s => s.AddSingleton<IDiscordSnapshotActions>(discord));
        await host.ResetAsync(Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        await context.DiscordSnapshots.ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordSnapshotRestoreOn = restoreOn;
        settings.DiscordSnapshotSchedule = schedule;
        await context.SaveChangesAsync(Ct);

        return (host, discord);
    }

    private static async Task<DiscordSnapshotRow> TakeAsync(ReadSurfaceTestHost host, string cookie)
    {
        var response = await host.PostJsonAsync("/api/discord/snapshots", null, cookie, Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<DiscordSnapshotRow>(Json, Ct))!;
    }

    private static Task<HttpResponseMessage> DeleteAsync(ReadSurfaceTestHost host, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Cookie", cookie);
        return host.Client.SendAsync(request, Ct);
    }

    private static object Body(string kind, string targetId, string? fingerprint = null)
        => new { kind, targetId, fingerprint };

    [Fact]
    public async Task TakingOneByHand_SavesTheRolesAndChannels_AndListsIt()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        var taken = await TakeAsync(host, staff);

        Assert.False(taken.Scheduled);
        Assert.Equal(2, taken.RoleCount);
        Assert.Equal(1, taken.ChannelCount);

        var list = await host.GetJsonAsync<DiscordSnapshotsResponse>("/api/discord/snapshots", staff, Ct);

        Assert.Equal(taken.Id, Assert.Single(list.Snapshots).Id);
        Assert.Equal(DiscordSnapshotSchedules.Off, list.Schedule);
        Assert.False(list.RestoreOn);
    }

    [Fact]
    public async Task SomebodyWhoOnlySees_CannotTakeDeleteOrRestore()
    {
        var (host, _) = await StartAsync(_db, restoreOn: true);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var taken = await TakeAsync(host, staff);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/discord/snapshots", viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/discord/snapshots", null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(host, $"/api/discord/snapshots/{taken.Id}", viewer)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore-preview", Body("role", "200"), viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task SomebodyWithoutSeeAnalytics_CannotOpenTheList()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/snapshots", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task WithTheBotOffline_TakingOneAnswersThatInsteadOfSavingNothing()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        discord.Server = DiscordStructureOutcome.Offline;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        var response = await host.PostJsonAsync("/api/discord/snapshots", null, staff, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty((await host.GetJsonAsync<DiscordSnapshotsResponse>("/api/discord/snapshots", staff, Ct)).Snapshots);
    }

    [Fact]
    public async Task Download_IsAJsonFileWithTheRolesInWords()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);

        var response = await host.GetAsync($"/api/discord/snapshots/{taken.Id}/download", staff, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Content.Headers.ContentDisposition?.FileName);

        var detail = await response.Content.ReadFromJsonAsync<DiscordSnapshotDetail>(Json, Ct);
        var mod = Assert.Single(detail!.Roles, r => r.Name == "Mod");
        Assert.Equal(["View Channel"], mod.PermissionNames);
    }

    [Fact]
    public async Task Delete_RemovesTheSnapshot_AndAnUnknownOneIsNotFound()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(host, $"/api/discord/snapshots/{taken.Id}", staff)).StatusCode);
        Assert.Empty((await host.GetJsonAsync<DiscordSnapshotsResponse>("/api/discord/snapshots", staff, Ct)).Snapshots);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(host, $"/api/discord/snapshots/{taken.Id}", staff)).StatusCode);
    }

    [Fact]
    public async Task Compare_ToNow_ListsWhatChangedSinceTheSnapshot()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);

        discord.Server = Structure(ViewChannel | SendMessages);

        var result = await host.GetJsonAsync<DiscordSnapshotCompareResponse>(
            $"/api/discord/snapshots/compare?from={taken.Id}&to=now", staff, Ct);

        Assert.True(result.ToNow);
        var change = Assert.Single(result.Changes);
        Assert.Equal("Mod", change.Name);
        Assert.Equal("changed", change.What);
        Assert.Equal(["Permissions gained: Send Messages"], change.Details);
    }

    [Fact]
    public async Task Compare_TwoSnapshots_ListsTheDifferenceBetweenThem()
    {
        var (host, discord) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var first = await TakeAsync(host, staff);

        discord.Server = Structure(ViewChannel, staffAllow: ViewChannel);
        host.Clock.Advance(TimeSpan.FromHours(1));
        var second = await TakeAsync(host, staff);

        var result = await host.GetJsonAsync<DiscordSnapshotCompareResponse>(
            $"/api/discord/snapshots/compare?from={first.Id}&to={second.Id}", staff, Ct);

        Assert.False(result.ToNow);
        var change = Assert.Single(result.Changes);
        Assert.Equal("staff", change.Name);
    }

    [Fact]
    public async Task Restore_WhileOff_IsRefusedAndChangesNothing()
    {
        var (host, discord) = await StartAsync(_db, restoreOn: false);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);
        discord.Server = Structure(0);

        var preview = await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore-preview", Body("role", "200"), staff, Ct);
        var restore = await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore", Body("role", "200", "x"), staff, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        Assert.Empty(discord.RoleEdits);
    }

    [Fact]
    public async Task RestoreARole_ShowsTheChangeFirst_ThenPutsItBack_AndIsRecorded()
    {
        var (host, discord) = await StartAsync(_db, restoreOn: true);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);
        discord.Server = Structure(SendMessages);

        var previewResponse = await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore-preview", Body("role", "200"), staff, Ct);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<DiscordSnapshotRestorePreview>(Json, Ct))!;

        Assert.Equal(["Mod gets: View Channel", "Mod loses: Send Messages"], preview.Lines);
        Assert.Empty(discord.RoleEdits);

        var restore = await host.PostJsonAsync(
            $"/api/discord/snapshots/{taken.Id}/restore", Body("role", "200", preview.Fingerprint), staff, Ct);
        var done = (await restore.Content.ReadFromJsonAsync<DiscordSnapshotRestoreResponse>(Json, Ct))!;

        Assert.True(done.Done);
        Assert.Equal(("200", ViewChannel), Assert.Single(discord.RoleEdits));

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var facts = await context.Events.AsNoTracking().Where(e => e.Type == FactType.DiscordSnapshotRestored).ToListAsync(Ct);

        var fact = Assert.Single(facts);
        Assert.Equal("role", ApiTestHost.DataOf(fact).GetProperty("kind").GetString());
        Assert.Equal("Mod", ApiTestHost.DataOf(fact).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Restore_WhenTheServerChangedSinceThePreview_IsRefusedWithConflict()
    {
        var (host, discord) = await StartAsync(_db, restoreOn: true);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);
        discord.Server = Structure(SendMessages);

        var preview = (await (await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore-preview", Body("role", "200"), staff, Ct))
            .Content.ReadFromJsonAsync<DiscordSnapshotRestorePreview>(Json, Ct))!;

        // Somebody changed the role again before the press.
        discord.Server = Structure(SendMessages | ManageRoles);

        var restore = await host.PostJsonAsync(
            $"/api/discord/snapshots/{taken.Id}/restore", Body("role", "200", preview.Fingerprint), staff, Ct);

        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Empty(discord.RoleEdits);
    }

    [Fact]
    public async Task Restore_WithoutAFingerprint_IsRefused()
    {
        var (host, discord) = await StartAsync(_db, restoreOn: true);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);
        discord.Server = Structure(SendMessages);

        var restore = await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore", Body("role", "200"), staff, Ct);

        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Empty(discord.RoleEdits);
    }

    [Fact]
    public async Task RestoreAChannel_SetsTheRolesSettingsFromTheSnapshot()
    {
        var (host, discord) = await StartAsync(_db, restoreOn: true);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        discord.Server = Structure(ViewChannel, staffAllow: ViewChannel);
        var taken = await TakeAsync(host, staff);
        discord.Server = Structure(ViewChannel, staffAllow: 0);

        var preview = (await (await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore-preview", Body("channel", "300"), staff, Ct))
            .Content.ReadFromJsonAsync<DiscordSnapshotRestorePreview>(Json, Ct))!;

        var restore = await host.PostJsonAsync(
            $"/api/discord/snapshots/{taken.Id}/restore", Body("channel", "300", preview.Fingerprint), staff, Ct);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var edit = Assert.Single(discord.ChannelEdits);
        Assert.Equal("300", edit.ChannelId);
        var set = Assert.Single(edit.Set);
        Assert.Equal(("200", ViewChannel), (set.RoleId, set.Allow));
    }

    [Fact]
    public async Task Restore_WhenNothingWouldChange_IsRefused()
    {
        var (host, discord) = await StartAsync(_db, restoreOn: true);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var taken = await TakeAsync(host, staff);

        var preview = (await (await host.PostJsonAsync($"/api/discord/snapshots/{taken.Id}/restore-preview", Body("role", "200"), staff, Ct))
            .Content.ReadFromJsonAsync<DiscordSnapshotRestorePreview>(Json, Ct))!;

        Assert.True(preview.NothingToChange);

        var restore = await host.PostJsonAsync(
            $"/api/discord/snapshots/{taken.Id}/restore", Body("role", "200", preview.Fingerprint), staff, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        Assert.Empty(discord.RoleEdits);
    }

    [Fact]
    public async Task Settings_AreSavedAndAnUnknownScheduleIsRefused()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        const string Url = "/api/settings/discord-snapshots";

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Url, new { schedule = "weekly", restoreOn = false }, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync(Url, new { schedule = "daily", restoreOn = false }, staff, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PutJsonAsync(Url, new { schedule = "monthly", restoreOn = true }, staff, Ct)).StatusCode);

        var saved = await host.GetJsonAsync<DiscordSnapshotSettingsResponse>(Url, staff, Ct);

        Assert.Equal(DiscordSnapshotSchedules.Monthly, saved.Schedule);
        Assert.True(saved.RestoreOn);
    }

    private static DiscordSnapshotPass PassFor(ReadSurfaceTestHost host, FakeSnapshotActions discord, ModbotContext context)
        => new(context, host.Clock, discord);

    [Fact]
    public async Task Pass_WhileTheScheduleIsOff_TakesNothing()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Off);
        await using var _ = host;
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.False(await PassFor(host, discord, context).RunOnceAsync(Ct));
        Assert.Equal(0, await context.DiscordSnapshots.CountAsync(Ct));
    }

    [Fact]
    public async Task Pass_TakesOneWhenNoneExists_ThenWaitsAFullPeriod()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Weekly);
        await using var _ = host;
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var pass = PassFor(host, discord, context);

        Assert.True(await pass.RunOnceAsync(Ct));
        Assert.True((await context.DiscordSnapshots.SingleAsync(Ct)).Scheduled);

        host.Clock.Advance(TimeSpan.FromDays(6));
        Assert.False(await pass.RunOnceAsync(Ct));

        host.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await pass.RunOnceAsync(Ct));
        Assert.Equal(2, await context.DiscordSnapshots.CountAsync(Ct));
    }

    [Fact]
    public async Task Pass_WithTheBotOffline_TakesNothingAndTriesAgainNextTime()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Monthly);
        await using var _ = host;
        discord.Server = DiscordStructureOutcome.Offline;
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var pass = PassFor(host, discord, context);

        Assert.False(await pass.RunOnceAsync(Ct));

        discord.Server = Structure(ViewChannel);
        Assert.True(await pass.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task Pass_KeepsOnlyTheNewestScheduledOnes_AndNeverDeletesOneTakenByHand()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Weekly);
        await using var _ = host;
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
        var byHand = await TakeAsync(host, staff);
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var pass = PassFor(host, discord, context);

        for (var i = 0; i < DiscordSnapshotLimits.MostScheduledKept + 3; i++)
        {
            host.Clock.Advance(TimeSpan.FromDays(8));
            Assert.True(await pass.RunOnceAsync(Ct));
        }

        Assert.Equal(DiscordSnapshotLimits.MostScheduledKept, await context.DiscordSnapshots.CountAsync(s => s.Scheduled, Ct));
        Assert.True(await context.DiscordSnapshots.AnyAsync(s => s.Id == byHand.Id, Ct));
    }

    [Fact]
    public async Task Pass_WhileAnotherModbotHoldsTheClaim_TakesNothing_ThenTakesOverFromOneThatStopped()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Weekly);
        await using var _ = host;
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var pass = PassFor(host, discord, context);

        await context.Settings.ExecuteUpdateAsync(
            u => u.SetProperty(s => s.DiscordSnapshotClaimedAt, (DateTimeOffset?)host.Clock.UtcNow), Ct);

        Assert.False(await pass.RunOnceAsync(Ct));
        Assert.Equal(0, await context.DiscordSnapshots.CountAsync(Ct));

        host.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(await pass.RunOnceAsync(Ct));
        Assert.Equal(1, await context.DiscordSnapshots.CountAsync(Ct));
        Assert.Null((await context.Settings.AsNoTracking().SingleAsync(Ct)).DiscordSnapshotClaimedAt);
    }

    private static async Task SeedAsync(ModbotContext context, ReadSurfaceTestHost host, int byHand, int scheduled)
    {
        for (var i = 0; i < byHand + scheduled; i++)
        {
            context.DiscordSnapshots.Add(new DiscordSnapshot
            {
                TakenAt = host.Clock.UtcNow.AddDays(-(i + 20)),
                Scheduled = i >= byHand,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Pass_WithOneHundredKept_DropsTheOldestScheduledOnesToStayAtOneHundred()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Weekly);
        await using var _ = host;
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        await SeedAsync(context, host, byHand: 90, scheduled: 10);

        Assert.True(await PassFor(host, discord, context).RunOnceAsync(Ct));

        Assert.Equal(DiscordSnapshotLimits.MostKept, await context.DiscordSnapshots.CountAsync(Ct));
        Assert.Equal(90, await context.DiscordSnapshots.CountAsync(s => !s.Scheduled, Ct));
    }

    [Fact]
    public async Task Pass_WithOneHundredTakenByHand_TakesNothing()
    {
        var (host, discord) = await StartAsync(_db, schedule: DiscordSnapshotSchedules.Weekly);
        await using var _ = host;
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        await SeedAsync(context, host, byHand: DiscordSnapshotLimits.MostKept, scheduled: 0);

        Assert.False(await PassFor(host, discord, context).RunOnceAsync(Ct));
        Assert.Equal(DiscordSnapshotLimits.MostKept, await context.DiscordSnapshots.CountAsync(Ct));
    }
}
