using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>Calendar design §4: the instance opens once, at start minus N, and never twice.</summary>
[Collection(nameof(PostgresCollection))]
public class CalendarOpenerTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    [Fact]
    public async Task TheInstanceOpensExactlyOnceAtStartMinusN_EvenAcrossARestart()
    {
        var e = await AddEventAsync(TimeSpan.FromMinutes(30), x =>
        {
            x.AutoOpen = true;
            x.OpenMinutesBefore = 10;
            x.AccessType = "plus";
            x.Region = "eu";
        });

        Clock.Advance(TimeSpan.FromMinutes(19));
        Assert.Equal(0, (await OpenAsync()).Opened);
        Assert.Empty(VRChat.Instances.Created);

        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await OpenAsync()).Opened);

        var request = Assert.Single(VRChat.Instances.Created);
        Assert.Equal(WorldId, request.WorldId);
        Assert.Equal(GroupId, request.OwnerId);
        Assert.Equal(global::VRChat.API.Model.InstanceType.Group, request.Type);
        Assert.Equal(global::VRChat.API.Model.GroupAccessType.Plus, request.GroupAccessType);
        Assert.Equal(global::VRChat.API.Model.InstanceRegion.Eu, request.Region);

        // Every pass after, and a fresh context -- which is all a restart leaves -- opens nothing more.
        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(0, (await OpenAsync()).Opened);
        }

        Assert.Single(VRChat.Instances.Created);

        await using var context = Database.NewContext();
        var opening = await context.CalendarOpenings.AsNoTracking().SingleAsync(o => o.EventId == e.Id, Ct);
        Assert.Equal(e.StartsAt, opening.OccurrenceStartsAt);
        Assert.NotNull(opening.Location);

        // Recorded as an instance the way a sighting is, so Live and the instance cards find it.
        var instance = await context.VRChatInstances.AsNoTracking().SingleAsync(r => r.Id == opening.InstanceId, Ct);
        Assert.Equal(opening.Location, instance.Location);
        Assert.Equal(GroupId, instance.GroupId);

        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceOpened));
    }

    [Fact]
    public async Task AFailureIsNotTriedAgainForThatOccurrence_ButTheNextOccurrenceGetsItsOwnAttempt()
    {
        var e = await AddEventAsync(TimeSpan.FromMinutes(5), x =>
        {
            x.AutoOpen = true;
            x.Repeat = CalendarRepeats.Daily;
        });

        VRChat.Instances.CreateStatus = HttpStatusCode.BadRequest;

        Assert.Equal(1, (await OpenAsync()).Failed);

        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            await OpenAsync();
        }

        Assert.Single(VRChat.Instances.Created);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInstanceFailed));

        await using (var context = Database.NewContext())
        {
            var opening = await context.CalendarOpenings.AsNoTracking().SingleAsync(o => o.EventId == e.Id, Ct);
            Assert.NotNull(opening.Error);
        }

        // Tomorrow's occurrence is a new chance.
        VRChat.Instances.CreateStatus = HttpStatusCode.OK;
        Clock.Advance(TimeSpan.FromDays(1));
        await ScheduleAsync();

        Assert.Equal(1, (await OpenAsync()).Opened);
        Assert.Equal(2, VRChat.Instances.Created.Count);
    }

    [Fact]
    public async Task AnEventThatDoesNotOpenItsInstanceIsLeftAlone()
    {
        await AddEventAsync(TimeSpan.FromMinutes(5));

        await OpenAsync();

        Assert.Empty(VRChat.Instances.Created);
    }

    [Fact]
    public async Task TheSchedulerOpensAndFinishesAnEventOnTheClock()
    {
        var e = await AddEventAsync(TimeSpan.FromMinutes(30));

        await ScheduleAsync();
        Clock.Advance(TimeSpan.FromMinutes(30));
        await ScheduleAsync();

        await using (var context = Database.NewContext())
            Assert.Equal(CalendarEventStates.Open, (await context.CalendarEvents.SingleAsync(x => x.Id == e.Id, Ct)).State);

        Clock.Advance(TimeSpan.FromHours(2));
        await ScheduleAsync();

        await using (var context = Database.NewContext())
            Assert.Equal(CalendarEventStates.Finished, (await context.CalendarEvents.SingleAsync(x => x.Id == e.Id, Ct)).State);

        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventOpened));
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventFinished));
    }
}
