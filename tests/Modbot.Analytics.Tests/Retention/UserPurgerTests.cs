using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Retention;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// Spec 5.5: "a purge-user action erases every fact for one user on request". The claim that
/// deletion works is the one that has to survive contact with a real request.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UserPurgerTests : AnalyticsTestBase
{
    /// <summary>A metric with no fact behind it, counted directly.</summary>
    private const string CountedMetric = "test.counted";

    public UserPurgerTests(PostgresFixture fixture) : base(fixture) { }

    private const string Subject = "usr_purge_me";
    private const string Bystander = "usr_someone_else";

    private UserPurger NewPurger(ModbotContext context) => new(
        context,
        Clock,
        NewJob(context),
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    [Fact]
    public async Task EveryFactAboutTheSubjectGoes_AcrossPartitions()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start.AddDays(-90), subjectId: Subject),
            Fact(FactType.InstanceJoined, Start.AddDays(-40), subjectId: Subject),
            Fact(FactType.InstanceLeft, Start, subjectId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Bystander));

        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(3, result.FactsDeleted);
        Assert.Equal(0, await CountAsync(Subject));
        Assert.Equal(1, await CountAsync(Bystander));
    }

    /// <summary>
    /// Imported facts are ordinary facts about the person and go with the rest, and so do the
    /// rows that say "this record was imported" (import design §7) -- otherwise a later upload of
    /// the same file would silently skip exactly the records that were erased.
    /// </summary>
    [Fact]
    public async Task ImportedFacts_AndTheirDedupeRows_GoToo()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start.AddYears(-2), subjectId: Subject, source: FactSource.Import),
            Fact(FactType.Unrecognised, Start.AddYears(-1), subjectId: Subject, source: FactSource.Import),
            Fact(FactType.MemberBanned, Start.AddYears(-2), subjectId: Bystander, source: FactSource.Import));

        await using (var setup = Database.NewContext())
        {
            setup.ImportRecords.AddRange(
                new ImportRecord { Source = "old-bot", Key = "id:ban-1", SubjectPlatform = FactPlatform.VRChat, SubjectId = Subject, ImportedAt = Start },
                new ImportRecord { Source = "old-bot", Key = "hash:abc", SubjectPlatform = FactPlatform.VRChat, SubjectId = Subject, ImportedAt = Start },
                new ImportRecord { Source = "old-bot", Key = "id:ban-2", SubjectPlatform = FactPlatform.VRChat, SubjectId = Bystander, ImportedAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(2, result.FactsDeleted);
        Assert.Equal(0, await CountAsync(Subject));
        Assert.Equal(1, await CountAsync(Bystander));

        await using var read = Database.NewContext();
        var left = await read.ImportRecords.AsNoTracking().OrderBy(r => r.Key).ToListAsync(Ct);
        var only = Assert.Single(left);
        Assert.Equal(Bystander, only.SubjectId);
    }

    /// <summary>
    /// The purge cuts across partitions, which is exactly why it is the one operation that gets to
    /// be a DELETE rather than a partition drop.
    /// </summary>
    [Fact]
    public async Task FactsFromDifferentMonthsAreAllRemoved()
    {
        var months = new[] { Start.AddMonths(-4), Start.AddMonths(-2), Start };

        foreach (var month in months)
            await WriteAsync(Fact(FactType.InstanceJoined, month, subjectId: Subject));

        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(months.Length, result.FactsDeleted);
        Assert.Equal(0, await CountAsync(Subject));
    }

    /// <summary>
    /// A moderator's ban is a record about the person banned. Erasing it because the moderator
    /// asked would delete someone else's moderation history, and the accountability in spec 5.8 is
    /// built entirely on those rows.
    /// </summary>
    [Fact]
    public async Task FactsWhereTheyWereTheActorAreKept()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start, subjectId: Bystander, actorId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Subject));

        await using var context = Database.NewContext();
        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(0, await CountAsync(Subject));

        var survivor = await context.Events
            .AsNoTracking()
            .SingleAsync(e => e.Type == FactType.MemberBanned, Ct);

        Assert.Equal(Subject, survivor.ActorId);
        Assert.Equal(Bystander, survivor.SubjectId);
    }

    /// <summary>
    /// Aggregates that still counted the purged facts would make the deletion cosmetic.
    /// </summary>
    [Fact]
    public async Task DailyTotalsNoLongerCountThePurgedFacts()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start, subjectId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Bystander),
            Fact(FactType.MemberJoined, Start.AddDays(1), subjectId: Bystander));

        await using var context = Database.NewContext();
        await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));

        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersNet));

        // And the running total after the purged day moves with it.
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(1)), DailyTotalMetrics.MembersNet));
    }

    /// <summary>
    /// The counted-only rows are the ones with no fact behind them (spec 5.2.1) -- a per-user
    /// message count is precisely the personal data a purge is about, and nothing else would
    /// remove it.
    /// </summary>
    [Fact]
    public async Task PerUserCountsWithNoFactBehindThemAreErased()
    {
        await using var context = Database.NewContext();
        var counter = new DailyTotalCounter(context, Clock);

        await counter.IncrementAsync(
            CountedMetric,
            DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject),
            120m,
            ct: Ct);

        await counter.IncrementAsync(
            CountedMetric,
            DailyTotalDimensions.ForUser(FactPlatform.VRChat, Bystander),
            7m,
            ct: Ct);

        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(1, result.CountedDailyTotalsDeleted);

        var today = DayOf(Clock.UtcNow);
        Assert.Null(await ValueAsync(
            today, CountedMetric, DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject)));
        Assert.Equal(7m, await ValueAsync(
            today, CountedMetric, DailyTotalDimensions.ForUser(FactPlatform.VRChat, Bystander)));
    }

    /// <summary>
    /// Computed rows that follow from facts the purge did not touch stay, because a rebuild would
    /// only put them back. Deleting them would be theatre and would break the invariant.
    /// </summary>
    [Fact]
    public async Task ModeratorTotalsForSurvivingFactsAreLeftIntact()
    {
        await WriteAsync(Fact(FactType.MemberBanned, Start, subjectId: Bystander, actorId: Subject));

        await using var context = Database.NewContext();
        await NewJob(context).RebuildAsync(Ct);
        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        var dimension = DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject);
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorBans, dimension));
    }

    /// <summary>
    /// An erasure log that names the erased person is not an erasure.
    /// </summary>
    [Fact]
    public async Task ThePurgeIsRecordedWithoutNamingWhoWasPurged()
    {
        await WriteAsync(Fact(FactType.InstanceJoined, Start, subjectId: Subject));

        await using var context = Database.NewContext();
        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        var record = await context.Events
            .AsNoTracking()
            .SingleAsync(e => e.Type == FactType.UserPurged, Ct);

        Assert.Equal(FactSource.Modbot, record.Source);
        Assert.DoesNotContain(Subject, record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(Subject, record.SubjectId, StringComparison.Ordinal);
        Assert.Contains("\"facts\": 1", record.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgingSomeoneWithNoFactsIsHarmless()
    {
        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, "usr_nobody", ct: Ct);

        Assert.Equal(0, result.FactsDeleted);
        Assert.Equal(0, result.DaysRecomputed);
    }

    /// <summary>
    /// The reminders a member asked for with <c>/remindme</c> (Discord commands design §3.5) name their
    /// Discord account, so a purge takes them whatever their state -- for the Discord account, and for
    /// the VRChat account it is linked to -- and leaves everybody else's.
    /// </summary>
    [Fact]
    public async Task TheRemindersTheyAskedFor_GoToo_ForTheDiscordAccountAndItsLinkedVRChatAccount()
    {
        const string DiscordSubject = "7001";
        const string DiscordBystander = "7002";

        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            StartsAt = Start.AddDays(1),
            EndsAt = Start.AddDays(1).AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        EventReminder Reminder(string user, string state) => new()
        {
            DiscordUserId = user,
            EventId = calendarEvent.Id,
            OccurrenceStartsAt = calendarEvent.StartsAt,
            MinutesBefore = 60,
            RemindAt = calendarEvent.StartsAt.AddHours(-1),
            State = state,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        async Task SetUpAsync()
        {
            await using var setup = Database.NewContext();
            await setup.EventReminders.ExecuteDeleteAsync(Ct);

            if (!await setup.CalendarEvents.AnyAsync(e => e.Id == calendarEvent.Id, Ct))
                setup.CalendarEvents.Add(calendarEvent);

            if (!await setup.DiscordAccountLinks.AnyAsync(l => l.DiscordUserId == DiscordSubject, Ct))
            {
                setup.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = DiscordSubject,
                    DiscordUsername = "member",
                    VRChatUserId = Subject,
                    LinkedAt = Start,
                });
            }

            setup.EventReminders.AddRange(
                Reminder(DiscordSubject, EventReminderStates.Waiting),
                Reminder(DiscordSubject, EventReminderStates.Sent),
                Reminder(DiscordSubject, EventReminderStates.Stopped),
                Reminder(DiscordBystander, EventReminderStates.Waiting));
            await setup.SaveChangesAsync(Ct);
        }

        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);
            Assert.Equal(3, result.EventRemindersDeleted);
        }

        await using (var read = Database.NewContext())
        {
            var left = await read.EventReminders.AsNoTracking().ToListAsync(Ct);
            Assert.Equal(DiscordBystander, Assert.Single(left).DiscordUserId);
        }

        // The same, asked about the VRChat account the Discord one is linked to.
        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);
            Assert.Equal(3, result.EventRemindersDeleted);
        }

        await using var again = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await again.EventReminders.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        // And the record of the purge counts them, naming nobody.
        var record = await again.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged)
            .OrderByDescending(e => e.Id)
            .FirstAsync(Ct);
        Assert.Contains("\"eventReminders\": 3", record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordSubject, record.Data, StringComparison.Ordinal);
    }

    /// <summary>
    /// Which invite a member joined the Discord server with (invite tracking) names their Discord
    /// account and the time, so a purge takes it -- for the Discord account, and for the VRChat account
    /// it is linked to -- and leaves everybody else's and the invite itself.
    /// </summary>
    [Fact]
    public async Task TheInviteTheyJoinedWith_Goes_ForTheDiscordAccountAndItsLinkedVRChatAccount()
    {
        const string DiscordSubject = "7101";
        const string DiscordBystander = "7102";

        var invite = new DiscordInviteLink { Code = "linktree", Name = "Linktree", FirstSeenAt = Start };

        async Task SetUpAsync()
        {
            await using var setup = Database.NewContext();
            await setup.DiscordInviteJoins.ExecuteDeleteAsync(Ct);

            if (!await setup.DiscordInviteLinks.AnyAsync(i => i.Id == invite.Id, Ct))
                setup.DiscordInviteLinks.Add(invite);

            if (!await setup.DiscordAccountLinks.AnyAsync(l => l.DiscordUserId == DiscordSubject, Ct))
            {
                setup.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = DiscordSubject,
                    DiscordUsername = "member",
                    VRChatUserId = Subject,
                    LinkedAt = Start,
                });
            }

            setup.DiscordInviteJoins.AddRange(
                new DiscordInviteJoin { UserId = DiscordSubject, JoinedAt = Start, InviteId = invite.Id },
                new DiscordInviteJoin { UserId = DiscordSubject, JoinedAt = Start.AddDays(1), InviteId = null },
                new DiscordInviteJoin { UserId = DiscordBystander, JoinedAt = Start, InviteId = invite.Id });
            await setup.SaveChangesAsync(Ct);
        }

        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);
            Assert.Equal(2, result.InviteJoinsDeleted);
        }

        await using (var read = Database.NewContext())
        {
            Assert.Equal(DiscordBystander, Assert.Single(await read.DiscordInviteJoins.AsNoTracking().ToListAsync(Ct)).UserId);
            Assert.Single(await read.DiscordInviteLinks.AsNoTracking().ToListAsync(Ct));
        }

        // The same, asked about the VRChat account the Discord one is linked to.
        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);
            Assert.Equal(2, result.InviteJoinsDeleted);
        }

        await using var again = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await again.DiscordInviteJoins.AsNoTracking().ToListAsync(Ct)).UserId);

        // And the record of the purge counts them, naming nobody.
        var record = await again.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged)
            .OrderByDescending(e => e.Id)
            .FirstAsync(Ct);
        Assert.Contains("\"inviteJoins\": 2", record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordSubject, record.Data, StringComparison.Ordinal);
    }

    /// <summary>
    /// A purge takes the note that a Discord account was sent the link reminder -- for the Discord
    /// account, and for the VRChat account it is linked to -- and leaves everybody else's.
    /// </summary>
    [Fact]
    public async Task TheLinkReminderTheyWereSent_Goes_ForTheDiscordAccountAndItsLinkedVRChatAccount()
    {
        const string DiscordSubject = "7201";
        const string DiscordBystander = "7202";

        async Task SetUpAsync()
        {
            await using var setup = Database.NewContext();
            await setup.DiscordLinkNudges.ExecuteDeleteAsync(Ct);

            if (!await setup.DiscordAccountLinks.AnyAsync(l => l.DiscordUserId == DiscordSubject, Ct))
            {
                setup.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = DiscordSubject,
                    DiscordUsername = "member",
                    VRChatUserId = Subject,
                    LinkedAt = Start,
                });
            }

            setup.DiscordLinkNudges.AddRange(
                new DiscordLinkNudge { DiscordUserId = DiscordSubject, State = DiscordLinkNudgeStates.Sent, CreatedAt = Start },
                new DiscordLinkNudge { DiscordUserId = DiscordBystander, State = DiscordLinkNudgeStates.Sent, CreatedAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);
            Assert.Equal(1, result.LinkRemindersDeleted);
        }

        await using (var read = Database.NewContext())
            Assert.Equal(DiscordBystander, Assert.Single(await read.DiscordLinkNudges.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        // The same, asked about the VRChat account the Discord one is linked to.
        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);
            Assert.Equal(1, result.LinkRemindersDeleted);
        }

        await using var again = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await again.DiscordLinkNudges.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        var record = await again.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged)
            .OrderByDescending(e => e.Id)
            .FirstAsync(Ct);
        Assert.Contains("\"linkReminders\": 1", record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordSubject, record.Data, StringComparison.Ordinal);
    }

    /// <summary>
    /// A purge takes the row saying a Discord account was welcomed (or is waiting for the join gate to
    /// be) -- for the Discord account, and for the VRChat account it is linked to -- and leaves
    /// everybody else's.
    /// </summary>
    [Fact]
    public async Task TheWelcomeTheyWereGiven_Goes_ForTheDiscordAccountAndItsLinkedVRChatAccount()
    {
        const string DiscordSubject = "7301";
        const string DiscordBystander = "7302";

        async Task SetUpAsync()
        {
            await using var setup = Database.NewContext();
            await setup.DiscordWelcomes.ExecuteDeleteAsync(Ct);

            if (!await setup.DiscordAccountLinks.AnyAsync(l => l.DiscordUserId == DiscordSubject, Ct))
            {
                setup.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = DiscordSubject,
                    DiscordUsername = "member",
                    VRChatUserId = Subject,
                    LinkedAt = Start,
                });
            }

            setup.DiscordWelcomes.AddRange(
                new DiscordWelcome { DiscordUserId = DiscordSubject, State = DiscordWelcomeStates.Sent, CreatedAt = Start, SentAt = Start },
                new DiscordWelcome { DiscordUserId = DiscordBystander, State = DiscordWelcomeStates.Waiting, CreatedAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);
            Assert.Equal(1, result.WelcomesDeleted);
        }

        await using (var read = Database.NewContext())
            Assert.Equal(DiscordBystander, Assert.Single(await read.DiscordWelcomes.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        // The same, asked about the VRChat account the Discord one is linked to.
        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);
            Assert.Equal(1, result.WelcomesDeleted);
        }

        await using var again = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await again.DiscordWelcomes.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        var record = await again.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged)
            .OrderByDescending(e => e.Id)
            .FirstAsync(Ct);
        Assert.Contains("\"welcomes\": 1", record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordSubject, record.Data, StringComparison.Ordinal);
    }

    /// <summary>
    /// A purge takes the Ko-fi supporter matched to a Discord account, with their payments, and leaves
    /// everybody else's.
    /// </summary>
    [Fact]
    public async Task TheSupporterMatchedToTheirDiscordAccount_Goes_WithTheirPayments()
    {
        const string DiscordSubject = "7401";
        const string DiscordBystander = "7402";

        await using (var setup = Database.NewContext())
        {
            await setup.Supporters.ExecuteDeleteAsync(Ct);

            foreach (var id in new[] { DiscordSubject, DiscordBystander })
            {
                var supporter = new Supporter
                {
                    PayerId = "payer" + id,
                    Name = "Supporter " + id,
                    DiscordUserId = id,
                    FirstPaidAt = Start,
                    LastPaidAt = Start,
                    PaidThrough = Start.AddMonths(1),
                    UpdatedAt = Start,
                };
                setup.Supporters.Add(supporter);
                setup.SupporterPayments.Add(new SupporterPayment { Code = "code" + id, SupporterId = supporter.Id, PaidAt = Start, ReceivedAt = Start });
            }

            await setup.SaveChangesAsync(Ct);
        }

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);
            Assert.Equal(1, result.SupportersDeleted);
        }

        await using var read = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await read.Supporters.AsNoTracking().ToListAsync(Ct)).DiscordUserId);
        Assert.Equal("code" + DiscordBystander, Assert.Single(await read.SupporterPayments.AsNoTracking().ToListAsync(Ct)).Code);
    }

    /// <summary>
    /// A purge takes the birthday a member set (a Discord purge, or a VRChat one through the linked
    /// account) and leaves everybody else's. A row whose birthday role is still held stays with no
    /// month and day, so the next birthday pass can take the role back and delete it.
    /// </summary>
    [Fact]
    public async Task TheBirthdayTheySet_Goes_AndARoleStillHeldIsKeptWithNoDateSoItCanBeTakenBack()
    {
        const string DiscordSubject = "7401";
        const string DiscordBystander = "7402";
        const string Role = "8001";

        async Task SetUpAsync(string? roleHeld)
        {
            await using var setup = Database.NewContext();
            await setup.MemberBirthdays.ExecuteDeleteAsync(Ct);

            if (!await setup.DiscordAccountLinks.AnyAsync(l => l.DiscordUserId == DiscordSubject, Ct))
            {
                setup.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = DiscordSubject,
                    DiscordUsername = "member",
                    VRChatUserId = Subject,
                    LinkedAt = Start,
                });
            }

            setup.MemberBirthdays.AddRange(
                new MemberBirthday
                {
                    DiscordUserId = DiscordSubject,
                    Month = 3,
                    Day = 14,
                    SetAt = Start,
                    RoleGivenId = roleHeld,
                    RoleGivenOn = roleHeld is null ? null : DateOnly.FromDateTime(Start.UtcDateTime),
                },
                new MemberBirthday { DiscordUserId = DiscordBystander, Month = 5, Day = 6, SetAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        // No role held: the row is gone, the bystander's stays.
        await SetUpAsync(null);

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);

        await using (var read = Database.NewContext())
            Assert.Equal(DiscordBystander, Assert.Single(await read.MemberBirthdays.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        // The same through the VRChat account the Discord one is linked to.
        await SetUpAsync(null);

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        await using (var read = Database.NewContext())
            Assert.Equal(DiscordBystander, Assert.Single(await read.MemberBirthdays.AsNoTracking().ToListAsync(Ct)).DiscordUserId);

        // A role still held: the date is gone, the role stays to be taken back.
        await SetUpAsync(Role);

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);

        await using var again = Database.NewContext();
        var rows = await again.MemberBirthdays.AsNoTracking().OrderBy(b => b.DiscordUserId).ToListAsync(Ct);
        Assert.Equal([DiscordSubject, DiscordBystander], rows.Select(b => b.DiscordUserId));

        var kept = rows[0];
        Assert.True(kept.IsForgotten);
        Assert.Equal(0, kept.Month);
        Assert.Equal(0, kept.Day);
        Assert.Equal(Role, kept.RoleGivenId);

        Assert.Equal((5, 6), (rows[1].Month, rows[1].Day));
    }

    /// <summary>A purge takes the member's Findable switch (member profiles design §8) and leaves everybody else's.</summary>
    [Fact]
    public async Task TheFindableSwitchOfThePurgedMemberGoes()
    {
        const string DiscordSubject = "7101";
        const string DiscordBystander = "7102";

        await using (var setup = Database.NewContext())
        {
            setup.MemberProfiles.AddRange(
                new MemberProfile { DiscordUserId = DiscordSubject, Findable = true, UpdatedAt = Start },
                new MemberProfile { DiscordUserId = DiscordBystander, Findable = true, UpdatedAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);

        await using var read = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await read.MemberProfiles.AsNoTracking().ToListAsync(Ct)).DiscordUserId);
    }

    [Fact]
    public async Task ThePicturesThePurgedMemberPostedForTheGalleryGo_ButNobodyElses()
    {
        const string DiscordSubject = "7301";
        const string DiscordBystander = "7302";

        GallerySubmission Picture(string author, string message) => new()
        {
            Id = Guid.NewGuid(),
            DiscordMessageId = message,
            DiscordChannelId = "chan_1",
            AuthorDiscordId = author,
            AuthorName = "Rowan",
            Bytes = [1, 2, 3],
            SubmittedAt = Start,
        };

        await using (var setup = Database.NewContext())
        {
            setup.GallerySubmissions.AddRange(
                Picture(DiscordSubject, "m1"),
                Picture(DiscordSubject, "m2"),
                Picture(DiscordBystander, "m3"));
            await setup.SaveChangesAsync(Ct);
        }

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);

        await using var read = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await read.GallerySubmissions.AsNoTracking().ToListAsync(Ct)).AuthorDiscordId);
    }

    /// <summary>
    /// A purge takes the member's opt-in, clears them from the weeks they were featured, and deletes
    /// the spotlight drafts that have not gone out (with their avatar copy); a draft that went out, and
    /// everybody else's, stay (spotlight design §9).
    /// </summary>
    [Fact]
    public async Task TheSpotlightOfThePurgedMemberGoes_UnsentDraftsToo_ButASentPostStays()
    {
        const string DiscordSubject = "7201";
        const string DiscordBystander = "7202";

        Post Draft() => new()
        {
            Id = Guid.CreateVersion7(),
            Kind = PostKinds.Spotlight,
            Status = PostStatuses.Draft,
            Title = "Member spotlight",
            Text = "Hobby: Pottery",
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        var unsent = Draft();
        var sent = Draft();
        var bystander = Draft();
        var avatar = new CalendarCoverPicture { Id = Guid.CreateVersion7(), Bytes = [1, 2, 3], ContentType = "image/png", CreatedAt = Start };
        unsent.PictureId = avatar.Id;
        sent.Destinations.Add(new PostDestination
        {
            Id = Guid.CreateVersion7(),
            Network = PostNetworks.Discord,
            State = PostDestinationStates.Posted,
        });

        await using (var setup = Database.NewContext())
        {
            await setup.SpotlightWeeks.ExecuteDeleteAsync(Ct);
            await setup.Posts.Where(p => p.Kind == PostKinds.Spotlight).ExecuteDeleteAsync(Ct);
            await setup.MemberProfiles.ExecuteDeleteAsync(Ct);

            setup.CalendarCoverPictures.Add(avatar);
            setup.Posts.AddRange(unsent, sent, bystander);
            setup.MemberProfiles.AddRange(
                new MemberProfile { DiscordUserId = DiscordSubject, Spotlight = true, UpdatedAt = Start },
                new MemberProfile { DiscordUserId = DiscordBystander, Spotlight = true, UpdatedAt = Start });
            await setup.SaveChangesAsync(Ct);

            setup.SpotlightWeeks.AddRange(
                new SpotlightWeek { Id = Guid.CreateVersion7(), Week = new DateOnly(2026, 9, 28), DiscordUserId = DiscordSubject, PostId = sent.Id, CreatedAt = Start },
                new SpotlightWeek { Id = Guid.CreateVersion7(), Week = new DateOnly(2026, 10, 5), DiscordUserId = DiscordSubject, PostId = unsent.Id, CreatedAt = Start },
                new SpotlightWeek { Id = Guid.CreateVersion7(), Week = new DateOnly(2026, 10, 12), DiscordUserId = DiscordBystander, PostId = bystander.Id, CreatedAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);

        await using var read = Database.NewContext();
        Assert.Equal(DiscordBystander, Assert.Single(await read.MemberProfiles.AsNoTracking().ToListAsync(Ct)).DiscordUserId);
        Assert.Equal(
            new HashSet<Guid> { sent.Id, bystander.Id },
            (await read.Posts.AsNoTracking().Where(p => p.Kind == PostKinds.Spotlight).Select(p => p.Id).ToListAsync(Ct)).ToHashSet());
        Assert.False(await read.CalendarCoverPictures.AnyAsync(c => c.Id == avatar.Id, Ct));

        var weeks = await read.SpotlightWeeks.AsNoTracking().OrderBy(w => w.Week).ToListAsync(Ct);
        Assert.Equal(3, weeks.Count);
        Assert.Null(weeks[0].DiscordUserId);
        Assert.Equal(sent.Id, weeks[0].PostId);
        Assert.Null(weeks[1].DiscordUserId);
        Assert.Null(weeks[1].PostId);
        Assert.Equal(DiscordBystander, weeks[2].DiscordUserId);
    }

    /// <summary>
    /// The event alerts a member signed up for (event alerts design §7) name their Discord account in
    /// three tables -- sign-ups, the choice of way and the alerts queued for them -- so a purge takes
    /// all three, for the Discord account and for the VRChat account it is linked to, and leaves
    /// everybody else's. The record of the purge counts them together and names nobody.
    /// </summary>
    [Fact]
    public async Task TheEventAlertsTheySignedUpFor_GoToo_ForTheDiscordAccountAndItsLinkedVRChatAccount()
    {
        const string DiscordSubject = "7101";
        const string DiscordBystander = "7102";

        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Karaoke",
            StartsAt = Start.AddDays(1),
            EndsAt = Start.AddDays(1).AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        var category = new CalendarCategory
        {
            Id = Guid.CreateVersion7(),
            Name = "Karaoke " + Guid.NewGuid().ToString("N")[..8],
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        var firstAlert = new EventAlert { EventId = calendarEvent.Id, OccurrenceStartsAt = calendarEvent.StartsAt, QueuedAt = Start };
        var secondAlert = new EventAlert { EventId = calendarEvent.Id, OccurrenceStartsAt = calendarEvent.StartsAt.AddDays(7), QueuedAt = Start };

        async Task SetUpAsync()
        {
            await using var setup = Database.NewContext();
            await setup.EventAlertDeliveries.ExecuteDeleteAsync(Ct);
            await setup.EventAlerts.ExecuteDeleteAsync(Ct);
            await setup.EventAlertMembers.ExecuteDeleteAsync(Ct);
            await setup.EventAlertSignups.ExecuteDeleteAsync(Ct);

            if (!await setup.CalendarEvents.AnyAsync(e => e.Id == calendarEvent.Id, Ct))
                setup.CalendarEvents.Add(calendarEvent);

            if (!await setup.CalendarCategories.AnyAsync(c => c.Id == category.Id, Ct))
                setup.CalendarCategories.Add(category);

            if (!await setup.DiscordAccountLinks.AnyAsync(l => l.DiscordUserId == DiscordSubject, Ct))
            {
                setup.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = DiscordSubject,
                    DiscordUsername = "member",
                    VRChatUserId = Subject,
                    LinkedAt = Start,
                });
            }

            await setup.SaveChangesAsync(Ct);

            firstAlert.Id = Guid.CreateVersion7();
            secondAlert.Id = Guid.CreateVersion7();
            setup.EventAlerts.AddRange(firstAlert, secondAlert);

            setup.EventAlertSignups.AddRange(
                new EventAlertSignup { DiscordUserId = DiscordSubject, CategoryId = category.Id, CreatedAt = Start },
                new EventAlertSignup { DiscordUserId = DiscordSubject, EventId = calendarEvent.Id, CreatedAt = Start },
                new EventAlertSignup { DiscordUserId = DiscordBystander, CategoryId = category.Id, CreatedAt = Start });

            setup.EventAlertMembers.AddRange(
                new EventAlertMember { DiscordUserId = DiscordSubject, Way = EventAlertSettings.Ping, UpdatedAt = Start },
                new EventAlertMember { DiscordUserId = DiscordBystander, Way = EventAlertSettings.Direct, UpdatedAt = Start });

            setup.EventAlertDeliveries.AddRange(
                new EventAlertDelivery { AlertId = firstAlert.Id, DiscordUserId = DiscordSubject, UpdatedAt = Start },
                new EventAlertDelivery { AlertId = secondAlert.Id, DiscordUserId = DiscordSubject, UpdatedAt = Start },
                new EventAlertDelivery { AlertId = firstAlert.Id, DiscordUserId = DiscordBystander, UpdatedAt = Start });

            await setup.SaveChangesAsync(Ct);
        }

        async Task AssertOnlyTheBystanderIsLeftAsync()
        {
            await using var read = Database.NewContext();
            Assert.Equal(DiscordBystander, Assert.Single(await read.EventAlertSignups.AsNoTracking().ToListAsync(Ct)).DiscordUserId);
            Assert.Equal(DiscordBystander, Assert.Single(await read.EventAlertMembers.AsNoTracking().ToListAsync(Ct)).DiscordUserId);
            Assert.Equal(DiscordBystander, Assert.Single(await read.EventAlertDeliveries.AsNoTracking().ToListAsync(Ct)).DiscordUserId);
        }

        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, DiscordSubject, ct: Ct);
            Assert.Equal(5, result.EventAlertsDeleted);
        }

        await AssertOnlyTheBystanderIsLeftAsync();

        // The same, asked about the VRChat account the Discord one is linked to.
        await SetUpAsync();

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);
            Assert.Equal(5, result.EventAlertsDeleted);
        }

        await AssertOnlyTheBystanderIsLeftAsync();

        // And the record of the purge counts them, naming nobody.
        await using var again = Database.NewContext();
        var record = await again.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged)
            .OrderByDescending(e => e.Id)
            .FirstAsync(Ct);
        Assert.Contains("\"eventAlerts\": 5", record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordSubject, record.Data, StringComparison.Ordinal);
    }

    private async Task<int> CountAsync(string subjectId)
    {
        await using var context = Database.NewContext();
        return await context.Events.AsNoTracking().CountAsync(e => e.SubjectId == subjectId, Ct);
    }
}
