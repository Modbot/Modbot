using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Settings → Discord → Member milestones: the first event, the fifth event and sixty days to start;
/// the numbers are held to a range; a post needs a channel; turning a post on stamps the moment so
/// nobody who got there earlier is acted on; a role Modbot cannot find is refused; and only somebody
/// who can change settings may save it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordMilestoneSettingsTests
{
    private const string Channel = "900";
    private const string Url = "/api/settings/discord-milestones";

    private readonly PostgresFixture _db;

    public DiscordMilestoneSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SettingsRow> StoredAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModbotContext>().GetSettingsAsync(Ct);
    }

    [Fact]
    public async Task NothingChosen_StartsAtTheFirstAndFifthEvent_AndSixtyDays_WithNothingPosted()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var view = await host.GetJsonAsync<DiscordMilestoneSettingsResponse>(Url, cookie, Ct);

        Assert.Equal(1, view.FirstAt);
        Assert.Equal(5, view.RegularAt);
        Assert.Equal(60, view.BackDays);
        Assert.False(view.PostFirst);
        Assert.False(view.PostRegular);
        Assert.False(view.PostBack);
        Assert.Null(view.ChannelId);
        Assert.Null(view.RegularRoleId);
        Assert.Null(view.Problem);
    }

    [Fact]
    public async Task APostWithoutAChannel_IsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.PutJsonAsync(Url, new { postFirst = true }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await StoredAsync(host)).MilestonePostFirst);
    }

    [Theory]
    [InlineData(0, 5, 60)]
    [InlineData(1, 1001, 60)]
    [InlineData(1, 5, 0)]
    [InlineData(1, 5, 3651)]
    public async Task NumbersOutsideTheirRange_AreRefused_AndNothingIsSaved(int firstAt, int regularAt, int backDays)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.PutJsonAsync(Url, new { firstAt, regularAt, backDays }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var stored = await StoredAsync(host);
        Assert.Equal(1, stored.MilestoneFirstAt);
        Assert.Equal(5, stored.MilestoneRegularAt);
        Assert.Equal(60, stored.MilestoneBackDays);
    }

    [Fact]
    public async Task TurningAPostOn_SavesTheNumbers_AndStampsTheMomentItWasTurnedOn()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.PutJsonAsync(
            Url, new { firstAt = 2, regularAt = 10, backDays = 90, channelId = Channel, postRegular = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var stored = await StoredAsync(host);
        Assert.Equal(2, stored.MilestoneFirstAt);
        Assert.Equal(10, stored.MilestoneRegularAt);
        Assert.Equal(90, stored.MilestoneBackDays);
        Assert.Equal(Channel, stored.MilestoneChannelId);
        Assert.True(stored.MilestonePostRegular);
        Assert.False(stored.MilestonePostFirst);
        Assert.Equal(host.Clock.UtcNow, stored.MilestonesFrom);
    }

    [Fact]
    public async Task ChangingOnlyTheNumbers_WithNothingPosted_DoesNotStampAMoment()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.PutJsonAsync(Url, new { firstAt = 3, regularAt = 8, backDays = 30 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var stored = await StoredAsync(host);
        Assert.Equal(3, stored.MilestoneFirstAt);
        Assert.Null(stored.MilestonesFrom);
    }

    [Fact]
    public async Task ARoleModbotCannotFind_IsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.PutJsonAsync(Url, new { regularRoleId = "123456" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null((await StoredAsync(host)).MilestoneRegularRoleId);
    }

    [Fact]
    public async Task SomebodyWithoutTheSettingsPermission_IsTurnedAway()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Url, new { firstAt = 2 }, viewer, Ct)).StatusCode);
    }
}
