using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
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

    /// <summary>What the group instance poll leaves behind: an instance it listed, and when it ran.</summary>
    private async Task<string> GroupPollSeesAsync(string? instanceNumber, DateTimeOffset polledAt)
    {
        var location = $"{WorldId}:{instanceNumber}~group({GroupId})~groupAccessType(members)~region(us)";

        await using var context = Database.NewContext();

        if (instanceNumber is not null)
            await new PlaceStore(context, Clock).RecordSightingAsync(location, polledAt, userCount: 0, fromGroupList: true, ct: Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.GroupInstancesPolledAt = polledAt;
        await context.SaveChangesAsync(Ct);

        return location;
    }

    [Fact]
    public async Task A500ThenTheInstanceShowsUp_IsTakenAsTheEvents_WithNoSecondRequest()
    {
        var e = await AutoOpenAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.InternalServerError;

        await OpenAsync();

        var checking = await OpeningAsync(e.Id);
        Assert.True(checking.Checking);
        Assert.False(checking.TryAgain);
        Assert.Null(checking.Error);

        // VRChat made it after all; the group poll lists it a few seconds later.
        Clock.Advance(TimeSpan.FromSeconds(10));
        var location = await GroupPollSeesAsync("777", Clock.UtcNow);
        VRChat.Instances.CreateStatus = HttpStatusCode.OK;

        Assert.Equal(1, (await OpenAsync()).Opened);

        Assert.Single(VRChat.Instances.Created);
        var opening = await OpeningAsync(e.Id);
        Assert.Equal(location, opening.Location);
        Assert.False(opening.Checking);
        Assert.Null(opening.Error);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceOpened));

        // And it stays the only request, however many passes follow.
        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(CalendarOpener.TryAgainAfter);
            await OpenAsync();
        }

        Assert.Single(VRChat.Instances.Created);
    }

    [Fact]
    public async Task A500AndNothingShowsUp_IsShownAsFailed_AndNeverSentAgainOnItsOwn()
    {
        var e = await AutoOpenAsync();
        VRChat.Instances.CreateStatus = HttpStatusCode.InternalServerError;

        await OpenAsync();

        // A poll that ran too soon after the attempt proves nothing yet.
        Clock.Advance(TimeSpan.FromSeconds(5));
        await GroupPollSeesAsync(null, Clock.UtcNow);
        await OpenAsync();
        Assert.True((await OpeningAsync(e.Id)).Checking);

        // One that ran well after it, and listed nothing.
        Clock.Advance(CalendarOpener.PollAfterAttempt);
        await GroupPollSeesAsync(null, Clock.UtcNow);
        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        await OpenAsync();

        var opening = await OpeningAsync(e.Id);
        Assert.False(opening.Checking);
        Assert.False(opening.TryAgain);
        Assert.Equal(CalendarOpener.NoAnswer, opening.Error);

        // Recorded the way a refusal is.
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceFailed));

        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(CalendarOpener.TryAgainAfter);
            await OpenAsync();
        }

        Assert.Single(VRChat.Instances.Created);

        // Open now is the moderator's to press, and it may.
        Assert.Equal(CalendarOpenOutcome.Opened, (await OpenNowAsync(e.Id, Pressed)).Opened);
        Assert.Equal(2, VRChat.Instances.Created.Count);
    }

    [Fact]
    public async Task OpenNowIsRefusedWhileAnAttemptIsInFlight_OrBeingCheckedOn()
    {
        var e = await AddEventAsync(TimeSpan.FromMinutes(30));

        // An attempt written and not yet answered: what the row looks like mid-request.
        await using (var context = Database.NewContext())
        {
            context.CalendarOpenings.Add(new CalendarOpening
            {
                EventId = e.Id,
                OccurrenceStartsAt = e.StartsAt,
                AttemptedAt = Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(CalendarOpenNowOutcome.Checking, (await OpenNowAsync(e.Id, Pressed)).Outcome);

        // Its answer was a 500: still no, while Modbot looks for the instance.
        await using (var context = Database.NewContext())
        {
            var row = await context.CalendarOpenings.SingleAsync(o => o.EventId == e.Id, Ct);
            row.Checking = true;
            await context.SaveChangesAsync(Ct);
        }

        Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(CalendarOpenNowOutcome.Checking, (await OpenNowAsync(e.Id, Pressed)).Outcome);
        Assert.Empty(VRChat.Instances.Created);
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
