using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// Discord's interested count for a date, written once when the date has started, from one read of
/// the server's events and none while no date is waiting for a count.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventInterestSnapshotTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string DiscordEventId = "900";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> SnapshotAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new EventInterestSnapshot(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    /// <summary>An event that started ten minutes ago and runs for two hours, with its Discord event.</summary>
    private static async Task<CalendarEvent> RunningEventAsync(TestServices services)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            StartsAt = now - TimeSpan.FromMinutes(10),
            EndsAt = now + TimeSpan.FromMinutes(110),
            TimeZone = "UTC",
            Repeat = CalendarRepeats.None,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        context.CalendarEventPlaces.Add(new CalendarEventPlace
        {
            EventId = e.Id,
            Place = CalendarPlaces.DiscordEvent,
            State = CalendarPlaceStates.Published,
            ExternalId = DiscordEventId,
            OccurrenceStartsAt = e.StartsAt,
        });

        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static DiscordServerEvent Listed(int? interested) =>
        new(DiscordEventId, "Movie night", DateTimeOffset.UnixEpoch, null, true, DiscordEventMakers.Modbot, "Modbot", interested);

    [Fact]
    public async Task TheCountIsWrittenOnceTheDateHasStarted_AndNotChangedByALaterPass()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { ServerEventList = [Listed(14)] };
        var e = await RunningEventAsync(services);

        Assert.Equal(1, await SnapshotAsync(services, gateway));

        gateway.ServerEventList = [Listed(40)];
        services.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(0, await SnapshotAsync(services, gateway));

        await using var context = services.Database.NewContext();
        var row = await context.CalendarInterests.SingleAsync(i => i.EventId == e.Id, Ct);
        Assert.Equal(14, row.DiscordInterested);
        Assert.Equal(e.StartsAt, row.OccurrenceStartsAt);
    }

    [Fact]
    public async Task DiscordIsNotAskedWhileNoDateIsWaitingForACount()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { ServerEventList = [Listed(14)] };

        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        Assert.Equal(0, await SnapshotAsync(services, gateway));
        Assert.Equal(0, gateway.ServerEventReads);

        await RunningEventAsync(services);
        await SnapshotAsync(services, gateway);
        await SnapshotAsync(services, gateway);

        Assert.Equal(1, gateway.ServerEventReads);
    }

    [Fact]
    public async Task ADateNotYetStarted_IsNotWritten()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { ServerEventList = [Listed(14)] };
        var e = await RunningEventAsync(services);

        services.Clock.Advance(TimeSpan.FromMinutes(-30));

        Assert.True(services.Clock.UtcNow < e.StartsAt);
        Assert.Equal(0, await SnapshotAsync(services, gateway));
        Assert.Equal(0, gateway.ServerEventReads);
    }

    [Fact]
    public async Task ADiscordEventMissingFromTheList_IsWrittenWithNoCount()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { ServerEventList = [] };
        var e = await RunningEventAsync(services);

        Assert.Equal(1, await SnapshotAsync(services, gateway));

        await using var context = services.Database.NewContext();
        var row = await context.CalendarInterests.SingleAsync(i => i.EventId == e.Id, Ct);
        Assert.Null(row.DiscordInterested);
        Assert.NotNull(row.DiscordTakenAt);
    }

    [Fact]
    public async Task WhenDiscordDoesNotAnswer_NothingIsWritten_AndTheNextPassTriesAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { ServerEventList = null };
        await RunningEventAsync(services);

        Assert.Equal(0, await SnapshotAsync(services, gateway));

        gateway.ServerEventList = [Listed(6)];

        Assert.Equal(1, await SnapshotAsync(services, gateway));
    }
}
