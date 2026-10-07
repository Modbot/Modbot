using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/ban</c> and <c>/kick</c> (Discord commands design §3.3, step 3): what each one means for a
/// VRChat person, a linked member and an unlinked member; who may do which; the form, the
/// confirmation and acting once; and what a caller without See profiles is never shown.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BanKickCommandsTests
{
    private const string Caller = "100";

    /// <summary>A Discord member linked to <see cref="Linked"/>'s VRChat account below.</summary>
    private const string LinkedMember = "666";

    /// <summary>A Discord member with no VRChat link.</summary>
    private const string UnlinkedMember = "777";

    /// <summary>A VRChat person linked to <see cref="LinkedMember"/>.</summary>
    private const string Linked = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";

    /// <summary>A VRChat person with no Discord link.</summary>
    private const string Stranger = "usr_aaaaaaaa-0000-0000-0000-000000000001";

    private readonly PostgresFixture _db;

    public BanKickCommandsTests(PostgresFixture db) => _db = db;

    // ── Setting the scene ───────────────────────────────────────────────────────────────────

    private sealed class Recorder
    {
        public List<DiscordReply> Replies { get; } = [];

        public List<DiscordReply> Updates { get; } = [];

        public List<DiscordForm> Forms { get; } = [];

        public bool FormsTooLate { get; set; }

        public Task Reply(DiscordReply reply, CancellationToken ct)
        {
            Replies.Add(reply);
            return Task.CompletedTask;
        }

        public Task Update(DiscordReply reply, CancellationToken ct)
        {
            Updates.Add(reply);
            return Task.CompletedTask;
        }

        public Task Show(DiscordForm form, CancellationToken ct)
        {
            if (FormsTooLate)
                throw new InvalidOperationException("Too late.");

            Forms.Add(form);
            return Task.CompletedTask;
        }
    }

    private static DiscordCommandCall Slash(string discordUserId, string command, Recorder recorder, params (string Name, string Value)[] options)
        => new(
            discordUserId,
            "moderator",
            command,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            recorder.Reply,
            recorder.Show);

    private static DiscordButtonPress Press(string discordUserId, string buttonId, Recorder recorder)
        => new(discordUserId, "moderator", buttonId, recorder.Reply, recorder.Show, recorder.Update);

    private static DiscordFormSubmit Submit(string discordUserId, string formId, params (string Field, string[] Values)[] values)
        => new(
            discordUserId,
            "moderator",
            formId,
            values.ToDictionary(v => v.Field, v => (IReadOnlyList<string>)v.Values, StringComparer.Ordinal),
            (_, _) => Task.CompletedTask);

    private static StaffInteractionHandler Handler(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<StaffInteractionHandler>();

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services, CancellationToken ct)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    /// <summary>The Discord side of the scene: who is linked to whom, and the names the server shows.</summary>
    private static async Task SeedPeopleAsync(TestServices services, CancellationToken ct)
    {
        await using (var db = services.Database.NewContext())
        {
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = LinkedMember,
                VRChatUserId = Linked,
                LinkedAt = services.Clock.UtcNow,
            });

            foreach (var (id, name) in new[] { (LinkedMember, "Casey"), (UnlinkedMember, "Robin") })
            {
                db.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = "424242",
                    UserId = id,
                    Username = name.ToLowerInvariant(),
                    DisplayName = name,
                    FirstSeenAt = services.Clock.UtcNow,
                    UpdatedAt = services.Clock.UtcNow,
                });
            }

            await db.SaveChangesAsync(ct);
        }

        await services.AddProfileAsync(Linked, "jessie", ct: ct);
        await services.AddProfileAsync(Stranger, "wanda", ct: ct);
    }

    private static async Task ChangePermissionsAsync(TestServices services, ModbotUser account, ModbotPermissions permissions, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.Id == account.Id, ct);
        var roleId = await TestAccounts.RoleForAsync(db, permissions, ct);

        foreach (var role in user.Roles.ToList())
            db.Remove(role);

        db.Add(new ModbotUserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync(ct);
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BanAndKick_AreShownToModerators_OnByDefault_AndNeedTheWebAppsPermissions()
    {
        foreach (var name in new[] { DiscordCommands.Ban, DiscordCommands.Kick })
        {
            var command = Assert.Single(DiscordCommands.All, c => c.Name == name);
            Assert.Equal(DiscordShownTo.Moderators, command.ShownTo);
            Assert.Equal(DiscordReplyKind.Private, command.Reply);
            Assert.Equal(DiscordCommandKind.Slash, command.Kind);

            var found = DiscordCommandSwitches.Find(name);
            Assert.True(found?.OnByDefault);
            Assert.False(found!.Menu);
            Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == name);

            var member = command.Options.Single(o => o.Name == DiscordCommands.MemberOption);
            Assert.Equal(DiscordOptionKind.Member, member.Kind);
            Assert.False(member.Required);

            var vrchat = command.Options.Single(o => o.Name == DiscordCommands.VRChatOption);
            Assert.Equal(DiscordOptionKind.Text, vrchat.Kind);
            Assert.True(vrchat.Suggests);
            Assert.False(vrchat.Required);
        }

        Assert.Equal(ModbotPermissions.Ban, DiscordCommands.Requires(DiscordCommands.Ban));
        Assert.Equal(ModbotPermissions.Kick, DiscordCommands.Requires(DiscordCommands.Kick));
        Assert.Equal(ModbotPermissions.DiscordBan, DiscordCommands.RequiresOnDiscord(DiscordCommands.Ban));
        Assert.Equal(ModbotPermissions.DiscordKick, DiscordCommands.RequiresOnDiscord(DiscordCommands.Kick));
        Assert.Null(DiscordCommands.RequiresOnDiscord(DiscordCommands.Note));

        Assert.Equal("Ban", DiscordCommands.Label(ModbotPermissions.Ban));
        Assert.Equal("Kick", DiscordCommands.Label(ModbotPermissions.Kick));
        Assert.Equal("Ban on Discord", DiscordCommands.Label(ModbotPermissions.DiscordBan));
        Assert.Equal("Remove from Discord", DiscordCommands.Label(ModbotPermissions.DiscordKick));

        Assert.True(DiscordCommands.Writes(DiscordCommands.Ban));
        Assert.True(DiscordCommands.Writes(DiscordCommands.Kick));
    }

    [Fact]
    public void Kick_OffersDiscordServerVRChatGroupOrBoth_InTheDesignsWords()
    {
        var from = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Kick).Options.Single(o => o.Name == DiscordCommands.KickFromOption);

        Assert.Equal(DiscordOptionKind.Choice, from.Kind);
        Assert.False(from.Required);
        Assert.Equal("Remove from", from.Description);
        Assert.Equal(["Discord server", "VRChat group", "Both"], from.Choices!.Select(c => c.Name));
        Assert.Equal(
            [DiscordCommands.FromDiscord, DiscordCommands.FromVRChat, DiscordCommands.FromBoth],
            from.Choices!.Select(c => c.Value));

        Assert.Equal("Ban someone", DiscordCommands.All.Single(c => c.Name == DiscordCommands.Ban).Description);
        Assert.Equal("Remove someone", DiscordCommands.All.Single(c => c.Name == DiscordCommands.Kick).Description);
    }

    [Theory]
    [InlineData(DiscordCommands.Ban, ModbotPermissions.Ban, true)]
    [InlineData(DiscordCommands.Ban, ModbotPermissions.DiscordBan, true)]
    [InlineData(DiscordCommands.Ban, ModbotPermissions.Kick | ModbotPermissions.DiscordKick | ModbotPermissions.ViewProfile, false)]
    [InlineData(DiscordCommands.Kick, ModbotPermissions.Kick, true)]
    [InlineData(DiscordCommands.Kick, ModbotPermissions.DiscordKick, true)]
    [InlineData(DiscordCommands.Kick, ModbotPermissions.Ban | ModbotPermissions.DiscordBan, false)]
    [InlineData(DiscordCommands.Note, ModbotPermissions.DiscordBan, false)]
    [InlineData(DiscordCommands.Kick, ModbotPermissions.Administrator, true)]
    public void ACommandIsUsable_WithEitherPermission_ButOnlyThoseOfItsOwn(string command, ModbotPermissions held, bool usable)
        => Assert.Equal(usable, DiscordCommands.CanUse(command, held));

    [Fact]
    public async Task Help_ListsBanAndKick_ForSomebodyWhoMayActOnDiscordOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.WriteNotes, ct: ct);

        async Task<string> HelpAsync(string who)
        {
            using var scope = services.Scope();
            var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(
                new DiscordCommandCall(who, "x", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask), ct);
            return reply.Text!;
        }

        var banner = await HelpAsync(Caller);
        Assert.Contains("`/ban member: vrchat:` — Ban someone", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("`/kick", banner, StringComparison.Ordinal);

        var noter = await HelpAsync("200");
        Assert.DoesNotContain("`/ban", noter, StringComparison.Ordinal);
        Assert.DoesNotContain("`/kick", noter, StringComparison.Ordinal);
    }

    // ── Who may run them ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiscordCommands.Ban)]
    [InlineData(DiscordCommands.Kick)]
    public async Task AnUnlinkedCaller_ADisabledOne_AndOneWithNoVRChatLink_AreRefusedInThatOrder(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        var unlinked = await handler.HandleCommandAsync(Slash("999", command, recorder, ("member", UnlinkedMember)), null, ct);
        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, unlinked?.Text);
        Assert.Equal("not-linked", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());

        await services.LinkedAccountAsync("101", ModbotPermissions.Administrator, disabled: true, ct: ct);
        var disabled = await handler.HandleCommandAsync(Slash("101", command, recorder, ("member", UnlinkedMember)), null, ct);
        Assert.Equal("Your Modbot account is disabled.", disabled?.Text);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "no_vrchat", TestAccounts.Password, ModbotPermissions.Administrator, linked: false, ct);
            user.DiscordUserId = "102";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var noLink = await handler.HandleCommandAsync(Slash("102", command, recorder, ("member", UnlinkedMember)), null, ct);
        Assert.Equal(StaffInteractionHandler.NeedsVRChatMessage, noLink?.Text);
        Assert.Equal("no-vrchat", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());

        Assert.Empty(recorder.Forms);
        Assert.Empty(services.Staff.DiscordChecks);
    }

    [Theory]
    [InlineData(DiscordCommands.Ban, "Ban")]
    [InlineData(DiscordCommands.Kick, "Kick")]
    public async Task WithNeitherPermission_TheCommandIsRefusedByName_AndRecorded(string command, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile | ModbotPermissions.WriteNotes, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleCommandAsync(Slash(Caller, command, recorder, ("member", UnlinkedMember)), null, ct);

        Assert.Equal($"You need the \"{label}\" permission in Modbot to use /{command}.", reply?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(DiscordCommands.Ban)]
    [InlineData(DiscordCommands.Kick)]
    public async Task ACommandSwitchedOff_IsRefused_AndNoFormOpens(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, ct: ct);
        await services.ConfigureAsync(s => s.SwitchCommand(command, false), ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleCommandAsync(Slash(Caller, command, recorder, ("member", UnlinkedMember)), null, ct);

        Assert.Equal($"/{command} is turned off on this server.", reply?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("off", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TheSlashHandler_DoesNotAnswerThem_ItSendsThemToTheStaffHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, ct: ct);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(
            new DiscordCommandCall(
                Caller, "x", DiscordCommands.Ban, new Dictionary<string, string> { ["member"] = UnlinkedMember }, (_, _) => Task.CompletedTask),
            ct);

        Assert.Equal("Modbot does not know that command.", reply.Text);
        Assert.True(StaffInteractionHandler.HandlesCommand(DiscordCommands.Ban));
        Assert.True(StaffInteractionHandler.HandlesCommand(DiscordCommands.Kick));
        Assert.False(StaffInteractionHandler.HandlesCommand(DiscordCommands.Note));
    }

    // ── One person, named one way (the helper /note and /watch use) ─────────────────────────

    [Fact]
    public async Task NamingNobody_OrBoth_IsRefusedInTheSharedWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        var none = await handler.HandleCommandAsync(Slash(Caller, "ban", recorder), null, ct);
        var both = await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember), ("vrchat", Stranger)), null, ct);

        Assert.Equal(StaffCommands.PickOneMessage, none?.Text);
        Assert.Equal(StaffCommands.PickOnlyOneMessage, both?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("invalid", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    // ── /ban: a linked member or a VRChat person is the group's ban ─────────────────────────

    [Fact]
    public async Task BanningALinkedMember_IsAGroupBan_AndTheConfirmationSaysBothPlaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban, ct: ct);
        services.Staff.Answer = new StaffActionAnswer(true, null, DiscordDone: true);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        Assert.Null(await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", LinkedMember)), gateway, ct));

        // The group's own reasons form, titled with the name the caller saw in Discord's picker.
        var form = Assert.Single(recorder.Forms);
        Assert.Equal("Ban Casey", form.Title);
        Assert.True(form.Fields.Single(f => f.Id == StaffInteractionHandler.ReasonsField).Required);
        Assert.DoesNotContain(form.Fields, f => f.Id == StaffInteractionHandler.WhyField);

        var harassment = services.Staff.Reasons[0].Id;
        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, form.Id, ("reasons", [harassment.ToString("N")]), ("note", ["Slurs in voice."])), gateway, ct);

        Assert.StartsWith("Ban **Casey** from the VRChat group and the Discord server?", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains("Reasons: Harassment", confirmation.Text, StringComparison.Ordinal);
        var yes = confirmation.Actions![0];
        Assert.Equal("Ban", yes.Label);
        Assert.Equal(DiscordButtonStyle.Danger, yes.Style);
        Assert.Equal("Cancel", confirmation.Actions[1].Label);
        Assert.Empty(services.Staff.Sent);

        Assert.Null(await handler.HandleButtonAsync(Press(Caller, yes.Id, recorder), gateway, ct));

        // The VRChat id of the link is what was banned; the service takes the linked Discord account with it.
        var sent = Assert.Single(services.Staff.Sent);
        Assert.Equal("ban", sent.Action);
        Assert.Equal(Linked, sent.UserId);
        Assert.Equal(account.Id, sent.By.UserId);
        Assert.StartsWith("discord:", sent.Key, StringComparison.Ordinal);
        Assert.Empty(services.Staff.DiscordBans);

        Assert.Equal("Banning **Casey**…", recorder.Updates[0].Text);
        Assert.Equal("Banned **Casey**.\nTheir linked Discord account was banned too.", recorder.Updates[^1].Text);
        Assert.Empty(gateway.Handled);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal("ban", fact.GetProperty("command").GetString());
        Assert.Equal("done", fact.GetProperty("outcome").GetString());
        Assert.Equal(Linked, fact.GetProperty("target").GetString());

        // Casey's VRChat name was never shown to somebody who may not see profiles.
        Assert.DoesNotContain(recorder.Updates.Concat(recorder.Replies), r => r.Text?.Contains("jessie", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task BanningAVRChatPerson_WithALinkedDiscordAccount_SaysBothPlaces_AndWithoutOneSaysTheGroup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct: ct);

        async Task<string?> QuestionAsync(string vrchat)
        {
            var recorder = new Recorder();
            using var scope = services.Scope();
            var handler = Handler(scope);

            Assert.Null(await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("vrchat", vrchat)), null, ct));
            var confirmation = await handler.HandleFormAsync(
                Submit(Caller, recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);
            return confirmation.Text;
        }

        Assert.StartsWith("Ban **jessie** from the VRChat group and the Discord server?", await QuestionAsync("jessie"), StringComparison.Ordinal);
        Assert.StartsWith("Ban **wanda** from the group?", await QuestionAsync(Stranger), StringComparison.Ordinal);
    }

    // ── /ban: an unlinked member is a Discord ban ───────────────────────────────────────────

    [Fact]
    public async Task BanningAnUnlinkedMember_IsADiscordBan_WithAReasonAndHowManyDaysOfMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        Assert.Null(await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), gateway, ct));

        // The server's form: a reason and "Delete their messages from", not the group's list.
        var form = Assert.Single(recorder.Forms);
        Assert.Equal("Ban Robin", form.Title);
        Assert.DoesNotContain(form.Fields, f => f.Id == StaffInteractionHandler.ReasonsField);

        var why = form.Fields.Single(f => f.Id == StaffInteractionHandler.WhyField);
        Assert.Equal("Reason", why.Label);
        Assert.True(why.Required);

        var days = form.Fields.Single(f => f.Id == StaffInteractionHandler.DeleteDaysField);
        Assert.Equal("Delete their messages from", days.Label);
        Assert.Equal(["None", "1 day", "7 days"], days.Choices!.Select(c => c.Label));
        Assert.Equal(["0", "1", "7"], days.Choices!.Select(c => c.Value));
        Assert.Equal(1, days.MaxChoices);
        Assert.False(days.Required);

        // The server's own checks ran when the command did, and nothing was sent.
        Assert.Equal(("ban", UnlinkedMember), Assert.Single(services.Staff.DiscordChecks));

        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, form.Id, ("why", ["Spam links."]), ("deletedays", ["7"])), gateway, ct);

        Assert.StartsWith("Ban **Robin** from the Discord server?", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains("Reason: Spam links.", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains("Their messages from the last 7 days will be deleted.", confirmation.Text, StringComparison.Ordinal);
        var yes = confirmation.Actions![0];
        Assert.Equal("Ban", yes.Label);
        Assert.Equal(DiscordButtonStyle.Danger, yes.Style);
        Assert.Empty(services.Staff.DiscordBans);

        Assert.Null(await handler.HandleButtonAsync(Press(Caller, yes.Id, recorder), gateway, ct));

        var banned = Assert.Single(services.Staff.DiscordBans);
        Assert.Equal((UnlinkedMember, "Spam links.", 7), (banned.DiscordUserId, banned.Reason, banned.DeleteMessageDays));
        Assert.Equal(account.Id, banned.By.UserId);
        Assert.Empty(services.Staff.Sent);

        Assert.Equal("Banning **Robin**…", recorder.Updates[0].Text);
        Assert.Equal("Banned **Robin** from the Discord server.", recorder.Updates[^1].Text);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal("done", fact.GetProperty("outcome").GetString());
        Assert.Equal(UnlinkedMember, fact.GetProperty("targetDiscord").GetString());
        Assert.False(fact.TryGetProperty("target", out _));
    }

    [Fact]
    public async Task ADiscordBan_NeedsAReason_AndDaysFromTheList_AndAlreadyBannedIsSaidSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), null, ct);
        var form = recorder.Forms.Single();

        var empty = await handler.HandleFormAsync(Submit(Caller, form.Id, ("why", ["   "]), ("deletedays", ["0"])), null, ct);
        Assert.Equal(StaffInteractionHandler.BanNeedsAReasonMessage, empty.Text);
        Assert.Null(empty.Actions);

        var odd = await handler.HandleFormAsync(Submit(Caller, form.Id, ("why", ["Spam"]), ("deletedays", ["3"])), null, ct);
        Assert.Equal(StaffInteractionHandler.PickDaysMessage, odd.Text);

        // No days picked is none; nothing about messages is said then.
        var confirmation = await handler.HandleFormAsync(Submit(Caller, form.Id, ("why", ["Spam"])), null, ct);
        Assert.DoesNotContain("messages", confirmation.Text, StringComparison.Ordinal);

        services.Staff.DiscordAnswer = new StaffActionAnswer(true, null, Unchanged: true);
        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions![0].Id, recorder), null, ct);

        Assert.Equal(0, Assert.Single(services.Staff.DiscordBans).DeleteMessageDays);
        Assert.Equal("**Robin** is already banned on Discord.", recorder.Updates[^1].Text);
    }

    // ── The accounts that are never acted on ────────────────────────────────────────────────

    [Fact]
    public async Task TheBotItself_IsRefusedBeforeAnyFormOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, ct: ct);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        foreach (var command in new[] { "ban", "kick" })
        {
            var reply = await handler.HandleCommandAsync(Slash(Caller, command, recorder, ("member", gateway.BotUserId!)), gateway, ct);
            Assert.Equal(StaffInteractionHandler.OwnAccountMessage, reply?.Text);
        }

        Assert.Empty(recorder.Forms);
        Assert.Empty(services.Staff.DiscordBans);
        Assert.Equal(gateway.BotUserId, (await LastCommandFactAsync(services, ct)).GetProperty("targetDiscord").GetString());
    }

    [Theory]
    [InlineData("That is the owner of the Discord server. Modbot will not act on them.")]
    [InlineData("That Discord account belongs to a Modbot staff account. Modbot will not act on staff.")]
    public async Task TheOwner_AndAStaffAccount_AreRefusedWhenTheCommandRuns_AndAgainWhenTheFormIsSent(string sentence)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan | ModbotPermissions.DiscordKick, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        // Refused at once: no form for somebody who cannot be acted on.
        services.Staff.DiscordRefusal = sentence;
        var refused = await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), null, ct);
        Assert.Equal(sentence, refused?.Text);
        Assert.Empty(recorder.Forms);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal("refused", fact.GetProperty("outcome").GetString());
        Assert.Equal(UnlinkedMember, fact.GetProperty("targetDiscord").GetString());

        // And once more at the form, for somebody who became staff since: no confirmation is shown.
        services.Staff.DiscordRefusal = null;
        await handler.HandleCommandAsync(Slash(Caller, "kick", recorder, ("member", UnlinkedMember)), null, ct);
        services.Staff.DiscordRefusal = sentence;

        var form = await handler.HandleFormAsync(Submit(Caller, recorder.Forms.Single().Id, ("why", ["x"])), null, ct);
        Assert.Equal(sentence, form.Text);
        Assert.Null(form.Actions);
        Assert.Empty(services.Staff.DiscordKicks);
    }

    // ── A confirmation acts once; Cancel; a confirmation that runs out ──────────────────────

    [Fact]
    public async Task OneConfirmation_ActsOnce_AndASecondPressGetsTheFirstAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), null, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, recorder.Forms.Single().Id, ("why", ["Raid."]), ("deletedays", ["1"])), null, ct);
        var yes = confirmation.Actions![0].Id;

        // Two presses at the same moment, as two clients or a fast double tap would make: each is
        // its own request, with its own scope, and both find the one confirmation.
        async Task<Recorder> PressAsync()
        {
            var pressed = new Recorder();
            using var other = services.Scope();
            await Handler(other).HandleButtonAsync(Press(Caller, yes, pressed), null, ct);
            return pressed;
        }

        var presses = await Task.WhenAll(PressAsync(), PressAsync());

        Assert.Single(services.Staff.DiscordBans);
        Assert.All(presses, p => Assert.Equal("Banned **Robin** from the Discord server.", p.Updates[^1].Text));

        // And one more, later: still the first answer, still one call to Discord.
        await handler.HandleButtonAsync(Press(Caller, yes, recorder), null, ct);

        Assert.Single(services.Staff.DiscordBans);
        Assert.Equal("Banned **Robin** from the Discord server.", recorder.Updates[^1].Text);
        Assert.Equal("repeat", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Cancel_SendsNothing_AndTheConfirmationCannotBeUsedAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordKick, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "kick", recorder, ("member", UnlinkedMember)), null, ct);
        var confirmation = await handler.HandleFormAsync(Submit(Caller, recorder.Forms.Single().Id, ("why", [""])), null, ct);

        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions![1].Id, recorder), null, ct);
        Assert.Equal(StaffInteractionHandler.CancelledMessage, recorder.Updates[^1].Text);

        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions[0].Id, recorder), null, ct);
        Assert.Equal(StaffInteractionHandler.RunOutMessage, recorder.Updates[^1].Text);
        Assert.Empty(services.Staff.DiscordKicks);
    }

    [Fact]
    public async Task AConfirmation_RunsOutAfterFifteenMinutes_AndSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), null, ct);
        var confirmation = await handler.HandleFormAsync(Submit(Caller, recorder.Forms.Single().Id, ("why", ["Spam"])), null, ct);

        services.Clock.Advance(PendingStaffActions.Lifetime);

        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions![0].Id, recorder), null, ct);
        Assert.Equal(StaffInteractionHandler.RunOutMessage, recorder.Updates[^1].Text);
        Assert.Empty(services.Staff.DiscordBans);
    }

    [Fact]
    public async Task OnlyThePersonWhoStartedIt_MayConfirmIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.DiscordBan, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), null, ct);
        var confirmation = await handler.HandleFormAsync(Submit(Caller, recorder.Forms.Single().Id, ("why", ["Spam"])), null, ct);

        var reply = await handler.HandleButtonAsync(Press("200", confirmation.Actions![0].Id, recorder), null, ct);

        Assert.Equal(StaffInteractionHandler.NotYoursMessage, reply?.Text);
        Assert.Empty(services.Staff.DiscordBans);
    }

    // ── Permission, path by path ────────────────────────────────────────────────────────────

    /// <summary>
    /// Which permission a run needs depends on who it is about and where it acts: the group's
    /// (Ban, Kick) for a VRChat person or a linked member's ban, and the server's (Ban on Discord,
    /// Remove from Discord) for the Discord side. A null expectation means the form opened.
    /// </summary>
    [Theory]
    // /ban: a VRChat person and a linked member are the group's; an unlinked member is the server's.
    [InlineData("ban", "vrchat", null, ModbotPermissions.Ban | ModbotPermissions.ViewProfile, null)]
    [InlineData("ban", "vrchat", null, ModbotPermissions.DiscordBan | ModbotPermissions.ViewProfile, "Ban")]
    [InlineData("ban", "linked", null, ModbotPermissions.Ban, null)]
    [InlineData("ban", "linked", null, ModbotPermissions.DiscordBan, "Ban")]
    [InlineData("ban", "unlinked", null, ModbotPermissions.DiscordBan, null)]
    [InlineData("ban", "unlinked", null, ModbotPermissions.Ban, "Ban on Discord")]
    // /kick by who is named, and where it was asked to go.
    [InlineData("kick", "vrchat", null, ModbotPermissions.Kick | ModbotPermissions.ViewProfile, null)]
    [InlineData("kick", "vrchat", null, ModbotPermissions.DiscordKick | ModbotPermissions.ViewProfile, "Kick")]
    [InlineData("kick", "unlinked", null, ModbotPermissions.DiscordKick, null)]
    [InlineData("kick", "unlinked", null, ModbotPermissions.Kick, "Remove from Discord")]
    [InlineData("kick", "linked", null, ModbotPermissions.DiscordKick, null)]
    [InlineData("kick", "linked", "vrchat", ModbotPermissions.Kick, null)]
    [InlineData("kick", "linked", "vrchat", ModbotPermissions.DiscordKick, "Kick")]
    [InlineData("kick", "linked", "both", ModbotPermissions.Kick | ModbotPermissions.DiscordKick, null)]
    [InlineData("kick", "linked", "both", ModbotPermissions.Kick, "Remove from Discord")]
    [InlineData("kick", "linked", "both", ModbotPermissions.DiscordKick, "Kick")]
    [InlineData("kick", "vrchat", "discord", ModbotPermissions.Kick | ModbotPermissions.ViewProfile, "Remove from Discord")]
    [InlineData("kick", "vrchat", "discord", ModbotPermissions.DiscordKick | ModbotPermissions.ViewProfile, null)]
    public async Task EachPathNeedsTheWebAppsPermissionForWhereItActs(
        string command, string target, string? kickFrom, ModbotPermissions held, string? missing)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, held, ct: ct);
        var recorder = new Recorder();

        var named = target switch
        {
            "vrchat" => ("vrchat", Linked),
            "linked" => ("member", LinkedMember),
            _ => ("member", UnlinkedMember),
        };

        var options = kickFrom is null ? new[] { named } : new[] { named, ("from", kickFrom) };

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleCommandAsync(Slash(Caller, command, recorder, options), null, ct);

        if (missing is null)
        {
            Assert.Null(reply);
            Assert.Single(recorder.Forms);
        }
        else
        {
            Assert.Equal($"You need the \"{missing}\" permission in Modbot to use /{command}.", reply?.Text);
            Assert.Empty(recorder.Forms);
            Assert.Equal("no-permission", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public async Task ThePermission_IsCheckedAgainWhenTheFormIsSent_AndWhenTheConfirmationIsPressed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordBan, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        // The role is taken away between the command and the form.
        await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", UnlinkedMember)), null, ct);
        var form = recorder.Forms.Single();
        await ChangePermissionsAsync(services, account, ModbotPermissions.ViewProfile, ct);

        var atForm = await handler.HandleFormAsync(Submit(Caller, form.Id, ("why", ["Spam"])), null, ct);
        Assert.Equal("You need the \"Ban on Discord\" permission in Modbot.", atForm.Text);
        Assert.Null(atForm.Actions);

        // Given back for the form, taken away again before the press.
        await ChangePermissionsAsync(services, account, ModbotPermissions.DiscordBan, ct);
        var confirmation = await handler.HandleFormAsync(Submit(Caller, form.Id, ("why", ["Spam"])), null, ct);
        Assert.NotNull(confirmation.Actions);

        await ChangePermissionsAsync(services, account, ModbotPermissions.ViewProfile, ct);
        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions![0].Id, recorder), null, ct);

        Assert.Equal("You need the \"Ban on Discord\" permission in Modbot.", recorder.Updates[^1].Text);
        Assert.Empty(services.Staff.DiscordBans);
    }

    // ── A 429 on groups.moderate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AVRChatRateLimit_IsReportedAsVRChatSaidIt_AndTheBotAsksOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban, ct: ct);
        services.Staff.Answer = new StaffActionAnswer(false, "VRChat rate limited this request.");
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("member", LinkedMember)), null, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);
        var yes = confirmation.Actions![0].Id;

        await handler.HandleButtonAsync(Press(Caller, yes, recorder), null, ct);

        Assert.Equal("VRChat refused: VRChat rate limited this request.", recorder.Updates[^1].Text);
        Assert.Equal("failed", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());

        // Pressing again is the same confirmation: the service answers from its key, and the bot
        // does not turn it into a second try.
        await handler.HandleButtonAsync(Press(Caller, yes, recorder), null, ct);
        Assert.Single(services.Staff.Sent);
    }

    // ── /kick ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KickingADiscordMember_RemovesThemFromTheServer_ByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.DiscordKick, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        // Even a member linked to VRChat: the target decides, and "from" was not given.
        Assert.Null(await handler.HandleCommandAsync(Slash(Caller, "kick", recorder, ("member", LinkedMember)), null, ct));

        var form = Assert.Single(recorder.Forms);
        Assert.Equal("Kick Casey", form.Title);
        var why = form.Fields.Single(f => f.Id == StaffInteractionHandler.WhyField);
        Assert.False(why.Required);
        Assert.DoesNotContain(form.Fields, f => f.Id == StaffInteractionHandler.DeleteDaysField);

        var confirmation = await handler.HandleFormAsync(Submit(Caller, form.Id, ("why", ["Raid."])), null, ct);
        Assert.StartsWith("Remove **Casey** from the Discord server?", confirmation.Text, StringComparison.Ordinal);
        Assert.Equal("Remove", confirmation.Actions![0].Label);
        Assert.Equal(DiscordButtonStyle.Danger, confirmation.Actions[0].Style);

        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions[0].Id, recorder), null, ct);

        var kicked = Assert.Single(services.Staff.DiscordKicks);
        Assert.Equal((LinkedMember, "Raid."), (kicked.DiscordUserId, kicked.Reason));
        Assert.Empty(services.Staff.Sent);
        Assert.Equal("Removing **Casey**…", recorder.Updates[0].Text);
        Assert.Equal("Removed **Casey** from the Discord server.", recorder.Updates[^1].Text);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal("done", fact.GetProperty("outcome").GetString());
        Assert.Equal(LinkedMember, fact.GetProperty("targetDiscord").GetString());
        Assert.False(fact.TryGetProperty("target", out _));
    }

    [Fact]
    public async Task KickingAVRChatPerson_IsTheGroupsKick_WithTheGroupsReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Kick | ModbotPermissions.ViewProfile, ct: ct);
        services.Staff.ReasonRequired = false;
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        Assert.Null(await handler.HandleCommandAsync(Slash(Caller, "kick", recorder, ("vrchat", "wanda")), null, ct));

        var form = Assert.Single(recorder.Forms);
        Assert.Equal("Kick wanda", form.Title);
        Assert.False(form.Fields.Single(f => f.Id == StaffInteractionHandler.ReasonsField).Required);

        var confirmation = await handler.HandleFormAsync(Submit(Caller, form.Id, ("reasons", [""]), ("note", ["Loud."])), null, ct);
        Assert.StartsWith("Kick **wanda** from the group?", confirmation.Text, StringComparison.Ordinal);
        Assert.Equal("Kick", confirmation.Actions![0].Label);

        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions[0].Id, recorder), null, ct);

        var sent = Assert.Single(services.Staff.Sent);
        Assert.Equal(("kick", Stranger), (sent.Action, sent.UserId));
        Assert.Empty(services.Staff.DiscordKicks);
        Assert.Equal("Kicked **wanda**.", recorder.Updates[^1].Text);
    }

    [Fact]
    public async Task KickingFromBoth_IsOneConfirmation_ThatActsOnceInEachPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.Kick | ModbotPermissions.DiscordKick, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        Assert.Null(await handler.HandleCommandAsync(
            Slash(Caller, "kick", recorder, ("member", LinkedMember), ("from", DiscordCommands.FromBoth)), null, ct));

        // The group's form, because the group is one of the places.
        var form = Assert.Single(recorder.Forms);
        Assert.Contains(form.Fields, f => f.Id == StaffInteractionHandler.ReasonsField);

        var harassment = services.Staff.Reasons[0].Id;
        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, form.Id, ("reasons", [harassment.ToString("N")]), ("note", ["Loud."])), null, ct);

        Assert.StartsWith(
            "Kick **Casey** from the VRChat group and remove them from the Discord server?", confirmation.Text, StringComparison.Ordinal);
        Assert.Equal("Kick and remove", confirmation.Actions![0].Label);

        // The group's check and the server's check both ran before the confirmation.
        // The group's check ran once; the server's ran when the command did and again when the form was sent.
        Assert.Single(services.Staff.Checks);
        Assert.Equal(2, services.Staff.DiscordChecks.Count(c => c.DiscordUserId == LinkedMember && c.Action == "kick"));

        var yes = confirmation.Actions[0].Id;
        await handler.HandleButtonAsync(Press(Caller, yes, recorder), null, ct);
        await handler.HandleButtonAsync(Press(Caller, yes, recorder), null, ct);

        Assert.Equal("kick", Assert.Single(services.Staff.Sent).Action);
        Assert.Equal(Linked, services.Staff.Sent[0].UserId);
        Assert.Equal(account.Id, services.Staff.Sent[0].By.UserId);
        Assert.Equal(1, services.Staff.Runs);

        // The reasons and the note go to Discord's audit log in words.
        var kicked = Assert.Single(services.Staff.DiscordKicks);
        Assert.Equal((LinkedMember, "Harassment: Loud."), (kicked.DiscordUserId, kicked.Reason));

        Assert.Equal("Kicked **Casey**.\nRemoved **Casey** from the Discord server.", recorder.Updates[^1].Text);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal(Linked, fact.GetProperty("target").GetString());
        Assert.Equal(LinkedMember, fact.GetProperty("targetDiscord").GetString());
    }

    [Fact]
    public async Task WhenOnePlaceFails_TheOtherIsStillDone_AndEachSaysWhatHappened()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Kick | ModbotPermissions.DiscordKick, ct: ct);
        services.Staff.Answer = new StaffActionAnswer(false, "VRChat rate limited this request.");
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleCommandAsync(Slash(Caller, "kick", recorder, ("member", LinkedMember), ("from", "both")), null, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);
        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions![0].Id, recorder), null, ct);

        Assert.Equal(
            "VRChat refused: VRChat rate limited this request.\nRemoved **Casey** from the Discord server.",
            recorder.Updates[^1].Text);
        Assert.Equal("partial", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
        Assert.Equal(1, services.Staff.Runs);

        // Discord saying no is shown as Discord's sentence, not as VRChat's.
        services.Staff.Answer = new StaffActionAnswer(true, null);
        services.Staff.DiscordAnswer = new StaffActionAnswer(false, "The bot may not remove that person.");

        var again = new Recorder();
        await handler.HandleCommandAsync(Slash(Caller, "kick", again, ("member", LinkedMember), ("from", "both")), null, ct);
        var second = await handler.HandleFormAsync(
            Submit(Caller, again.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);
        await handler.HandleButtonAsync(Press(Caller, second.Actions![0].Id, again), null, ct);

        Assert.Equal(
            "Kicked **Casey**.\nDiscord did not do it: The bot may not remove that person.",
            again.Updates[^1].Text);
    }

    [Fact]
    public async Task AskingForAPlaceThePersonHasNoAccountIn_IsRefusedInPlainWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Kick | ModbotPermissions.DiscordKick | ModbotPermissions.ViewProfile, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        var noVRChat = await handler.HandleCommandAsync(
            Slash(Caller, "kick", recorder, ("member", UnlinkedMember), ("from", "vrchat")), null, ct);
        var noBoth = await handler.HandleCommandAsync(
            Slash(Caller, "kick", recorder, ("member", UnlinkedMember), ("from", "both")), null, ct);
        var noDiscord = await handler.HandleCommandAsync(
            Slash(Caller, "kick", recorder, ("vrchat", Stranger), ("from", "discord")), null, ct);

        Assert.Equal(StaffInteractionHandler.NoLinkedVRChatMessage, noVRChat?.Text);
        Assert.Equal(StaffInteractionHandler.NoLinkedVRChatMessage, noBoth?.Text);
        Assert.Equal(StaffInteractionHandler.NoLinkedDiscordMessage, noDiscord?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("invalid", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    // ── See profiles: a VRChat name is a profile's ──────────────────────────────────────────

    [Theory]
    [InlineData("ban")]
    [InlineData("kick")]
    public async Task WithoutSeeProfiles_TheVRChatOptionTakesAnExactIdOnly_AndEveryRefusalIsOneSentence(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban | ModbotPermissions.Kick, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        // A real name, a near miss, part of a name, and an id Modbot has never seen.
        var refusals = new List<string?>();
        foreach (var typed in new[] { "jessie", "Jessie", "jesie", "jess", "usr_00000000-0000-0000-0000-000000000000" })
            refusals.Add((await handler.HandleCommandAsync(Slash(Caller, command, recorder, ("vrchat", typed)), null, ct))?.Text);

        Assert.All(refusals, text => Assert.Equal(StaffCommands.NeedsAnIdMessage, text));
        Assert.Empty(recorder.Forms);
        Assert.Empty(services.Staff.Checks);

        // Nothing typed is quoted back, and no name of anybody appears.
        Assert.All(refusals, text => Assert.DoesNotContain("jess", text, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ban")]
    [InlineData("kick")]
    public async Task WithoutSeeProfiles_AnExactIdWorks_ButTheFormAndTheConfirmationNameNoOne(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban | ModbotPermissions.Kick, ct: ct);
        services.Staff.ReasonRequired = false;
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        Assert.Null(await handler.HandleCommandAsync(Slash(Caller, command, recorder, ("vrchat", Linked)), null, ct));

        var form = Assert.Single(recorder.Forms);
        Assert.Contains(Linked, form.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("jessie", form.Title, StringComparison.Ordinal);

        var confirmation = await handler.HandleFormAsync(
            Submit(Caller, form.Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")]), ("note", [""])), null, ct);

        Assert.Contains($"**{CardText.EscapeName(Linked)}**", confirmation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("jessie", confirmation.Text, StringComparison.Ordinal);

        await handler.HandleButtonAsync(Press(Caller, confirmation.Actions![0].Id, recorder), null, ct);
        Assert.DoesNotContain(recorder.Updates, u => u.Text?.Contains("jessie", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task WithSeeProfiles_ANameWorks_AndSeveralMatchesAreListedWithTheirIds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.AddProfileAsync("usr_bbbbbbbb-0000-0000-0000-000000000002", "wanda the second", ct: ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        var several = await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("vrchat", "wanda")), null, ct);
        Assert.StartsWith("Several people match \"wanda\"", several?.Text, StringComparison.Ordinal);
        Assert.Contains(Stranger, several!.Text, StringComparison.Ordinal);
        Assert.Empty(recorder.Forms);

        Assert.Null(await handler.HandleCommandAsync(Slash(Caller, "ban", recorder, ("vrchat", "jessie")), null, ct));
        Assert.Equal("Ban jessie", Assert.Single(recorder.Forms).Title);
    }

    [Fact]
    public async Task TheVRChatSuggestions_NeedSeeProfiles_AndThePermissionForTheVRChatPerson()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await SeedPeopleAsync(services, ct);
        await services.LinkedAccountAsync("101", ModbotPermissions.Ban, ct: ct);
        await services.LinkedAccountAsync("102", ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct: ct);
        await services.LinkedAccountAsync("103", ModbotPermissions.Kick | ModbotPermissions.ViewProfile, ct: ct);
        await services.LinkedAccountAsync("104", ModbotPermissions.DiscordBan | ModbotPermissions.DiscordKick | ModbotPermissions.ViewProfile, ct: ct);

        async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(string command, string option, string caller)
        {
            using var scope = services.Scope();
            return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(
                new DiscordSuggestionAsk(caller, command, option, "jess", (_, _) => Task.CompletedTask), ct);
        }

        // Without See profiles: nothing, not even the ones the caller could ban.
        Assert.Empty(await SuggestAsync("ban", "vrchat", "101"));

        // With it: the name to read and the id to fill the option with.
        var suggestion = Assert.Single(await SuggestAsync("ban", "vrchat", "102"));
        Assert.Equal(new DiscordSuggestion("jessie", Linked), suggestion);
        Assert.Single(await SuggestAsync("kick", "vrchat", "103"));

        // Each command suggests only to callers it would answer for that person.
        Assert.Empty(await SuggestAsync("ban", "vrchat", "103"));
        Assert.Empty(await SuggestAsync("kick", "vrchat", "102"));
        Assert.Empty(await SuggestAsync("ban", "vrchat", "104"));
        Assert.Empty(await SuggestAsync("ban", "vrchat", "999"));
        Assert.Empty(await SuggestAsync("ban", "member", "102"));
        Assert.Empty(await SuggestAsync("kick", "from", "103"));
    }
}
