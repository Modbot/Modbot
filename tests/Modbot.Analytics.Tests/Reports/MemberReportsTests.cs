using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reports;
using Modbot.Analytics.Retention;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Reports;

/// <summary>
/// Member reports (Discord commands design §3.4): what is kept and for how long, what the facts
/// say and leave out, and what a purge removes.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MemberReportsTests : AnalyticsTestBase
{
    private const string Reporter = "100000000000000001";
    private const string Reported = "200000000000000002";
    private const string Other = "300000000000000003";

    public MemberReportsTests(PostgresFixture fixture) : base(fixture) { }

    private MemberReports NewReports(ModbotContext context) => new(
        context,
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock),
        Clock);

    private UserPurger NewPurger(ModbotContext context) => new(
        context,
        Clock,
        NewJob(context),
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    private async Task<MemberReport> AddAsync(
        string reporter,
        string reported,
        string state = MemberReportStates.Open,
        DateTimeOffset? closedAt = null,
        string? vrchat = null,
        string? messageId = null)
    {
        await using var context = Database.NewContext();

        var report = new MemberReport
        {
            ReporterDiscordId = reporter,
            ReporterName = "reporter name",
            ReportedDiscordId = reported,
            ReportedName = "reported name",
            ReportedVRChatUserId = vrchat,
            Text = "they were rude",
            MessageId = messageId,
            MessageChannelId = messageId is null ? null : "55",
            MessageChannelName = messageId is null ? null : "general",
            MessageSentAt = messageId is null ? null : Clock.UtcNow,
            MessageText = messageId is null ? null : "the quoted words",
            MessageAttachments = messageId is null ? null : ["cat.png"],
            MessageUrl = messageId is null ? null : "https://discord.com/channels/1/55/" + messageId,
            State = state,
            CreatedAt = Clock.UtcNow,
            ClosedAt = closedAt,
            ClosedByUserId = closedAt is null ? null : Guid.NewGuid(),
            ClosedByUsername = closedAt is null ? null : "alice",
            CloseNote = closedAt is null ? null : "dealt with",
        };

        context.MemberReports.Add(report);
        await context.SaveChangesAsync(Ct);
        return report;
    }

    private async Task SetRetentionAsync(int days)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.MemberReportRetentionDays = days;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<MemberReport> ReadAsync(Guid id)
    {
        await using var context = Database.NewContext();
        return await context.MemberReports.AsNoTracking().FirstAsync(r => r.Id == id, Ct);
    }

    // ── What is kept, and what the facts leave out ───────────────────────────────────────────

    [Fact]
    public async Task AReport_KeepsWhoAndWhatAndTheMessageCopy_AndTheLinkedVRChatAccount()
    {
        await using (var setup = Database.NewContext())
        {
            setup.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = Reported,
                DiscordUsername = "reported",
                VRChatUserId = "usr_reported",
                LinkedAt = Start,
            });
            await setup.SaveChangesAsync(Ct);
        }

        await using var context = Database.NewContext();
        var outcome = await NewReports(context).OpenAsync(
            new NewMemberReport(
                Reporter,
                "Reporter",
                Reported,
                "Reported",
                "  They were rude.  ",
                new ReportedMessage("77", "55", "general", Start.AddMinutes(-3), "the words", ["a.png", "b.mp4"], "https://discord.com/channels/1/55/77")),
            Ct);

        Assert.Equal(MemberReportResult.Sent, outcome.Result);

        var saved = await ReadAsync(outcome.Id!.Value);
        Assert.Equal(Reporter, saved.ReporterDiscordId);
        Assert.Equal("Reporter", saved.ReporterName);
        Assert.Equal(Reported, saved.ReportedDiscordId);
        Assert.Equal("usr_reported", saved.ReportedVRChatUserId);
        Assert.Equal("They were rude.", saved.Text);
        Assert.Equal("77", saved.MessageId);
        Assert.Equal("55", saved.MessageChannelId);
        Assert.Equal("general", saved.MessageChannelName);
        Assert.Equal("the words", saved.MessageText);
        Assert.Equal(["a.png", "b.mp4"], saved.MessageAttachments);
        Assert.Equal("https://discord.com/channels/1/55/77", saved.MessageUrl);
        Assert.Equal(MemberReportStates.Open, saved.State);
    }

    [Fact]
    public async Task TheOpenFact_HasTheReportedAsSubject_AndNoReporterAndNoWords()
    {
        await using var context = Database.NewContext();
        var outcome = await NewReports(context).OpenAsync(
            new NewMemberReport(
                Reporter,
                "Reporter Name",
                Reported,
                "Reported",
                "secret words about them",
                new ReportedMessage("77", "55", "general", Start, "quoted message words", [], "https://discord.com/channels/1/55/77")),
            Ct);

        await using var read = Database.NewContext();
        var fact = await read.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.MemberReportOpened, Ct);

        Assert.Equal(Reported, fact.SubjectId);
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Null(fact.ActorId);
        Assert.Contains(outcome.Id!.Value.ToString(), fact.Data, StringComparison.Ordinal);
        Assert.Contains("55", fact.Data, StringComparison.Ordinal);

        foreach (var leak in new[] { Reporter, "Reporter Name", "secret words", "quoted message", "reporter" })
            Assert.DoesNotContain(leak, fact.Data, StringComparison.OrdinalIgnoreCase);

        // And no command fact: its subject would be the reporter.
        Assert.Empty(await read.Events.AsNoTracking().Where(e => e.Type == FactType.DiscordCommandRun).ToListAsync(Ct));
    }

    [Fact]
    public async Task Closing_NeedsANote_WritesAFactNamingTheActor_AndLeavesTheNoteAndTheReporterOut()
    {
        var report = await AddAsync(Reporter, Reported);
        var closer = Guid.NewGuid();

        await using (var context = Database.NewContext())
        {
            var reports = NewReports(context);

            Assert.Equal(MemberReportCloseResult.NoNote, await reports.CloseAsync(report.Id, "   ", closer, "alice", Ct));
            Assert.Equal(MemberReportCloseResult.NoteTooLong, await reports.CloseAsync(report.Id, new string('x', MemberReport.MaxCloseNoteLength + 1), closer, "alice", Ct));
            Assert.Equal(MemberReportCloseResult.NotFound, await reports.CloseAsync(Guid.NewGuid(), "x", closer, "alice", Ct));
            Assert.Equal(MemberReportCloseResult.Closed, await reports.CloseAsync(report.Id, "Spoke to them about Reporter Name.", closer, "alice", Ct));
        }

        await using (var again = Database.NewContext())
            Assert.Equal(MemberReportCloseResult.AlreadyClosed, await NewReports(again).CloseAsync(report.Id, "twice", closer, "alice", Ct));

        var closed = await ReadAsync(report.Id);
        Assert.Equal(MemberReportStates.Closed, closed.State);
        Assert.Equal(closer, closed.ClosedByUserId);
        Assert.Equal("Spoke to them about Reporter Name.", closed.CloseNote);

        await using var read = Database.NewContext();
        var fact = await read.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.MemberReportClosed, Ct);

        Assert.Equal(Reported, fact.SubjectId);
        Assert.Equal(closer.ToString(), fact.ActorId);
        Assert.Contains(report.Id.ToString(), fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("Spoke to them", fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(Reporter, fact.Data, StringComparison.Ordinal);
    }

    // ── The same message twice, the same person twice ────────────────────────────────────────

    [Fact]
    public async Task TheSameMessageTwiceFromTheSamePerson_IsRefused_AndAnotherPersonMayReportIt()
    {
        var message = new ReportedMessage("77", "55", "general", Start, "words", [], "https://discord.com/channels/1/55/77");

        await using var context = Database.NewContext();
        var reports = NewReports(context);

        Assert.Equal(MemberReportResult.Sent, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "one", message), Ct)).Result);
        Assert.Equal(MemberReportResult.AlreadyReportedMessage, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "two", message), Ct)).Result);
        Assert.Equal(MemberReportResult.Sent, (await reports.OpenAsync(new NewMemberReport(Other, "o", Reported, null, "three", message), Ct)).Result);

        await using var read = Database.NewContext();
        Assert.Equal(2, await read.MemberReports.CountAsync(Ct));
    }

    [Fact]
    public async Task ASecondOpenReportOnTheSamePerson_IsRefused_UntilTheFirstIsClosed()
    {
        await using var context = Database.NewContext();
        var reports = NewReports(context);

        var first = await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "one"), Ct);
        Assert.Equal(MemberReportResult.AlreadyOpen, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "two"), Ct)).Result);

        Assert.Equal(MemberReportCloseResult.Closed, await reports.CloseAsync(first.Id!.Value, "done", Guid.NewGuid(), "alice", Ct));
        Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(MemberReportResult.Sent, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "three"), Ct)).Result);
    }

    [Fact]
    public async Task TheDatabaseHoldsBothPromises_SoTwoPressesAtOnceCannotMakeTwo()
    {
        await AddAsync(Reporter, Reported, messageId: "77");

        await using var sameMessage = Database.NewContext();
        sameMessage.MemberReports.Add(new MemberReport
        {
            ReporterDiscordId = Reporter, ReporterName = "r", ReportedDiscordId = Other, MessageId = "77", Text = "x", CreatedAt = Start,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => sameMessage.SaveChangesAsync(Ct));

        await using var sameOpen = Database.NewContext();
        sameOpen.MemberReports.Add(new MemberReport
        {
            ReporterDiscordId = Reporter, ReporterName = "r", ReportedDiscordId = Reported, Text = "x", CreatedAt = Start,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => sameOpen.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task AReportAboutYourself_IsRefused_AndNothingIsKept()
    {
        await using var context = Database.NewContext();
        var outcome = await NewReports(context).OpenAsync(new NewMemberReport(Reporter, "r", Reporter, null, "me"), Ct);

        Assert.Equal(MemberReportResult.Yourself, outcome.Result);
        Assert.Equal(0, await context.MemberReports.CountAsync(Ct));
    }

    [Theory]
    [InlineData("", MemberReportResult.NothingWritten)]
    [InlineData("   ", MemberReportResult.NothingWritten)]
    public async Task NothingWritten_IsRefused(string text, MemberReportResult expected)
    {
        await using var context = Database.NewContext();
        Assert.Equal(expected, (await NewReports(context).OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, text), Ct)).Result);
    }

    [Fact]
    public async Task TheLongestAllowedText_IsKept_AndOneMoreIsRefused()
    {
        await using var context = Database.NewContext();
        var reports = NewReports(context);

        Assert.Equal(MemberReportResult.TooLong, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, new string('x', 1001)), Ct)).Result);
        Assert.Equal(MemberReportResult.Sent, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, new string('x', 1000)), Ct)).Result);
    }

    // ── The limits: 3 in 10 minutes and 10 a day, counted in the database ────────────────────

    [Fact]
    public async Task ThreeInTenMinutes_AFourthIsRefused_AndAllowedOnceTheFirstIsTenMinutesOld()
    {
        string Person(int n) => "4000000000000000" + n.ToString("D2");

        for (var n = 0; n < 3; n++)
        {
            // A new object each time: nothing is remembered in memory between them.
            await using var context = Database.NewContext();
            Assert.Equal(MemberReportResult.Sent, (await NewReports(context).OpenAsync(new NewMemberReport(Reporter, "r", Person(n), null, "x"), Ct)).Result);
            Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await using (var fourth = Database.NewContext())
            Assert.Equal(MemberReportResult.TooMany, (await NewReports(fourth).OpenAsync(new NewMemberReport(Reporter, "r", Person(3), null, "x"), Ct)).Result);

        // Somebody else is not held up by it: nothing is limited server-wide.
        await using (var other = Database.NewContext())
            Assert.Equal(MemberReportResult.Sent, (await NewReports(other).OpenAsync(new NewMemberReport(Other, "o", Person(3), null, "x"), Ct)).Result);

        // Eight more minutes puts the first one ten minutes back.
        Clock.Advance(TimeSpan.FromMinutes(8));

        await using var later = Database.NewContext();
        Assert.Equal(MemberReportResult.Sent, (await NewReports(later).OpenAsync(new NewMemberReport(Reporter, "r", Person(3), null, "x"), Ct)).Result);
    }

    [Fact]
    public async Task TenInADay_AnEleventhIsRefused_AndAllowedOnceTheFirstIsADayOld()
    {
        string Person(int n) => "5000000000000000" + n.ToString("D2");

        // Eleven minutes apart, so the ten-minute limit never gets in the way.
        for (var n = 0; n < 10; n++)
        {
            await using var context = Database.NewContext();
            Assert.Equal(MemberReportResult.Sent, (await NewReports(context).OpenAsync(new NewMemberReport(Reporter, "r", Person(n), null, "x"), Ct)).Result);
            Clock.Advance(TimeSpan.FromMinutes(11));
        }

        await using (var eleventh = Database.NewContext())
            Assert.Equal(MemberReportResult.TooMany, (await NewReports(eleventh).OpenAsync(new NewMemberReport(Reporter, "r", Person(10), null, "x"), Ct)).Result);

        // The first one was made 110 minutes ago; a day after it, there is room again.
        Clock.Advance(TimeSpan.FromHours(22));

        await using var later = Database.NewContext();
        Assert.Equal(MemberReportResult.Sent, (await NewReports(later).OpenAsync(new NewMemberReport(Reporter, "r", Person(10), null, "x"), Ct)).Result);
    }

    /// <summary>
    /// The limits are hard caps: reports made at the very same moment are counted one reporter at a
    /// time, so six at once keep exactly three, and two with one place left keep exactly one.
    /// </summary>
    [Fact]
    public async Task ReportsMadeAtTheSameMoment_KeepExactlyTheLimit()
    {
        string Person(int n) => "6000000000000000" + n.ToString("D2");

        async Task<MemberReportResult> OpenAsync(string reporter, string person)
        {
            await using var context = Database.NewContext();
            return (await NewReports(context).OpenAsync(new NewMemberReport(reporter, "r", person, null, "x"), Ct)).Result;
        }

        var six = await Task.WhenAll(Enumerable.Range(0, 6).Select(n => OpenAsync(Reporter, Person(n))));

        Assert.Equal(3, six.Count(r => r == MemberReportResult.Sent));
        Assert.Equal(3, six.Count(r => r == MemberReportResult.TooMany));

        await using (var read = Database.NewContext())
            Assert.Equal(3, await read.MemberReports.CountAsync(r => r.ReporterDiscordId == Reporter, Ct));

        // One place left in the ten minutes: two at once keep one.
        await using (var clean = Database.NewContext())
            await clean.MemberReports.Where(r => r.ReporterDiscordId == Other).ExecuteDeleteAsync(Ct);

        Assert.Equal(MemberReportResult.Sent, await OpenAsync(Other, Person(10)));
        Assert.Equal(MemberReportResult.Sent, await OpenAsync(Other, Person(11)));

        var two = await Task.WhenAll(OpenAsync(Other, Person(12)), OpenAsync(Other, Person(13)));

        Assert.Equal(1, two.Count(r => r == MemberReportResult.Sent));
        Assert.Equal(1, two.Count(r => r == MemberReportResult.TooMany));

        await using var after = Database.NewContext();
        Assert.Equal(3, await after.MemberReports.CountAsync(r => r.ReporterDiscordId == Other, Ct));
    }

    /// <summary>Two presses at once on the same report keep one report, and no extra fact.</summary>
    [Fact]
    public async Task TwoPressesAtOnceOnTheSameReport_KeepOne_AndTheSecondIsTold()
    {
        async Task<MemberReportResult> OpenAsync()
        {
            await using var context = Database.NewContext();
            return (await NewReports(context).OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "x"), Ct)).Result;
        }

        var results = await Task.WhenAll(OpenAsync(), OpenAsync());

        Assert.Equal(1, results.Count(r => r == MemberReportResult.Sent));
        Assert.Equal(1, results.Count(r => r == MemberReportResult.AlreadyOpen));

        await using var read = Database.NewContext();
        Assert.Equal(1, await read.MemberReports.CountAsync(Ct));
        Assert.Single(await read.Events.AsNoTracking().Where(e => e.Type == FactType.MemberReportOpened).ToListAsync(Ct));
    }

    /// <summary>The close is one statement that only works on an open report, so two at once cannot both win.</summary>
    [Fact]
    public async Task TwoClosesAtOnce_OneWins_TheOtherIsToldAlreadyClosed_AndOnlyOneFactIsWritten()
    {
        var report = await AddAsync(Reporter, Reported);

        async Task<MemberReportCloseResult> CloseAsync(string who)
        {
            await using var context = Database.NewContext();
            return await NewReports(context).CloseAsync(report.Id, "note from " + who, Guid.NewGuid(), who, Ct);
        }

        var results = await Task.WhenAll(CloseAsync("alice"), CloseAsync("bob"));

        Assert.Equal(1, results.Count(r => r == MemberReportCloseResult.Closed));
        Assert.Equal(1, results.Count(r => r == MemberReportCloseResult.AlreadyClosed));

        await using var read = Database.NewContext();
        Assert.Single(await read.Events.AsNoTracking().Where(e => e.Type == FactType.MemberReportClosed).ToListAsync(Ct));

        var closed = await ReadAsync(report.Id);
        Assert.Equal(MemberReportStates.Closed, closed.State);
        Assert.StartsWith("note from ", closed.CloseNote, StringComparison.Ordinal);
        Assert.Equal(closed.CloseNote!["note from ".Length..], closed.ClosedByUsername);
    }

    [Fact]
    public async Task ARefusedReport_DoesNotCountTowardsTheLimits()
    {
        await using var context = Database.NewContext();
        var reports = NewReports(context);

        Assert.Equal(MemberReportResult.Sent, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "x"), Ct)).Result);

        for (var n = 0; n < 5; n++)
            Assert.Equal(MemberReportResult.AlreadyOpen, (await reports.OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "x"), Ct)).Result);

        Assert.Equal(1, await context.MemberReports.CountAsync(Ct));
        Assert.False(await reports.IsOverTheLimitAsync(Reporter, Ct));
    }

    // ── Retention: the words go, the record stays ────────────────────────────────────────────

    [Fact]
    public async Task Retention_RemovesTheTextAndTheMessageCopyOfAnOldClosedReport_AndKeepsTheRecord()
    {
        await SetRetentionAsync(365);
        var closedAt = Start.AddDays(-366);
        var old = await AddAsync(Reporter, Reported, MemberReportStates.Closed, closedAt, "usr_x", "77");

        await using (var context = Database.NewContext())
            Assert.Equal(1, await MemberReports.RemoveOldTextAsync(context, Start, Ct));

        var kept = await ReadAsync(old.Id);
        Assert.Null(kept.Text);
        Assert.Null(kept.MessageId);
        Assert.Null(kept.MessageChannelId);
        Assert.Null(kept.MessageChannelName);
        Assert.Null(kept.MessageSentAt);
        Assert.Null(kept.MessageText);
        Assert.Null(kept.MessageAttachments);
        Assert.Null(kept.MessageUrl);
        Assert.Equal(Start, kept.TextRemovedAt);

        // The bare record stays: who, about whom, when, and how it was closed.
        Assert.Equal(Reporter, kept.ReporterDiscordId);
        Assert.Equal(Reported, kept.ReportedDiscordId);
        Assert.Equal("usr_x", kept.ReportedVRChatUserId);
        Assert.Equal(old.CreatedAt, kept.CreatedAt);
        Assert.Equal(MemberReportStates.Closed, kept.State);
        Assert.Equal(closedAt, kept.ClosedAt);
        Assert.Equal("alice", kept.ClosedByUsername);

        // Once is enough.
        await using var again = Database.NewContext();
        Assert.Equal(0, await MemberReports.RemoveOldTextAsync(again, Start.AddDays(10), Ct));
    }

    [Fact]
    public async Task Retention_LeavesARecentlyClosedReport_AnOpenOne_AndEveryReportWhenSetToKeepForever()
    {
        await SetRetentionAsync(365);
        var recent = await AddAsync(Reporter, Reported, MemberReportStates.Closed, Start.AddDays(-364), messageId: "1");
        var open = await AddAsync(Other, Reported);

        await using (var context = Database.NewContext())
            Assert.Equal(0, await MemberReports.RemoveOldTextAsync(context, Start, Ct));

        Assert.NotNull((await ReadAsync(recent.Id)).Text);
        Assert.NotNull((await ReadAsync(open.Id)).Text);

        // Five years on, the closed one has aged out and the open one has not: an open report is
        // never touched, however old, because nobody has answered it yet.
        await using (var context = Database.NewContext())
            Assert.Equal(1, await MemberReports.RemoveOldTextAsync(context, Start.AddYears(5), Ct));

        Assert.Null((await ReadAsync(recent.Id)).Text);
        Assert.NotNull((await ReadAsync(open.Id)).Text);

        // Zero keeps everything.
        var old = await AddAsync("600000000000000006", Reported, MemberReportStates.Closed, Start.AddYears(-9));
        await SetRetentionAsync(0);

        await using var forever = Database.NewContext();
        Assert.Equal(0, await MemberReports.RemoveOldTextAsync(forever, Start, Ct));
        Assert.NotNull((await ReadAsync(old.Id)).Text);
    }

    [Fact]
    public async Task TheRetentionJob_DoesIt_AndCountsIt()
    {
        await SetRetentionAsync(30);
        var old = await AddAsync(Reporter, Reported, MemberReportStates.Closed, Start.AddDays(-31));

        await using var context = Database.NewContext();
        var pruner = new RetentionPruner(context, Clock, new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock));
        var result = await pruner.PruneAsync(Ct);

        Assert.Equal(1, result.MemberReportsReduced);
        Assert.Null((await ReadAsync(old.Id)).Text);
    }

    // ── A purge removes reports by the person and about them ─────────────────────────────────

    [Fact]
    public async Task APurgeOfADiscordAccount_RemovesReportsByThemAndAboutThem_AndNoOthers()
    {
        await AddAsync(Reporter, Other);
        await AddAsync(Other, Reporter);
        var bystander = await AddAsync("700000000000000007", "800000000000000008");

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, Reporter, ct: Ct);
            Assert.Equal(2, result.MemberReportsDeleted);
        }

        await using var read = Database.NewContext();
        Assert.Equal(bystander.Id, Assert.Single(await read.MemberReports.AsNoTracking().ToListAsync(Ct)).Id);

        // The record of the purge counts them and names nobody.
        var record = await read.Events.AsNoTracking().Where(e => e.Type == FactType.UserPurged).OrderByDescending(e => e.Id).FirstAsync(Ct);
        Assert.Contains("\"memberReports\": 2", record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(Reporter, record.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APurgeOfAVRChatAccount_RemovesReportsAboutIt_AndByAndAboutTheDiscordAccountLinkedToIt()
    {
        await using (var setup = Database.NewContext())
        {
            setup.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = Reporter,
                DiscordUsername = "member",
                VRChatUserId = "usr_purge_me",
                LinkedAt = Start,
            });
            await setup.SaveChangesAsync(Ct);
        }

        await AddAsync(Reporter, Other);
        await AddAsync(Other, Reporter);
        await AddAsync("900000000000000009", "910000000000000009", vrchat: "usr_purge_me");
        var bystander = await AddAsync("920000000000000009", "930000000000000009", vrchat: "usr_someone_else");

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, "usr_purge_me", ct: Ct);
            Assert.Equal(3, result.MemberReportsDeleted);
        }

        await using var read = Database.NewContext();
        Assert.Equal(bystander.Id, Assert.Single(await read.MemberReports.AsNoTracking().ToListAsync(Ct)).Id);
    }

    [Fact]
    public async Task APurgeOfTheReportedPerson_AlsoErasesTheReportFactsAboutThem()
    {
        await using (var context = Database.NewContext())
            await NewReports(context).OpenAsync(new NewMemberReport(Reporter, "r", Reported, null, "x"), Ct);

        await using (var context = Database.NewContext())
            await NewPurger(context).PurgeAsync(FactPlatform.Discord, Reported, ct: Ct);

        await using var read = Database.NewContext();
        Assert.Empty(await read.Events.AsNoTracking().Where(e => e.Type == FactType.MemberReportOpened).ToListAsync(Ct));
        Assert.Equal(0, await read.MemberReports.CountAsync(Ct));
    }

    // ── Who may see a report about a staff account ───────────────────────────────────────────

    [Fact]
    public async Task AReportAboutAStaffAccount_IsLeftOutForSomebodyWithoutReviewTickets_AndShownToAnAdministratorOrReviewer()
    {
        await using (var setup = Database.NewContext())
        {
            var staff = await TestAccounts.CreateAsync(setup, "staffer", TestAccounts.Password, ModbotPermissions.ViewMembers, linked: true, Ct);
            staff.DiscordUserId = Other;
            staff.DiscordVerifiedAt = Start;
            await setup.SaveChangesAsync(Ct);
        }

        var aboutStaff = await AddAsync(Reporter, Other);
        var ordinary = await AddAsync(Reporter, Reported);

        await using var context = Database.NewContext();

        var plain = await (await MemberReportAccess.VisibleAsync(context, ModbotPermissions.ViewReports, Start, Ct)).Select(r => r.Id).ToListAsync(Ct);
        Assert.Equal([ordinary.Id], plain);

        var reviewer = await (await MemberReportAccess.VisibleAsync(context, ModbotPermissions.ViewReports | ModbotPermissions.ReviewTickets, Start, Ct)).Select(r => r.Id).ToListAsync(Ct);
        Assert.Equal(2, reviewer.Count);
        Assert.Contains(aboutStaff.Id, reviewer);

        var admin = await (await MemberReportAccess.VisibleAsync(context, ModbotPermissions.Administrator, Start, Ct)).Select(r => r.Id).ToListAsync(Ct);
        Assert.Equal(2, admin.Count);
    }
}
