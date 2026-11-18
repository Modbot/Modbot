using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Events;
using Modbot.Api.Features.ProblemAvatars;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// Mark avatar on the companion's person card: the avatar a person was last seen wearing goes on the
/// problem avatar list as the device's owner, who must manage settings. Written, not run (Docker needed).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CompanionProblemAvatarTests(PostgresFixture db)
{
    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private const string Tiger = "White Tiger (v2)";

    private const string Mark = "/api/v1/companion/problem-avatars";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CompanionApiTestHost> StartAsync()
    {
        await CompanionApiTestHost.ResetAsync(db, Ct);
        var host = await CompanionApiTestHost.StartAsync(db);
        await host.ConfigureGroupAsync(db, Group, Ct);

        return host;
    }

    private static async Task ReportAsync(
        CompanionApiTestHost host, string token, string type, string subject, string? avatar, DateTimeOffset? at = null)
    {
        var data = new Dictionary<string, string> { ["displayName"] = "Rin" };
        if (avatar is not null)
            data["avatarName"] = avatar;

        var report = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        report.Content = JsonContent.Create(new EventBatchDto(
            Guid.NewGuid().ToString("n"), "2026.9.0", 0, "good",
            [
                new CompanionEventDto(
                    Guid.NewGuid().ToString("n"), type, at ?? Noon, null, subject,
                    "wrld_4b34", Instance, Group, data),
            ]));

        (await host.Client.SendAsync(report, Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> MarkAsync(CompanionApiTestHost host, string? token, string? person)
    {
        using var request = host.WithToken(HttpMethod.Post, Mark, token);
        request.Content = JsonContent.Create(new { vrchatUserId = person });

        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> SummaryAsync(CompanionApiTestHost host, string token, string person)
    {
        using var request = host.WithToken(HttpMethod.Get, $"/api/v1/companion/user/{person}", token);
        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<(string Token, string Username)> ManagerAsync(CompanionApiTestHost host)
    {
        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ModbotPermissions.ManageSettings, Ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, Ct);

        return (token, owner.Username);
    }

    [Fact]
    public async Task MarkingAPersonsAvatar_AddsItsExactName_AsTheOwner_AndTheCardThenReadsListed()
    {
        await using var host = await StartAsync();
        var (token, username) = await ManagerAsync(host);

        await ReportAsync(host, token, "AvatarChanged", "usr_wearer", "Plain Fox", Noon.AddMinutes(-5));
        await ReportAsync(host, token, "AvatarChanged", "usr_wearer", Tiger, Noon);

        var before = await SummaryAsync(host, token, "usr_wearer");
        Assert.Equal(Tiger, before.GetProperty("avatarName").GetString());
        Assert.False(before.GetProperty("avatarListed").GetBoolean());

        var response = await MarkAsync(host, token, "usr_wearer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Tiger, (await response.Content.ReadFromJsonAsync<ProblemAvatarView>(Ct))!.Name);

        await using (var context = db.NewContext())
        {
            var entry = await context.ProblemAvatars.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(Tiger, entry.Name);
            Assert.Equal(ProblemAvatar.KeyOf(Tiger), entry.NameKey);
            Assert.Equal(username, entry.AddedByUsername);
        }

        Assert.True((await SummaryAsync(host, token, "usr_wearer")).GetProperty("avatarListed").GetBoolean());

        // A second press is the same entry, not an error.
        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(host, token, "usr_wearer")).StatusCode);
        await using var again = db.NewContext();
        Assert.Equal(1, await again.ProblemAvatars.CountAsync(Ct));
    }

    [Fact]
    public async Task AnOwnerWhoMayNotManageSettings_IsRefused_AndTheCardNeverShowsThemTheAvatar()
    {
        await using var host = await StartAsync();
        var (manager, _) = await ManagerAsync(host);
        var plain = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ModbotPermissions.ViewMembers, Ct);
        var (plainToken, _) = await host.PairDeviceToAsync(plain.Id, Ct);

        await ReportAsync(host, manager, "AvatarChanged", "usr_wearer", Tiger);

        Assert.Equal(HttpStatusCode.Forbidden, (await MarkAsync(host, plainToken, "usr_wearer")).StatusCode);

        var summary = await SummaryAsync(host, plainToken, "usr_wearer");
        Assert.True(summary.GetProperty("avatarName").ValueKind is JsonValueKind.Null);
        Assert.True(summary.GetProperty("avatarListed").ValueKind is JsonValueKind.Null);

        await using var context = db.NewContext();
        Assert.Equal(0, await context.ProblemAvatars.CountAsync(Ct));
    }

    [Fact]
    public async Task AdministratorMayMark_AsAnyoneWhoManagesSettingsMay()
    {
        await using var host = await StartAsync();
        var admin = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ModbotPermissions.Administrator, Ct);
        var (token, _) = await host.PairDeviceToAsync(admin.Id, Ct);

        await ReportAsync(host, token, "AvatarChanged", "usr_wearer", Tiger);

        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(host, token, "usr_wearer")).StatusCode);
    }

    [Fact]
    public async Task WithoutAToken_ItIsRefused()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await MarkAsync(host, null, "usr_wearer")).StatusCode);
    }

    [Fact]
    public async Task SomeoneNeverSeenInAnAvatar_IsNotFound_AndNoPersonIsABadRequest()
    {
        await using var host = await StartAsync();
        var (token, _) = await ManagerAsync(host);

        await ReportAsync(host, token, "InstanceJoined", "usr_bare", avatar: null);

        Assert.Equal(HttpStatusCode.NotFound, (await MarkAsync(host, token, "usr_bare")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MarkAsync(host, token, "usr_nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MarkAsync(host, token, " ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MarkAsync(host, token, null)).StatusCode);

        var summary = await SummaryAsync(host, token, "usr_bare");
        Assert.True(summary.GetProperty("avatarName").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task AfterItIsMarked_ALaterJoinWearingIt_AlertsTheModeratorsInThatInstance()
    {
        await using var host = await StartAsync();
        var (marker, _) = await ManagerAsync(host);
        var watcher = await host.PairDeviceAsync(Ct);

        // The watcher says it is standing in the instance the way an overlay does, by reading its roster.
        (await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v1/companion/context?instanceId={Instance}", watcher), Ct))
            .EnsureSuccessStatusCode();

        await ReportAsync(host, marker, "AvatarChanged", "usr_first", Tiger, Noon.AddMinutes(-10));
        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(host, marker, "usr_first")).StatusCode);

        // Marking adds to the list and raises nothing by itself.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await host.Client.SendAsync(host.WithToken(HttpMethod.Get, "/api/v1/companion/alerts?wait=1", watcher), Ct)).StatusCode);

        await ReportAsync(host, marker, "InstanceJoined", "usr_second", Tiger, Noon);

        var response = await host.Client.SendAsync(host.WithToken(HttpMethod.Get, "/api/v1/companion/alerts?wait=1", watcher), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alert = (await response.Content.ReadFromJsonAsync<FlaggedJoinAlertDto>(Ct))!;
        Assert.Equal("usr_second", alert.SubjectId);
        Assert.Equal($"Wearing a problem avatar: {Tiger}", alert.Reason);
    }
}
