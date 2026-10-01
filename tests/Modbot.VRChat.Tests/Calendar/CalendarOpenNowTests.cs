using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §4 as changed on 2026-10-01: only a real refusal from VRChat gives up on a time,
/// anything else is tried again on a later pass, and Open now opens the instance by hand through the
/// same path.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarOpenNowTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly Guid Pressed = Guid.CreateVersion7();

    private Task<CalendarEvent> AutoOpenAsync(TimeSpan? startsIn = null) =>
        AddEventAsync(startsIn ?? TimeSpan.FromMinutes(5), x =>
        {
            x.AutoOpen = true;
            x.OpenMinutesBefore = 10;
        });

    [Fact]
    public async Task VRChatFailingOnItsSideIsTriedAgain_AfterAMinute_NotAtOnce()
    {
        var e = await AutoOpenAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.InternalServerError;

        Assert.Equal(1, (await OpenAsync()).Failed);

        var opening = await OpeningAsync(e.Id);
        Assert.True(opening.TryAgain);
        Assert.NotNull(opening.Error);
        Assert.Empty(await FactsOfTypeAsync(FactType.PlannedEventInstanceFailed));

        // Not in the same breath.
        Clock.Advance(TimeSpan.FromSeconds(15));
        await OpenAsync();
        Assert.Single(VRChat.Instances.Created);

        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        Clock.Advance(CalendarOpener.TryAgainAfter);
        Assert.Equal(1, (await OpenAsync()).Opened);

        Assert.Equal(2, VRChat.Instances.Created.Count);
        Assert.Null((await OpeningAsync(e.Id)).Error);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceOpened));
    }

    [Fact]
    public async Task A429IsNotTriedAgainUntilTheLimiterAllows()
    {
        var e = await AutoOpenAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.TooManyRequests;

        await OpenAsync();
        Assert.True((await OpeningAsync(e.Id)).TryAgain);

        // The class is cold-stopped: a later pass sends nothing, and the time is still not given up.
        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        Clock.Advance(CalendarOpener.TryAgainAfter);
        await OpenAsync();

        Assert.Single(VRChat.Instances.Created);
        var waiting = await OpeningAsync(e.Id);
        Assert.True(waiting.TryAgain);
        Assert.Null(waiting.Location);
        Assert.Empty(await FactsOfTypeAsync(FactType.PlannedEventInstanceFailed));
    }

    [Fact]
    public async Task AReal400IsStillFinal()
    {
        var e = await AutoOpenAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.BadRequest;

        await OpenAsync();

        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(CalendarOpener.TryAgainAfter);
            await OpenAsync();
        }

        Assert.Single(VRChat.Instances.Created);
        Assert.False((await OpeningAsync(e.Id)).TryAgain);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceFailed));
    }

    [Fact]
    public async Task OpenNowOpensOnce_AndSaysWhoPressedIt()
    {
        // Not set to open on its own, an hour away.
        var e = await AddEventAsync(TimeSpan.FromHours(1));

        var result = await OpenNowAsync(e.Id, Pressed);
        Assert.Equal(CalendarOpenNowOutcome.Tried, result.Outcome);
        Assert.Equal(CalendarOpenOutcome.Opened, result.Opened);

        var opening = await OpeningAsync(e.Id);
        Assert.Equal(Pressed, opening.OpenedByUserId);
        Assert.NotNull(opening.Location);

        var fact = Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceOpened));
        Assert.Equal(Pressed.ToString(), fact.ActorId);

        // Pressed again while it is open: nothing more is opened.
        Assert.Equal(CalendarOpenNowOutcome.AlreadyOpen, (await OpenNowAsync(e.Id, Pressed)).Outcome);
        Assert.Single(VRChat.Instances.Created);
    }

    [Fact]
    public async Task OpenNowIsRefusedTooEarly_AndWithoutAWorld()
    {
        var early = await AddEventAsync(TimeSpan.FromHours(3));
        Assert.Equal(CalendarOpenNowOutcome.TooEarly, (await OpenNowAsync(early.Id, Pressed)).Outcome);

        var nowhere = await AddEventAsync(TimeSpan.FromMinutes(10), x => x.WorldId = null);
        Assert.Equal(CalendarOpenNowOutcome.NoWorld, (await OpenNowAsync(nowhere.Id, Pressed)).Outcome);

        Assert.Empty(VRChat.Instances.Created);
    }

    [Fact]
    public async Task OpenNowAfterAFailure_TriesAgain()
    {
        var e = await AutoOpenAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.BadRequest;
        await OpenAsync();

        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        var result = await OpenNowAsync(e.Id, Pressed);

        Assert.Equal(CalendarOpenOutcome.Opened, result.Opened);
        Assert.Equal(2, VRChat.Instances.Created.Count);
        Assert.Null((await OpeningAsync(e.Id)).Error);
    }

    [Fact]
    public async Task OpenNowAfterTheInstanceClosed_PutsTheUninvitedBackOnTheQueue()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b");
        VRChat.Invites.FriendsWith("usr_a", "usr_b");

        var e = await AddEventAsync(TimeSpan.FromMinutes(5), x =>
        {
            x.AutoOpen = true;
            x.InviteListId = list.Id;
        });

        await OpenAsync();
        await InviteAsync();

        // The instance closes before usr_b's turn.
        var first = await OpeningAsync(e.Id);
        await using (var context = Database.NewContext())
        {
            var instance = await context.VRChatInstances.SingleAsync(i => i.Id == first.InstanceId, Ct);
            instance.ClosedAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        Clock.Advance(CalendarInviter.NoFasterThan);
        await InviteAsync();
        Assert.Equal(CalendarInviteStates.Stopped, (await InviteRowsAsync(e.Id))[1].State);

        Assert.Equal(CalendarOpenOutcome.Opened, (await OpenNowAsync(e.Id, Pressed)).Opened);
        Assert.Equal(CalendarInviteStates.Waiting, (await InviteRowsAsync(e.Id))[1].State);

        Clock.Advance(CalendarInviter.NoFasterThan);
        await InviteAsync();

        // usr_a once, from the first instance; usr_b to the new one.
        Assert.Equal(["usr_a", "usr_b"], VRChat.Invites.Sent.Select(s => s.UserId));
        Assert.Equal((await OpeningAsync(e.Id)).Location, VRChat.Invites.Sent[1].Location);
    }
}
