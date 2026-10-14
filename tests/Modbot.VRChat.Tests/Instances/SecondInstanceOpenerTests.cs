using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Modbot.VRChat.Instances;
using Modbot.VRChat.Tests.Sync;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Instances;

/// <summary>
/// The Open second instance button: staff only, one instance per alert, the same link for a second
/// press, a 429 given back for a later press and never sent again here, a 500 held (written, not run --
/// CI runs them when staging is merged).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SecondInstanceOpenerTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string World = "wrld_full";
    private const string FullLocation = World + ":1~group(" + GroupId + ")~groupAccessType(plus)~region(eu)";

    private static readonly StaffMember Staff = new(Guid.CreateVersion7(), "staff", ModbotPermissions.ManageCalendar);
    private static readonly StaffMember Other = new(Guid.CreateVersion7(), "other", ModbotPermissions.ManageCalendar);

    /// <summary>A full instance and the alert about it, saved.</summary>
    private async Task<Guid> AddAlertAsync()
    {
        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = FullLocation,
            WorldId = World,
            VRChatInstanceId = "1",
            GroupId = GroupId,
            Type = "group",
            GroupAccessType = "plus",
            Region = "eu",
            OpenedAt = Clock.UtcNow.AddHours(-1),
            LastSeenAt = Clock.UtcNow,
            SeenInGroupList = true,
            HeadCount = 40,
        };

        var alert = new Alert
        {
            Watcher = AlertWatchers.InstanceSecond,
            At = Clock.UtcNow,
            WindowStart = Clock.UtcNow,
            WindowEnd = Clock.UtcNow,
            Now = 40,
            InstanceId = instance.Id,
        };

        await using var context = Database.NewContext();
        context.VRChatInstances.Add(instance);
        context.Alerts.Add(alert);
        await context.SaveChangesAsync(Ct);
        return alert.Id;
    }

    private async Task<SecondInstanceAnswer> PressAsync(Guid alertId, StaffMember? by = null)
    {
        await using var context = Database.NewContext();
        var opener = new SecondInstanceOpener(
            Gate,
            context,
            new PlaceStore(context, Clock),
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            Clock);

        return await opener.OpenAsync(alertId, by ?? Staff, Ct);
    }

    private async Task<Alert> AlertAsync(Guid id)
    {
        await using var context = Database.NewContext();
        return await context.Alerts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
    }

    [Fact]
    public async Task SomebodyWithoutManageCalendar_IsRefused_AndNothingIsSent()
    {
        var id = await AddAlertAsync();

        var answer = await PressAsync(id, new StaffMember(Guid.CreateVersion7(), "viewer", ModbotPermissions.None));

        Assert.False(answer.Opened);
        Assert.Equal(SecondInstanceOpener.NoPermission, answer.Message);
        Assert.Empty(VRChat.Instances.Created);
        Assert.Null((await AlertAsync(id)).SecondTriedAt);
    }

    [Fact]
    public async Task AnAdministrator_MayPress()
    {
        var id = await AddAlertAsync();

        var answer = await PressAsync(id, new StaffMember(Guid.CreateVersion7(), "admin", ModbotPermissions.Administrator));

        Assert.True(answer.Opened);
        Assert.Single(VRChat.Instances.Created);
    }

    [Fact]
    public async Task APress_OpensOneInstance_OfTheSameWorldAccessAndRegion_AndKeepsWhereItIs()
    {
        var id = await AddAlertAsync();

        var answer = await PressAsync(id);

        Assert.True(answer.Opened);
        Assert.False(answer.Already);
        Assert.NotNull(answer.JoinLink);

        var request = Assert.Single(VRChat.Instances.Created);
        Assert.Equal(World, request.WorldId);
        Assert.Equal(InstanceType.Group, request.Type);
        Assert.Equal(GroupId, request.OwnerId);
        Assert.Equal(GroupAccessType.Plus, request.GroupAccessType);
        Assert.Equal(InstanceRegion.Eu, request.Region);

        var alert = await AlertAsync(id);
        Assert.NotNull(alert.SecondLocation);
        Assert.Equal(Staff.UserId, alert.SecondOpenedByUserId);
        Assert.Single(await FactsOfTypeAsync(FactType.SecondInstanceOpened));
    }

    [Fact]
    public async Task ASecondPress_GetsTheSameLink_AndOpensNothing()
    {
        var id = await AddAlertAsync();

        var first = await PressAsync(id);
        var second = await PressAsync(id, Other);

        Assert.True(second.Opened);
        Assert.True(second.Already);
        Assert.Equal(first.JoinLink, second.JoinLink);
        Assert.Single(VRChat.Instances.Created);
        Assert.Single(await FactsOfTypeAsync(FactType.SecondInstanceOpened));
    }

    [Fact]
    public async Task A429_GivesTheClaimBack_AndIsNotSentAgainHere()
    {
        var id = await AddAlertAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.TooManyRequests;

        var first = await PressAsync(id);

        Assert.False(first.Opened);
        Assert.Equal(SecondInstanceOpener.TryAgainSoon, first.Message);
        Assert.Null((await AlertAsync(id)).SecondTriedAt);

        // A later press finds the class cold-stopped: the gate sends nothing, and this code does not
        // go round it. The press still gives the claim back.
        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        var second = await PressAsync(id, Other);

        Assert.False(second.Opened);
        Assert.Single(VRChat.Instances.Created);
        Assert.Null((await AlertAsync(id)).SecondLocation);
        Assert.Null((await AlertAsync(id)).SecondTriedAt);
    }

    [Fact]
    public async Task A500_HoldsTheClaim_SoAnotherPressCannotMakeAThird()
    {
        var id = await AddAlertAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.InternalServerError;

        var first = await PressAsync(id);

        Assert.False(first.Opened);
        Assert.Equal(SecondInstanceOpener.NoAnswer, first.Message);
        Assert.NotNull((await AlertAsync(id)).SecondTriedAt);

        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        Clock.Advance(TimeSpan.FromSeconds(30));
        var second = await PressAsync(id, Other);

        Assert.False(second.Opened);
        Assert.Equal(SecondInstanceOpener.BeingOpened, second.Message);
        Assert.Single(VRChat.Instances.Created);

        // Once the wait is over, staff may look and press again.
        Clock.Advance(SecondInstanceOpener.TryAgainAfter);
        var third = await PressAsync(id, Other);

        Assert.True(third.Opened);
        Assert.Equal(2, VRChat.Instances.Created.Count);
    }

    [Fact]
    public async Task ARealRefusal_GivesTheClaimBack()
    {
        var id = await AddAlertAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.BadRequest;

        var answer = await PressAsync(id);

        Assert.False(answer.Opened);
        Assert.Null((await AlertAsync(id)).SecondTriedAt);
    }

    [Fact]
    public async Task AnAlertThatIsNotAboutASecondInstance_HasNothingToCopy()
    {
        var alert = new Alert
        {
            Watcher = AlertWatchers.InstanceFilling,
            At = Clock.UtcNow,
            WindowStart = Clock.UtcNow,
            WindowEnd = Clock.UtcNow,
        };

        await using (var context = Database.NewContext())
        {
            context.Alerts.Add(alert);
            await context.SaveChangesAsync(Ct);
        }

        var answer = await PressAsync(alert.Id);

        Assert.False(answer.Opened);
        Assert.Equal(SecondInstanceOpener.NoInstance, answer.Message);
        Assert.Empty(VRChat.Instances.Created);
    }
}
