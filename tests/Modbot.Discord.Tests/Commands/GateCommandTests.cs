using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.Commands;
using Modbot.Discord.Gate;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;
using Modbot.Analytics.Facts;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/gate</c> (Discord commands design §3.7 and §4, step 4): who may run each step, that Let in and
/// Hold are the join gate's own actions and write its own facts, that Hold asks for nothing first
/// and carries Lift hold, and the list of who is waiting with a Let in button for each.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GateCommandTests
{
    private const string Guild = "700";
    private const string Role = "800";
    private const string Channel = "900";
    private const string Caller = "100";

    private readonly PostgresFixture _db;

    public GateCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, string mode = DiscordGateModes.On)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.DiscordGateMode = mode;
            s.DiscordGateMemberRoleId = Role;
            s.DiscordGateChannelId = Channel;
            s.DiscordGateStartedAt = services.Clock.UtcNow;
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string caller, string step, params (string Name, string Value)[] options)
        => new(
            caller,
            "someone",
            DiscordCommands.Gate,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            (_, _) => Task.CompletedTask)
        {
            Subcommand = step,
        };

    private static async Task<DiscordReply> RunAsync(TestServices services, DiscordCommandCall call, FakeGateway? gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, Ct, gateway);
    }

    private static async Task JoinAsync(TestServices services, FakeGateway gateway, string userId, string name = "newcomer")
    {
        using var scope = services.Scope();
        var member = new DiscordMemberSnapshot(userId, name, name, null, false, services.Clock.UtcNow, [], null);
        await scope.ServiceProvider.GetRequiredService<JoinGate>()
            .JoinedAsync(gateway, new DiscordMemberJoin(Guild, userId, name, false, member), Ct);
    }

    private static async Task<DiscordReply> PressAsync(TestServices services, FakeGateway gateway, string presser, string buttonId)
    {
        using var scope = services.Scope();
        var press = new DiscordButtonPress(presser, "someone", buttonId, (_, _) => Task.CompletedTask);
        return await scope.ServiceProvider.GetRequiredService<JoinGate>().PressAsync(gateway, press, Ct);
    }

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    private static async Task<DiscordGateEntry> EntryAsync(TestServices services, string userId)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordGateEntries.AsNoTracking().Where(e => e.DiscordUserId == userId).FirstAsync(Ct);
    }

    /// <summary>An enabled account with a Discord id proved and no VRChat account linked.</summary>
    private static async Task LinkedWithoutVRChatAsync(TestServices services, string discordUserId, ModbotPermissions permissions)
    {
        await using var db = services.Database.NewContext();
        var user = await TestAccounts.CreateAsync(
            db, "user_" + Guid.NewGuid().ToString("n")[..8], TestAccounts.Password, permissions, linked: false, Ct);
        user.DiscordUserId = discordUserId;
        user.DiscordUsername = "someone";
        user.DiscordVerifiedAt = services.Clock.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    public static TheoryData<string> Steps =>
    [
        DiscordCommands.GateWaiting,
        DiscordCommands.GateLetIn,
        DiscordCommands.GateHold,
        DiscordCommands.GateLift,
    ];

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate_IsAModeratorsCommand_OnByDefault_PrivateAndNeedsManageTheJoinGate()
    {
        var gate = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.Gate);

        Assert.Equal(DiscordShownTo.Moderators, gate.ShownTo);
        Assert.Equal(DiscordReplyKind.Private, gate.Reply);
        Assert.Empty(gate.Options);
        Assert.True(DiscordCommandSwitches.Find(DiscordCommands.Gate)?.OnByDefault);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.Gate);
        Assert.False(DiscordCommands.IsForEveryone(DiscordCommands.Gate));

        Assert.Equal(ModbotPermissions.ManageJoinGate, DiscordCommands.Requires(DiscordCommands.Gate));
        Assert.Equal("Manage the join gate", DiscordCommands.Label(ModbotPermissions.ManageJoinGate));
        Assert.Equal("See members", DiscordCommands.Label(ModbotPermissions.ViewMembers));
    }

    [Fact]
    public void Gate_HasFourSteps_InTheDesignsWords_AndLetInTakesAMember()
    {
        var gate = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Gate);

        Assert.Equal(
            [
                ("waiting", "Who is waiting"),
                ("let-in", "Let someone in"),
                ("hold", "Hold new joiners"),
                ("lift", "Lift the hold"),
            ],
            gate.Subcommands!.Select(s => (s.Name, s.Description)));

        var member = Assert.Single(gate.Subcommands!.Single(s => s.Name == "let-in").Options);
        Assert.Equal(DiscordOptionKind.Member, member.Kind);
        Assert.True(member.Required);
        Assert.Equal(DiscordCommands.MemberOption, member.Name);

        Assert.Equal(
            [
                "`/gate waiting` — Who is waiting",
                "`/gate let-in member:` — Let someone in",
                "`/gate hold` — Hold new joiners",
                "`/gate lift` — Lift the hold",
            ],
            DiscordCommandHandler.HelpLines(gate));
    }

    [Fact]
    public void OnlyWaiting_DoesNotWrite()
    {
        Assert.False(DiscordCommands.Writes(DiscordCommands.Gate, DiscordCommands.GateWaiting));
        Assert.True(DiscordCommands.Writes(DiscordCommands.Gate, DiscordCommands.GateLetIn));
        Assert.True(DiscordCommands.Writes(DiscordCommands.Gate, DiscordCommands.GateHold));
        Assert.True(DiscordCommands.Writes(DiscordCommands.Gate, DiscordCommands.GateLift));
    }

    // ── This server only ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACommandFromAnotherServerOrADirectMessage_IsNotOurs_AndTheLetInButtonFollowsTheSameRule()
    {
        Assert.True(DiscordNetGateway.IsForThisServer(Guild, ulong.Parse(Guild)));
        Assert.False(DiscordNetGateway.IsForThisServer(Guild, 999UL));
        Assert.False(DiscordNetGateway.IsForThisServer(Guild, null));

        var letIn = JoinGateButtons.LetInFor("1001");
        Assert.True(DiscordNetGateway.IsOurButton(Guild, ulong.Parse(Guild), letIn));
        Assert.False(DiscordNetGateway.IsOurButton(Guild, 999UL, letIn));
        Assert.False(DiscordNetGateway.IsOurButton(Guild, null, letIn));
    }

    [Fact]
    public void ALetInButton_IsAcknowledgedStraightAway_LikeTheGatesOthers()
        => Assert.False(DiscordNetGateway.AnswersInPlace(JoinGateButtons.LetInFor("1001")));

    [Fact]
    public void ALetInButton_NamesThePerson_UnderTheGatesOwnPrefix()
    {
        var id = JoinGateButtons.LetInFor("1001");

        Assert.StartsWith("modbot:gate:", id, StringComparison.Ordinal);
        Assert.True(JoinGateButtons.Is(id));
        Assert.True(id.Length <= 100);
        Assert.Equal("1001", JoinGateButtons.LetInTarget(id));
        Assert.Equal("1001", JoinGateButtons.LetInTarget(DiscordActionButton.Marked(id, Guild)));

        Assert.Null(JoinGateButtons.LetInTarget(JoinGateButtons.Hold));
        Assert.Null(JoinGateButtons.LetInTarget(JoinGateButtons.LetIn));
        Assert.Null(JoinGateButtons.LetInTarget(JoinGateButtons.LetIn + ":"));
    }

    // ── Who may run it ──────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task AnUnlinkedCaller_IsToldToLink_AndNothingIsDone(string step)
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001");

        var reply = await RunAsync(services, Call("999", step, ("member", "1001")), gateway);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, reply.Text);
        await AssertNothingDoneAsync(services, gateway);
        Assert.Equal("not-linked", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task ADisabledAccount_IsRefused(string step)
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001");
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, disabled: true, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, step, ("member", "1001")), gateway);

        Assert.Equal("Your Modbot account is disabled.", reply.Text);
        await AssertNothingDoneAsync(services, gateway);
        Assert.Equal("disabled", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task WithoutManageTheJoinGate_EveryStepIsRefusedByName(string step)
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001");
        await services.LinkedAccountAsync(
            Caller, ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile | ModbotPermissions.WriteNotes, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, step, ("member", "1001")), gateway);

        Assert.Equal("You need the \"Manage the join gate\" permission in Modbot to use /gate.", reply.Text);
        await AssertNothingDoneAsync(services, gateway);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(DiscordCommands.GateLetIn)]
    [InlineData(DiscordCommands.GateHold)]
    [InlineData(DiscordCommands.GateLift)]
    public async Task WithNoVRChatLink_TheStepsThatWriteAreRefused(string step)
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001");
        await LinkedWithoutVRChatAsync(services, Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers);

        var reply = await RunAsync(services, Call(Caller, step, ("member", "1001")), gateway);

        Assert.Equal("Link your VRChat account in Modbot first.", reply.Text);
        await AssertNothingDoneAsync(services, gateway);
        Assert.Equal("no-vrchat", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WithNoVRChatLink_WaitingIsAReadAndStillAnswers()
    {
        await using var services = await SetUpAsync(_db);
        await JoinAsync(services, new FakeGateway(), "1001");
        await LinkedWithoutVRChatAsync(services, Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), new FakeGateway());

        Assert.Contains("newcomer", reply.Text, StringComparison.Ordinal);
    }

    /// <summary>The web app shows who is at the gate only with See members; the bot shows no more.</summary>
    [Fact]
    public async Task Waiting_AlsoNeedsSeeMembers_AsTheWebAppDoes()
    {
        await using var services = await SetUpAsync(_db);
        await JoinAsync(services, new FakeGateway(), "1001");
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), new FakeGateway());

        Assert.Equal("You need the \"See members\" permission in Modbot to use /gate waiting.", reply.Text);
        Assert.Null(reply.Actions);
        Assert.DoesNotContain("newcomer", reply.Text, StringComparison.Ordinal);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    private static async Task AssertNothingDoneAsync(TestServices services, FakeGateway gateway)
    {
        Assert.Empty(gateway.RoleChanges);
        Assert.Null((await services.SettingsAsync(Ct)).DiscordGateHeldAt);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordGateLetIn, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordGateHeld, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordGateHoldLifted, Ct));
    }

    [Fact]
    public async Task Help_ListsTheFourSteps_OnlyForSomebodyWhoMayManageTheGate()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewMembers, ct: Ct);

        var help = new Func<string, Task<string?>>(async who => (await RunHelpAsync(services, who)).Text);

        var manager = await help(Caller);
        Assert.Contains("`/gate waiting` — Who is waiting", manager, StringComparison.Ordinal);
        Assert.Contains("`/gate let-in member:` — Let someone in", manager, StringComparison.Ordinal);
        Assert.Contains("`/gate hold` — Hold new joiners", manager, StringComparison.Ordinal);
        Assert.Contains("`/gate lift` — Lift the hold", manager, StringComparison.Ordinal);

        Assert.DoesNotContain("/gate", await help("200") ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("/gate", await help("999") ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task<DiscordReply> RunHelpAsync(TestServices services, string who)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(
            new DiscordCommandCall(who, "someone", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask), Ct);
    }

    // ── Let in ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LetIn_GivesTheMemberRole_AndWritesTheGatesOwnFact_UnderTheModeratorsName()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001");
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateLetIn, ("member", "1001")), gateway);

        Assert.Equal("Let in <@1001>.", reply.Text);

        var change = Assert.Single(gateway.RoleChanges);
        Assert.Equal((true, Guild, "1001", Role), change);

        var entry = await EntryAsync(services, "1001");
        Assert.Equal(DiscordGateOutcomes.LetIn, entry.Outcome);
        Assert.NotNull(entry.ClosedAt);

        // The fact the web app's Let in writes, with the same subject and the moderator as actor.
        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateLetIn, Ct));
        Assert.Equal("1001", fact.SubjectId);
        Assert.Equal(account.Id.ToString(), fact.ActorId);
        Assert.Equal("person", JsonDocument.Parse(fact.Data).RootElement.GetProperty("by").GetString());

        var command = await LastCommandFactAsync(services);
        Assert.Equal("gate", command.GetProperty("command").GetString());
        Assert.Equal("answered", command.GetProperty("outcome").GetString());
        Assert.Equal("1001", command.GetProperty("targetDiscord").GetString());
    }

    [Fact]
    public async Task LetIn_ForSomebodyNotAtTheGate_SaysSo_AndChangesNothing()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateLetIn, ("member", "4242")), gateway);

        Assert.Equal("That person is not at the join gate.", reply.Text);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordGateLetIn, Ct));
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task LetIn_WithNoLiveSession_SaysTheBotIsNotConnected()
    {
        await using var services = await SetUpAsync(_db);
        await JoinAsync(services, new FakeGateway(), "1001");
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateLetIn, ("member", "1001")), gateway: null);

        Assert.Equal("The Discord bot is not connected.", reply.Text);
        Assert.Null((await EntryAsync(services, "1001")).ClosedAt);
    }

    [Fact]
    public async Task LetIn_WithNoMemberPicked_AsksForOne()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateLetIn), new FakeGateway());

        Assert.Equal(GateCommand.PickOneMessage, reply.Text);
        Assert.Equal("invalid", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    // ── Hold and lift ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_HoldsAtOnce_WithNoConfirmation_AndTheReplyCarriesLiftHold()
    {
        await using var services = await SetUpAsync(_db);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        // One call is all it takes: the first answer is the held reply, not a question.
        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateHold), new FakeGateway());

        Assert.Equal("New joiners are held.", reply.Text);
        var button = Assert.Single(reply.Actions!);
        Assert.Equal("Lift hold", button.Label);
        Assert.Equal(JoinGateButtons.LiftHold, DiscordActionButton.Plain(button.Id));
        Assert.True(DiscordNetGateway.IsOurButton(Guild, ulong.Parse(Guild), button.Id));

        Assert.NotNull((await services.SettingsAsync(Ct)).DiscordGateHeldAt);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateHeld, Ct));
        Assert.Equal(account.Id.ToString(), fact.ActorId);
        Assert.Equal("person", JsonDocument.Parse(fact.Data).RootElement.GetProperty("by").GetString());
    }

    [Fact]
    public async Task TheLiftHoldButtonOnTheReply_LiftsTheHold_ThroughTheGatesOwnButton()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var held = await RunAsync(services, Call(Caller, DiscordCommands.GateHold), gateway);
        var pressed = await PressAsync(services, gateway, Caller, held.Actions![0].Id);

        Assert.Equal("Hold lifted.", pressed.Text);
        Assert.Null((await services.SettingsAsync(Ct)).DiscordGateHeldAt);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateHoldLifted, Ct));
    }

    [Fact]
    public async Task Lift_LiftsTheHold_AndWritesTheGatesOwnFact()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s => s.DiscordGateHeldAt = services.Clock.UtcNow, Ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateLift), new FakeGateway());

        Assert.Equal("Hold lifted.", reply.Text);
        Assert.Null(reply.Actions);
        Assert.Null((await services.SettingsAsync(Ct)).DiscordGateHeldAt);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateHoldLifted, Ct));
        Assert.Equal(account.Id.ToString(), fact.ActorId);
        Assert.Equal("answered", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Hold_WhileTheGateIsOff_SaysSo_AndWritesNothing()
    {
        await using var services = await SetUpAsync(_db, DiscordGateModes.Off);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateHold), new FakeGateway());

        Assert.Equal("The join gate is not on.", reply.Text);
        Assert.Null(reply.Actions);
        Assert.Null((await services.SettingsAsync(Ct)).DiscordGateHeldAt);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordGateHeld, Ct));
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    // ── Waiting ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waiting_ListsTheOldestTen_EachWithALetInButton_AndSaysHowManyMore()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        for (var i = 0; i < 12; i++)
        {
            await JoinAsync(services, gateway, (1001 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "joiner" + i);
            services.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), gateway);

        var buttons = reply.Actions!;
        Assert.Equal(10, buttons.Count);
        Assert.Equal(
            Enumerable.Range(1001, 10).Select(id => JoinGateButtons.LetInFor(id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            buttons.Select(b => b.Id));
        Assert.All(buttons, b => Assert.StartsWith("Let in ", b.Label, StringComparison.Ordinal));
        Assert.Equal("Let in joiner0", buttons[0].Label);

        Assert.Equal(10, reply.Text!.Split('\n').Count(line => line.StartsWith("**joiner", StringComparison.Ordinal)));
        Assert.Contains("**joiner0**", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("joiner10", reply.Text, StringComparison.Ordinal);
        Assert.EndsWith("and 2 more", reply.Text, StringComparison.Ordinal);
        Assert.Contains("<t:", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APressOnTheLetInButtonFromTheList_LetsThatPersonIn()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001", "joiner");
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers, ct: Ct);

        var list = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), gateway);
        var pressed = await PressAsync(services, gateway, Caller, list.Actions![0].Id);

        Assert.Equal("Let in <@1001>.", pressed.Text);
        Assert.Equal((true, Guild, "1001", Role), Assert.Single(gateway.RoleChanges));
        Assert.Equal(DiscordGateOutcomes.LetIn, (await EntryAsync(services, "1001")).Outcome);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateLetIn, Ct));
        Assert.Equal(account.Id.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task ALetInButtonPressedWithoutManageTheJoinGate_ChangesNothing()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001");
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewMembers, ct: Ct);

        var refused = await PressAsync(services, gateway, "200", JoinGateButtons.LetInFor("1001"));
        var stranger = await PressAsync(services, gateway, "300", JoinGateButtons.LetInFor("1001"));

        Assert.Equal("You need the \"Manage the join gate\" permission in Modbot.", refused.Text);
        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, stranger.Text);
        Assert.Empty(gateway.RoleChanges);
        Assert.Null((await EntryAsync(services, "1001")).ClosedAt);
    }

    [Fact]
    public async Task Waiting_WithNobodyAtTheGate_SaysSo()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), new FakeGateway());

        Assert.Equal("Nobody is waiting at the join gate.", reply.Text);
        Assert.Null(reply.Actions);
    }

    [Fact]
    public async Task Waiting_WhileTheGateIsOff_SaysSo()
    {
        await using var services = await SetUpAsync(_db, DiscordGateModes.Off);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), new FakeGateway());

        Assert.Equal("The join gate is not on.", reply.Text);
    }

    [Fact]
    public async Task Waiting_ShowsTheNameAsEscapedText_AndNotWhoIsLinked()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1001", "*bold*");
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ManageJoinGate | ModbotPermissions.ViewMembers, ct: Ct);

        var reply = await RunAsync(services, Call(Caller, DiscordCommands.GateWaiting), gateway);

        Assert.StartsWith("**\\*bold\\***", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("VRChat", reply.Text, StringComparison.Ordinal);
    }
}
