using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reports;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Notifications;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/report</c> and the "Report to mods" message menu (Discord commands design §3.4, step 9): off
/// until the operator turns them on, open to every member, private, never naming the reporter
/// anywhere in Discord, in a fact or in a notification, and leaving no command fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ReportCommandTests
{
    private const string Guild = "111111111111111111";
    private const string Reporter = "100000000000000001";
    private const string ReporterName = "Quillfeather";
    private const string Reported = "200000000000000002";
    private const string Words = "they keep shouting slurs in voice";

    private readonly PostgresFixture _db;

    public ReportCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, bool slash = true, bool menu = true)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.SwitchCommand(DiscordCommands.Report, slash);
            s.SwitchCommand(DiscordCommands.ReportMenu, menu);
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Slash(string caller, string member, string what, string name = ReporterName)
        => new(
            caller,
            name,
            DiscordCommands.Report,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DiscordCommands.MemberOption] = member,
                [DiscordCommands.ReportWhatOption] = what,
            },
            (_, _) => Task.CompletedTask);

    private static DiscordTargetMessage Message(string id = "777", string author = Reported, string text = "the quoted message", IReadOnlyList<string>? files = null)
        => new(id, "555", "general", author, "Reported", false, text, new DateTimeOffset(2026, 9, 13, 11, 50, 0, TimeSpan.Zero), "https://discord.com/channels/1/555/" + id, files ?? ["cat.png"]);

    /// <summary>The menu call, with the forms it shows collected.</summary>
    private static (DiscordCommandCall Call, List<DiscordForm> Forms) Menu(string caller, DiscordTargetMessage? message, string name = ReporterName)
    {
        var forms = new List<DiscordForm>();
        var call = new DiscordCommandCall(
            caller,
            name,
            DiscordCommands.ReportMenu,
            new Dictionary<string, string>(),
            (_, _) => Task.CompletedTask,
            (form, _) =>
            {
                forms.Add(form);
                return Task.CompletedTask;
            })
        {
            Kind = DiscordCommandKind.Message,
            TargetMessage = message,
        };

        return (call, forms);
    }

    private static DiscordFormSubmit Submit(string caller, DiscordForm form, string text, string name = ReporterName)
        => new(
            caller,
            name,
            form.Id,
            new Dictionary<string, IReadOnlyList<string>> { [ReportCommand.WhatField] = [text] },
            (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply?> SlashAsync(TestServices services, DiscordCommandCall call, FakeGateway? gateway = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(call, Ct, gateway);
    }

    private static async Task<DiscordReply?> MenuAsync(TestServices services, DiscordCommandCall call, FakeGateway? gateway = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<ReportCommand>().HandleMenuAsync(call, gateway, Ct);
    }

    private static async Task<DiscordReply> SendFormAsync(TestServices services, DiscordFormSubmit submit, FakeGateway? gateway = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<ReportCommand>().HandleFormAsync(submit, gateway, Ct);
    }

    private static async Task<List<MemberReport>> ReportsAsync(TestServices services)
    {
        await using var context = services.Database.NewContext();
        return await context.MemberReports.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(Ct);
    }

    private static async Task<int> CommandFactsAsync(TestServices services)
        => (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct)).Count;

    // ── Off by default ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothAreOffUntilTheOperatorTurnsThemOn()
    {
        Assert.False(DiscordCommandSwitches.IsOn(DiscordCommandSwitches.Empty, DiscordCommands.Report));
        Assert.False(DiscordCommandSwitches.IsOn(DiscordCommandSwitches.Empty, DiscordCommands.ReportMenu));
        Assert.DoesNotContain(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name is DiscordCommands.Report or DiscordCommands.ReportMenu);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.With(null, DiscordCommands.Report, true)), c => c.Name == DiscordCommands.Report);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.With(null, DiscordCommands.ReportMenu, true)), c => c.Name == DiscordCommands.ReportMenu);
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Report));
        Assert.Contains(DiscordCommands.All, c => c.Name == DiscordCommands.ReportMenu && c.Kind == DiscordCommandKind.Message && !c.StaffOnly);
    }

    [Fact]
    public async Task WhileOff_BothSayTheyAreOff_KeepNothing_AndLeaveNoFactOfAnyKind()
    {
        await using var services = await SetUpAsync(_db, slash: false, menu: false);

        var reply = await SlashAsync(services, Slash(Reporter, Reported, Words));
        Assert.Equal("/report is turned off on this server.", reply?.Text);

        var (call, forms) = Menu(Reporter, Message());
        var menuReply = await MenuAsync(services, call);
        Assert.Equal("\"Report to mods\" is turned off on this server.", menuReply?.Text);
        Assert.Empty(forms);

        Assert.Empty(await ReportsAsync(services));
        Assert.Equal(0, await CommandFactsAsync(services));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.MemberReportOpened, Ct));
    }

    // ── /report ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASlashReport_IsKept_AnsweredInPrivateWords_AndWritesNoCommandFact()
    {
        await using var services = await SetUpAsync(_db);

        var reply = await SlashAsync(services, Slash(Reporter, Reported, Words));

        Assert.Equal(ReportCommand.SentMessage, reply?.Text);
        Assert.Empty(reply!.Embeds);
        Assert.DoesNotContain(ReporterName, reply.Text, StringComparison.Ordinal);

        var report = Assert.Single(await ReportsAsync(services));
        Assert.Equal(Reporter, report.ReporterDiscordId);
        Assert.Equal(ReporterName, report.ReporterName);
        Assert.Equal(Reported, report.ReportedDiscordId);
        Assert.Equal(Words, report.Text);
        Assert.Null(report.MessageId);

        Assert.Equal(0, await CommandFactsAsync(services));
    }

    [Fact]
    public async Task TheOpenFact_NamesNoReporterAndCarriesNoWords()
    {
        await using var services = await SetUpAsync(_db);

        var (call, forms) = Menu(Reporter, Message());
        Assert.Null(await MenuAsync(services, call));
        await SendFormAsync(services, Submit(Reporter, forms.Single(), Words));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.MemberReportOpened, Ct));
        Assert.Equal(Reported, fact.SubjectId);

        foreach (var leak in new[] { Reporter, ReporterName, "slurs", "quoted message", "cat.png" })
            Assert.DoesNotContain(leak, fact.Data, StringComparison.Ordinal);

        Assert.Equal(0, await CommandFactsAsync(services));
    }

    [Fact]
    public async Task YouCannotReportYourself_OrTheBot()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        Assert.Equal(ReportCommand.YourselfMessage, (await SlashAsync(services, Slash(Reporter, Reporter, Words), gateway))?.Text);
        Assert.Equal(ReportCommand.BotMessage, (await SlashAsync(services, Slash(Reporter, gateway.BotUserId!, Words), gateway))?.Text);

        Assert.Empty(await ReportsAsync(services));
        Assert.Equal(0, await CommandFactsAsync(services));
    }

    [Fact]
    public async Task ASecondOpenReportOnTheSamePerson_GetsTheMessage_ButAnotherPersonCanStillReport()
    {
        await using var services = await SetUpAsync(_db);

        Assert.Equal(ReportCommand.SentMessage, (await SlashAsync(services, Slash(Reporter, Reported, Words)))?.Text);
        Assert.Equal(ReportCommand.AlreadyOpenMessage, (await SlashAsync(services, Slash(Reporter, Reported, "more words")))?.Text);
        Assert.Equal(ReportCommand.SentMessage, (await SlashAsync(services, Slash("300000000000000003", Reported, Words)))?.Text);

        Assert.Equal(2, (await ReportsAsync(services)).Count);
    }

    [Fact]
    public async Task ThreeInTenMinutes_AndTenInADay_ThroughTheCommand_AndTheDatabaseKeepsCountThroughARestart()
    {
        await using var services = await SetUpAsync(_db);
        string Person(int n) => "4000000000000000" + n.ToString("D2");

        for (var n = 0; n < 3; n++)
            Assert.Equal(ReportCommand.SentMessage, (await SlashAsync(services, Slash(Reporter, Person(n), Words)))?.Text);

        Assert.Equal(ReportCommand.TooManyMessage, (await SlashAsync(services, Slash(Reporter, Person(3), Words)))?.Text);

        // A restart: a new process has no memory of anything, and the answer is the same, because
        // the count is read from the reporter's own rows. A service built from nothing but the
        // database stands in for it.
        await using (var context = services.Database.NewContext())
        {
            var restarted = new MemberReports(
                context,
                new FactWriter(context, services.Clock),
                new EventPartitionMaintainer(context, services.Clock),
                services.Clock);

            Assert.True(await restarted.IsOverTheLimitAsync(Reporter, Ct));
            Assert.False(await restarted.IsOverTheLimitAsync("300000000000000003", Ct));
        }

        // Nothing is limited server-wide.
        Assert.Equal(ReportCommand.SentMessage, (await SlashAsync(services, Slash("300000000000000003", Person(3), Words)))?.Text);

        // Ten in a day: eleven minutes apart, so only the daily limit is in the way.
        services.Clock.Advance(TimeSpan.FromMinutes(11));
        for (var n = 3; n < 10; n++)
        {
            Assert.Equal(ReportCommand.SentMessage, (await SlashAsync(services, Slash(Reporter, Person(n), Words)))?.Text);
            services.Clock.Advance(TimeSpan.FromMinutes(11));
        }

        Assert.Equal(ReportCommand.TooManyMessage, (await SlashAsync(services, Slash(Reporter, Person(10), Words)))?.Text);
    }

    // ── The message menu ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMenu_OpensAFormAskingWhatIsWrong_AndTheFormKeepsTheReportWithAMessageCopy()
    {
        await using var services = await SetUpAsync(_db);

        var (call, forms) = Menu(Reporter, Message(files: ["cat.png", "dog.mp4"]));
        Assert.Null(await MenuAsync(services, call));

        var form = Assert.Single(forms);
        Assert.Equal("Report to mods", form.Title);
        Assert.True(form.Title.Length <= 45);
        Assert.True(form.Id.Length <= 100);
        Assert.True(ReportCommand.IsReportForm(form.Id));
        var field = Assert.Single(form.Fields);
        Assert.Equal("What's wrong?", field.Label);
        Assert.True(field.Required);
        Assert.Equal(1000, field.MaxLength);

        var reply = await SendFormAsync(services, Submit(Reporter, form, Words));
        Assert.Equal(ReportCommand.SentMessage, reply.Text);

        var report = Assert.Single(await ReportsAsync(services));
        Assert.Equal(Reported, report.ReportedDiscordId);
        Assert.Equal(Words, report.Text);
        Assert.Equal("777", report.MessageId);
        Assert.Equal("555", report.MessageChannelId);
        Assert.Equal("general", report.MessageChannelName);
        Assert.Equal("the quoted message", report.MessageText);
        Assert.Equal(["cat.png", "dog.mp4"], report.MessageAttachments);
        Assert.Equal("https://discord.com/channels/1/555/777", report.MessageUrl);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 11, 50, 0, TimeSpan.Zero), report.MessageSentAt);
    }

    [Fact]
    public async Task TheSameMessageTwice_IsRefusedBeforeTheFormIsShown()
    {
        await using var services = await SetUpAsync(_db);

        var (first, firstForms) = Menu(Reporter, Message());
        await MenuAsync(services, first);
        await SendFormAsync(services, Submit(Reporter, firstForms.Single(), Words));

        var (again, againForms) = Menu(Reporter, Message());
        var reply = await MenuAsync(services, again);

        Assert.Equal(ReportCommand.SameMessageMessage, reply?.Text);
        Assert.Empty(againForms);
        Assert.Single(await ReportsAsync(services));
    }

    [Fact]
    public async Task TwoFormsForTheSameMessageSentAtOnce_KeepOneReport()
    {
        await using var services = await SetUpAsync(_db);

        var (call, forms) = Menu(Reporter, Message());
        await MenuAsync(services, call);
        var form = forms.Single();

        var first = await SendFormAsync(services, Submit(Reporter, form, Words));
        var second = await SendFormAsync(services, Submit(Reporter, form, Words));

        Assert.Equal(ReportCommand.SentMessage, first.Text);
        Assert.NotEqual(ReportCommand.SentMessage, second.Text);
        Assert.Single(await ReportsAsync(services));
    }

    [Fact]
    public async Task TheMenu_RefusesYourOwnMessage_TheBotsMessage_AndASecondOpenReportOnThePerson()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var (own, ownForms) = Menu(Reporter, Message(author: Reporter));
        Assert.Equal(ReportCommand.YourselfMessage, (await MenuAsync(services, own, gateway))?.Text);

        var (bot, botForms) = Menu(Reporter, Message(author: gateway.BotUserId!));
        Assert.Equal(ReportCommand.BotMessage, (await MenuAsync(services, bot, gateway))?.Text);

        Assert.Empty(ownForms);
        Assert.Empty(botForms);

        // A slash report is open on the person; a message from them is a second one.
        await SlashAsync(services, Slash(Reporter, Reported, Words));
        var (second, secondForms) = Menu(Reporter, Message(id: "888"));
        Assert.Equal(ReportCommand.AlreadyOpenMessage, (await MenuAsync(services, second, gateway))?.Text);
        Assert.Empty(secondForms);

        Assert.Single(await ReportsAsync(services));
        Assert.Equal(0, await CommandFactsAsync(services));
    }

    [Fact]
    public async Task AFormFromSomebodyElse_OrOneThatHasRunOut_KeepsNothing()
    {
        await using var services = await SetUpAsync(_db);

        var (call, forms) = Menu(Reporter, Message());
        await MenuAsync(services, call);
        var form = forms.Single();

        var stolen = await SendFormAsync(services, Submit("300000000000000003", form, Words));
        Assert.Equal(ReportCommand.NotYoursMessage, stolen.Text);

        var unknown = await SendFormAsync(services, Submit(Reporter, new DiscordForm("x", ReportCommand.FormPrefix + "nothing", []), Words));
        Assert.Equal(ReportCommand.RunOutMessage, unknown.Text);

        services.Clock.Advance(PendingReports.Lifetime + TimeSpan.FromSeconds(1));
        var late = await SendFormAsync(services, Submit(Reporter, form, Words));
        Assert.Equal(ReportCommand.RunOutMessage, late.Text);

        Assert.Empty(await ReportsAsync(services));
    }

    // ── The notification ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMods_AreToldWithNoNamesAndNoWords_AndOnlyThoseWhoHoldSeeReports()
    {
        await using var services = await SetUpAsync(_db);
        var seer = await services.LinkedAccountAsync("900000000000000001", ModbotPermissions.ViewReports, ct: Ct);
        var blind = await services.LinkedAccountAsync("900000000000000002", ModbotPermissions.ViewMembers, ct: Ct);

        await SlashAsync(services, Slash(Reporter, Reported, Words));

        await using var context = services.Database.NewContext();
        var notification = Assert.Single(await context.Notifications.AsNoTracking().Where(n => n.Kind == NotificationKinds.MemberReportNew).ToListAsync(Ct));

        Assert.Equal("Modbot: new report", notification.Title);
        Assert.Equal("Somebody reported a member.", notification.Body);
        Assert.Equal("/reports", notification.Link);

        foreach (var text in new[] { notification.Title, notification.Body, notification.Link, notification.SameAs })
        {
            Assert.DoesNotContain(ReporterName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Reporter, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Reported, text, StringComparison.Ordinal);
            Assert.DoesNotContain("slurs", text, StringComparison.Ordinal);
        }

        var addressed = await context.NotificationsForPeople.AsNoTracking().Where(p => p.NotificationId == notification.Id).Select(p => p.UserId).ToListAsync(Ct);
        Assert.Contains(seer.Id, addressed);
        Assert.DoesNotContain(blind.Id, addressed);
    }

    [Fact]
    public async Task Notifications_CollapseToOnePerFifteenMinutes()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync("900000000000000001", ModbotPermissions.ViewReports, ct: Ct);

        await SlashAsync(services, Slash("300000000000000001", Reported, Words));
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await SlashAsync(services, Slash("300000000000000002", Reported, Words));
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await SlashAsync(services, Slash("300000000000000003", Reported, Words));

        await using (var context = services.Database.NewContext())
            Assert.Single(await context.Notifications.AsNoTracking().Where(n => n.Kind == NotificationKinds.MemberReportNew).ToListAsync(Ct));

        Assert.Equal(3, (await ReportsAsync(services)).Count);

        services.Clock.Advance(TimeSpan.FromMinutes(16));
        await SlashAsync(services, Slash("300000000000000004", Reported, Words));

        await using var after = services.Database.NewContext();
        Assert.Equal(2, await after.Notifications.AsNoTracking().CountAsync(n => n.Kind == NotificationKinds.MemberReportNew, Ct));
    }

    [Fact]
    public async Task AReportAboutAStaffAccount_ToldOnlyToThoseWhoAlsoHoldReviewTickets()
    {
        await using var services = await SetUpAsync(_db);
        var staffId = "910000000000000001";
        await services.LinkedAccountAsync(staffId, ModbotPermissions.ViewMembers, ct: Ct);
        var plain = await services.LinkedAccountAsync("900000000000000001", ModbotPermissions.ViewReports, ct: Ct);
        var reviewer = await services.LinkedAccountAsync("900000000000000002", ModbotPermissions.ViewReports | ModbotPermissions.ReviewTickets, ct: Ct);

        await SlashAsync(services, Slash(Reporter, staffId, Words));

        await using var context = services.Database.NewContext();
        var notification = Assert.Single(await context.Notifications.AsNoTracking().Where(n => n.Kind == NotificationKinds.MemberReportNew).ToListAsync(Ct));
        var addressed = await context.NotificationsForPeople.AsNoTracking().Where(p => p.NotificationId == notification.Id).Select(p => p.UserId).ToListAsync(Ct);

        Assert.Contains(reviewer.Id, addressed);
        Assert.DoesNotContain(plain.Id, addressed);
    }

    // ── Through the bot ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bot sends the menu to the report handler and the report form back to it, not to the staff
    /// handler, which would say it does not know the form; neither leaves a command fact; and the menu
    /// is registered, for everyone, once the operator turns it on.
    /// </summary>
    [Fact]
    public async Task TheBotRoutesTheMenuAndItsForm_ToTheReportHandler_AndRegistersTheMenuForEveryone()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = new Modbot.Discord.Bot.DiscordBotService(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new Modbot.Discord.Bot.DiscordBotOptions
            {
                FirstRetry = TimeSpan.FromSeconds(30),
                MaxRetry = TimeSpan.FromMinutes(10),
                RebuildAfterDisconnected = TimeSpan.FromMinutes(3),
            },
            (_, _) => Task.CompletedTask);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = "424242";
            s.SwitchCommand(DiscordCommands.ReportMenu, true);
        }, Ct);

        await bot.TickAsync(Ct);
        await gateway.RaiseReadyAsync();

        var registered = Assert.Single(gateway.RegisteredCommands, c => c.Name == DiscordCommands.ReportMenu);
        Assert.Equal(DiscordCommandKind.Message, registered.Kind);
        Assert.False(registered.StaffOnly);

        var (call, forms) = Menu(Reporter, Message());
        await gateway.RaiseCommandAsync(call);

        var form = Assert.Single(forms);
        await gateway.RaiseFormAsync(Submit(Reporter, form, Words));

        var report = Assert.Single(await ReportsAsync(services));
        Assert.Equal(Words, report.Text);
        Assert.Equal(0, await CommandFactsAsync(services));
    }

    // ── /help ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Help_ListsReportOnlyWhileItIsOn()
    {
        await using var services = await SetUpAsync(_db, slash: true);

        DiscordCommandCall Help() => new("999", "someone", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

        Assert.Contains("`/report member: what:`", (await SlashAsync(services, Help()))!.Text, StringComparison.Ordinal);

        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Report, false), Ct);
        Assert.DoesNotContain("/report", (await SlashAsync(services, Help()))!.Text, StringComparison.Ordinal);
    }
}
