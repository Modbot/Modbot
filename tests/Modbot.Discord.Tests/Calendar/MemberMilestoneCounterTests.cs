using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// What the member milestones are made from: each date of an event is counted once, after its instance
/// has closed, for the people seen there; and a visit after a long gap is kept as a return.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MemberMilestoneCounterTests(PostgresFixture db)
{
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Number = "54321";
    private const string Location = World + ":" + Number + "~group(grp_x)~groupAccessType(members)~region(us)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A weekly event that opened an instance an hour ago, ending in an hour.</summary>
    private static async Task<Guid> OpenedAsync(TestServices services)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + TimeSpan.FromMinutes(5),
            EndsAt = now + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            Repeat = CalendarRepeats.Weekly,
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            AutoOpen = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        CalendarTimeline.Advance(e, now);

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = Location,
            WorldId = World,
            VRChatInstanceId = Number,
            OpenedAt = now,
            LastSeenAt = now,
            PeakUserCount = 2,
        };

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        context.VRChatInstances.Add(instance);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            AttemptedAt = now,
            Location = Location,
            InstanceId = instance.Id,
        });

        await context.SaveChangesAsync(Ct);
        return instance.Id;
    }

    private static Task ArrivedAsync(TestServices services, string userId, DateTimeOffset at, bool inEvent = false)
        => services.WriteFactAsync(
            new FactRecord
            {
                Type = FactType.InstanceJoined,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = userId,
                WorldId = World,
                InstanceId = inEvent ? Number : "99999",
                Source = FactSource.Manual,
            }, Ct);

    private static async Task CloseAsync(TestServices services, Guid instanceId)
    {
        await using var context = services.Database.NewContext();
        var instance = await context.VRChatInstances.SingleAsync(i => i.Id == instanceId, Ct);
        instance.ClosedAt = services.Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ADateIsCountedOnceItsInstanceHasClosed_ForEachPersonSeenThere_AndNeverAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);

        var instanceId = await OpenedAsync(services);
        await ArrivedAsync(services, "usr_a", services.Clock.UtcNow, inEvent: true);
        await ArrivedAsync(services, "usr_b", services.Clock.UtcNow, inEvent: true);

        await using var context = services.Database.NewContext();
        var counter = new MemberMilestoneCounter(context, services.Clock);

        Assert.Equal(0, await counter.CountDatesAsync(Ct));
        Assert.Equal(0, await context.EventAttendees.CountAsync(Ct));

        services.Clock.Advance(TimeSpan.FromMinutes(30));
        await CloseAsync(services, instanceId);

        Assert.Equal(1, await counter.CountDatesAsync(Ct));
        Assert.Equal(0, await counter.CountDatesAsync(Ct));

        var rows = await context.EventAttendees.AsNoTracking().OrderBy(a => a.UserId).ToListAsync(Ct);
        Assert.Equal(["usr_a", "usr_b"], rows.Select(r => r.UserId));
        Assert.All(rows, r => Assert.Equal(services.Clock.UtcNow, r.CountedAt));

        var opening = await context.CalendarOpenings.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(services.Clock.UtcNow, opening.MilestonesCountedAt);
    }

    [Fact]
    public async Task TwoCopiesOfModbotCountingTheSameDate_LeaveEachPersonOnce()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);

        var instanceId = await OpenedAsync(services);
        await ArrivedAsync(services, "usr_a", services.Clock.UtcNow, inEvent: true);
        services.Clock.Advance(TimeSpan.FromMinutes(30));
        await CloseAsync(services, instanceId);

        await using var first = services.Database.NewContext();
        await using var second = services.Database.NewContext();

        await Task.WhenAll(
            new MemberMilestoneCounter(first, services.Clock).CountDatesAsync(Ct),
            new MemberMilestoneCounter(second, services.Clock).CountDatesAsync(Ct));

        await using var context = services.Database.NewContext();
        Assert.Equal(1, await context.EventAttendees.CountAsync(Ct));
    }

    [Fact]
    public async Task AVisitAfterTheLongGap_IsKeptAsAReturn_AndTheFirstVisitNever()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var now = services.Clock.UtcNow;

        await ArrivedAsync(services, "usr_a", now - TimeSpan.FromDays(100));
        await ArrivedAsync(services, "usr_a", now - TimeSpan.FromDays(30));
        await ArrivedAsync(services, "usr_b", now - TimeSpan.FromDays(5));

        await using var context = services.Database.NewContext();
        await new MemberMilestoneCounter(context, services.Clock).ReadVisitsAsync(Ct);

        var returned = await context.MemberMilestones.AsNoTracking().SingleAsync(m => m.UserId == "usr_a", Ct);
        Assert.Equal(now - TimeSpan.FromDays(30), returned.LastVisitAt);
        Assert.Equal(now - TimeSpan.FromDays(30), returned.BackAt);
        Assert.Equal(70, returned.BackAfterDays);

        var newcomer = await context.MemberMilestones.AsNoTracking().SingleAsync(m => m.UserId == "usr_b", Ct);
        Assert.Equal(now - TimeSpan.FromDays(5), newcomer.LastVisitAt);
        Assert.Null(newcomer.BackAt);
    }

    [Fact]
    public async Task TheGapIsTheSettingsNumberOfDaysOrMore()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var now = services.Clock.UtcNow;

        await services.ConfigureAsync(s => s.MilestoneBackDays = 60, Ct);

        await ArrivedAsync(services, "usr_a", now - TimeSpan.FromDays(100));
        await ArrivedAsync(services, "usr_a", now - TimeSpan.FromDays(40));
        await ArrivedAsync(services, "usr_b", now - TimeSpan.FromDays(100));
        await ArrivedAsync(services, "usr_b", now - TimeSpan.FromDays(41));

        await using var context = services.Database.NewContext();
        await new MemberMilestoneCounter(context, services.Clock).ReadVisitsAsync(Ct);

        var exactly = await context.MemberMilestones.AsNoTracking().SingleAsync(m => m.UserId == "usr_a", Ct);
        Assert.Equal(60, exactly.BackAfterDays);

        var shorter = await context.MemberMilestones.AsNoTracking().SingleAsync(m => m.UserId == "usr_b", Ct);
        Assert.Null(shorter.BackAt);
    }

    [Fact]
    public async Task SeveralArrivalsOnOneDay_AreNotAReturn()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var now = services.Clock.UtcNow;

        await ArrivedAsync(services, "usr_a", now - TimeSpan.FromDays(10));
        await ArrivedAsync(services, "usr_a", now - TimeSpan.FromDays(10) + TimeSpan.FromHours(3));

        await using var context = services.Database.NewContext();
        await new MemberMilestoneCounter(context, services.Clock).ReadVisitsAsync(Ct);

        var row = await context.MemberMilestones.AsNoTracking().SingleAsync(m => m.UserId == "usr_a", Ct);
        Assert.Equal(now - TimeSpan.FromDays(10) + TimeSpan.FromHours(3), row.LastVisitAt);
        Assert.Null(row.BackAt);
    }
}
