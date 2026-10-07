using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Interactions;

/// <summary>
/// Acting from Discord: who may open a form, what a form writes, and the confirmation that makes a
/// ban one press of a red button and never two bans (acting from Discord design).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffInteractionHandlerTests
{
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Member = "445566778899";
    private const string CardChannel = "1234567890";
    private const string CardMessage = "9876543210";

    private readonly PostgresFixture _db;

    public StaffInteractionHandlerTests(PostgresFixture db) => _db = db;

    /// <summary>A press that records what the handler did with it.</summary>
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

    private static DiscordCommandCall Menu(string discordUserId, string menu, Recorder recorder, DiscordTargetUser? user = null)
        => new(discordUserId, "moderator", menu, new Dictionary<string, string>(), recorder.Reply, recorder.Show)
        {
            Kind = DiscordCommandKind.User,
            TargetUser = user,
        };

    private static DiscordButtonPress Press(string discordUserId, string buttonId, Recorder recorder, bool onCard = false)
        => new(discordUserId, "moderator", buttonId, recorder.Reply, recorder.Show, recorder.Update)
        {
            CardChannelId = onCard ? CardChannel : null,
            CardMessageId = onCard ? CardMessage : null,
        };

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

    // ── Who may ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnlinkedDiscordUser_IsToldToConnect_AndNoFormOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleMenuAsync(
            Menu("999", StaffMenus.AddNote, recorder, new DiscordTargetUser(Member, "someone", false)), null, ct);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, reply?.Text);
        Assert.Empty(recorder.Forms);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal("not-linked", fact.GetProperty("outcome").GetString());
        Assert.Equal(StaffMenus.AddNote, fact.GetProperty("command").GetString());
        Assert.Equal(Member, fact.GetProperty("target").GetString());
    }

    [Fact]
    public async Task WithoutWriteNotes_TheNoteMenuNamesThePermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleMenuAsync(
            Menu("100", StaffMenus.AddNote, recorder, new DiscordTargetUser(Member, "someone", false)), null, ct);

        Assert.Equal("You need the \"Write notes\" permission in Modbot.", reply?.Text);
        Assert.Empty(recorder.Forms);
    }

    [Fact]
    public async Task AnAccountWithNoVRChatLink_MayNotWrite_AsTheWebAppRefusesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "unlinked_mod", TestAccounts.Password, ModbotPermissions.WriteNotes, linked: false, ct);
            user.DiscordUserId = "100";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var recorder = new Recorder();
        using var scope = services.Scope();
        var reply = await Handler(scope).HandleMenuAsync(
            Menu("100", StaffMenus.AddNote, recorder, new DiscordTargetUser(Member, "someone", false)), null, ct);

        Assert.Equal(StaffInteractionHandler.NeedsVRChatMessage, reply?.Text);
        Assert.Equal("no-vrchat", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ADisabledAccount_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Administrator, disabled: true, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleButtonAsync(
            Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder), null, ct);

        Assert.Equal("Your Modbot account is disabled.", reply?.Text);
        Assert.Empty(recorder.Forms);
    }

    // ── Notes ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddANote_OpensAForm_AndTheFormWritesTheNoteAboutTheDiscordAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync("100", ModbotPermissions.WriteNotes, ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = "https://modbot.example.com", ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        var opened = await handler.HandleMenuAsync(
            Menu("100", StaffMenus.AddNote, recorder, new DiscordTargetUser(Member, "someone", false)), new FakeGateway(), ct);

        Assert.Null(opened);
        var form = Assert.Single(recorder.Forms);
        Assert.Equal(StaffMenus.NoteFormFor(onDiscord: true, Member), form.Id);
        Assert.StartsWith(DiscordActionButton.Prefix, form.Id, StringComparison.Ordinal);

        var reply = await handler.HandleFormAsync(Submit("100", form.Id, ("note", ["  Spamming invites in #general.  "])), null, ct);

        Assert.Equal(StaffInteractionHandler.NoteAddedMessage, reply.Text);
        Assert.Equal(
            $"https://modbot.example.com/discord/members?subject=discord-person%3A{Member}",
            Assert.Single(reply.Links!).Url);

        var note = Assert.Single(services.Staff.Notes);
        Assert.Equal(FactPlatform.Discord, note.Platform);
        Assert.Equal(Member, note.UserId);
        Assert.Equal("Spamming invites in #general.", note.Text);
        Assert.Equal(account.Id, note.By.UserId);

        var fact = await LastCommandFactAsync(services, ct);
        Assert.Equal("note", fact.GetProperty("command").GetString());
        Assert.Equal("answered", fact.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnEmptyNote_IsRefused_AndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.WriteNotes, ct: ct);

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleFormAsync(
            Submit("100", StaffMenus.NoteFormFor(onDiscord: false, Target), ("note", ["   "])), null, ct);

        Assert.Equal(StaffInteractionHandler.EmptyNoteMessage, reply.Text);
        Assert.Empty(services.Staff.Notes);
    }

    [Fact]
    public async Task TheNoteFormIsCheckedAgain_WhenItIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        // Somebody who never had the permission sends a form id they made up.
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleFormAsync(
            Submit("100", StaffMenus.NoteFormFor(onDiscord: false, Target), ("note", ["hello"])), null, ct);

        Assert.Equal("You need the \"Write notes\" permission in Modbot.", reply.Text);
        Assert.Empty(services.Staff.Notes);
    }

    [Fact]
    public async Task ANoteAboutTheBotItself_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.WriteNotes, ct: ct);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleMenuAsync(
            Menu("100", StaffMenus.AddNote, recorder, new DiscordTargetUser(gateway.BotUserId!, "Modbot", true)), gateway, ct);

        Assert.Equal(StaffInteractionHandler.OwnAccountMessage, reply?.Text);
        Assert.Empty(recorder.Forms);
    }

    [Fact]
    public async Task AFormThatComesTooLate_SaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.WriteNotes, ct: ct);
        var recorder = new Recorder { FormsTooLate = true };

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleButtonAsync(
            Press("100", StaffMenus.NoteButtonFor(onDiscord: false, Target), recorder), null, ct);

        Assert.Equal(StaffInteractionHandler.TooLateMessage, reply?.Text);
    }

    // ── Ban: form, confirmation, once ────────────────────────────────────────────────────────

    [Fact]
    public async Task Ban_IsAFormThenAConfirmation_AndTheConfirmationActsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        // The card's button opens the form, with the group's reasons, one required.
        Assert.Null(await handler.HandleButtonAsync(
            Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder, onCard: true), gateway, ct));

        var form = Assert.Single(recorder.Forms);
        Assert.Equal("Ban jessie", form.Title);
        var reasons = form.Fields.Single(f => f.Id == StaffInteractionHandler.ReasonsField);
        Assert.True(reasons.Required);
        Assert.Equal(new[] { "Harassment", "Other" }, reasons.Choices!.Select(c => c.Label));
        Assert.Empty(services.Staff.Sent);

        // Sending the form checks it and asks for the confirmation; still nothing sent.
        var harassment = services.Staff.Reasons[0].Id;
        var confirmation = await handler.HandleFormAsync(
            Submit("100", form.Id, ("reasons", [harassment.ToString("N")]), ("note", ["Repeated slurs in voice."])), gateway, ct);

        Assert.StartsWith("Ban **jessie** from the group?", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains("Reasons: Harassment", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains("Note: Repeated slurs in voice.", confirmation.Text, StringComparison.Ordinal);
        var yes = confirmation.Actions![0];
        Assert.Equal("Ban", yes.Label);
        Assert.Equal(DiscordButtonStyle.Danger, yes.Style);
        Assert.Equal("Cancel", confirmation.Actions[1].Label);
        Assert.Empty(services.Staff.Sent);
        Assert.Equal(new[] { harassment }, services.Staff.Checks.Single().ReasonIds);

        // The red button: the confirmation is rewritten, the ban is sent once, the card is marked.
        Assert.Null(await handler.HandleButtonAsync(Press("100", yes.Id, recorder), gateway, ct));

        Assert.Equal("Banning **jessie**…", recorder.Updates[0].Text);
        Assert.Empty(recorder.Updates[0].Actions ?? []);
        Assert.Equal("Banned **jessie**.", recorder.Updates[^1].Text);

        var sent = Assert.Single(services.Staff.Sent);
        Assert.Equal("ban", sent.Action);
        Assert.Equal(Target, sent.UserId);
        Assert.Equal(new[] { harassment }, sent.ReasonIds);
        Assert.Equal("Repeated slurs in voice.", sent.Note);
        Assert.Equal(account.Id, sent.By.UserId);
        Assert.StartsWith("discord:", sent.Key, StringComparison.Ordinal);

        var handled = Assert.Single(gateway.Handled);
        Assert.Equal((CardChannel, CardMessage), (handled.ChannelId, handled.MessageId));
        Assert.Equal(StaffMenus.ActButton, handled.RemoveButtonsStarting);
        Assert.Contains("Banned by", handled.Line, StringComparison.Ordinal);

        Assert.Equal("done", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());

        // A second press of the same confirmation reaches the service with the same key and gets
        // the first answer back: one ban, one card line.
        await handler.HandleButtonAsync(Press("100", yes.Id, recorder), gateway, ct);

        Assert.Equal(2, services.Staff.Runs);
        Assert.Single(services.Staff.Sent);
        Assert.Single(gateway.Handled);
        Assert.Equal("repeat", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    /// <summary>
    /// A card somebody acted on takes no more repeats: folding one in rewrites the message whole,
    /// which would take the "Banned by" line away and bring back the buttons the ban took off.
    /// </summary>
    [Fact]
    public async Task ACardSomebodyActedOn_TakesNoMoreRepeats()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        // The card is the post the channel's next repeat would be written into.
        await using (var db = services.Database.NewContext())
        {
            db.DiscordEventChannels.Add(new DiscordEventChannel { ChannelId = CardChannel, RepeatPostId = CardMessage });
            await db.SaveChangesAsync(ct);
        }

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(
            Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder, onCard: true), gateway, ct);
        var form = Assert.Single(recorder.Forms);

        var confirmation = await handler.HandleFormAsync(
            Submit("100", form.Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")]), ("note", [""])), gateway, ct);
        await handler.HandleButtonAsync(Press("100", confirmation.Actions![0].Id, recorder), gateway, ct);

        Assert.Single(services.Staff.Sent);
        Assert.Single(gateway.Handled);
        Assert.Null((await services.ChannelPlaceAsync(CardChannel, ct))!.RepeatPostId);
    }

    [Fact]
    public async Task WithoutTheBanPermission_TheButtonOpensNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Kick | ModbotPermissions.WriteNotes, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleButtonAsync(
            Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder, onCard: true), null, ct);

        Assert.Equal("You need the \"Ban\" permission in Modbot.", reply?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnAccountDisabled_BeforeTheConfirmation_SendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync("100", ModbotPermissions.Kick, ct: ct);
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Kick, Target), recorder, onCard: true), gateway, ct);
        var form = Assert.Single(recorder.Forms);
        var confirmation = await handler.HandleFormAsync(
            Submit("100", form.Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), gateway, ct);

        // An administrator disables the account between the form and the confirmation.
        await using (var db = services.Database.NewContext())
        {
            var user = await db.Users.FindAsync([account.Id], ct);
            user!.IsDisabled = true;
            await db.SaveChangesAsync(ct);
        }

        await handler.HandleButtonAsync(Press("100", confirmation.Actions![0].Id, recorder), gateway, ct);

        Assert.Equal(2, recorder.Updates.Count);
        Assert.Equal("Your Modbot account is disabled.", recorder.Updates[^1].Text);
        Assert.Empty(services.Staff.Sent);
        Assert.Empty(gateway.Handled);
    }

    [Fact]
    public async Task ARefusedCheck_IsSaidBeforeTheConfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        services.Staff.Refusal = "\"Other\" needs a note saying what happened.";
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder), null, ct);
        var reply = await handler.HandleFormAsync(
            Submit("100", recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[1].Id.ToString("N")])), null, ct);

        Assert.Equal("\"Other\" needs a note saying what happened.", reply.Text);
        Assert.Null(reply.Actions);
        Assert.Empty(services.Staff.Sent);
    }

    [Fact]
    public async Task OnlyThePersonWhoStartedIt_MayConfirmIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.Ban, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder), null, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit("100", recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);

        var reply = await handler.HandleButtonAsync(Press("200", confirmation.Actions![0].Id, recorder), null, ct);

        Assert.Equal(StaffInteractionHandler.NotYoursMessage, reply?.Text);
        Assert.Empty(services.Staff.Sent);
    }

    [Fact]
    public async Task Cancel_SendsNothing_AndTheConfirmationRunsOut()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder), null, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit("100", recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);

        await handler.HandleButtonAsync(Press("100", confirmation.Actions![1].Id, recorder), null, ct);
        Assert.Equal(StaffInteractionHandler.CancelledMessage, recorder.Updates[^1].Text);

        await handler.HandleButtonAsync(Press("100", confirmation.Actions[0].Id, recorder), null, ct);
        Assert.Equal(StaffInteractionHandler.RunOutMessage, recorder.Updates[^1].Text);
        Assert.Empty(services.Staff.Sent);
    }

    [Fact]
    public async Task AKickWithTheReasonListLeftEmpty_GoesToTheConfirmation_WhenNoneIsRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Kick, ct: ct);
        services.Staff.ReasonRequired = false;
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Kick, Target), recorder), null, ct);
        var form = recorder.Forms.Single();
        Assert.False(form.Fields.Single(f => f.Id == StaffInteractionHandler.ReasonsField).Required);

        // Discord can send an empty list as one empty value.
        var confirmation = await handler.HandleFormAsync(Submit("100", form.Id, ("reasons", [""]), ("note", [""])), null, ct);

        Assert.StartsWith("Kick **", confirmation.Text, StringComparison.Ordinal);
        Assert.NotNull(confirmation.Actions);
        Assert.Empty(services.Staff.Checks.Single().ReasonIds);
    }

    [Fact]
    public async Task AFormWithNoReasonListAtAll_IsNoReasonsPicked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.AnswerJoinRequests, ct: ct);
        services.Staff.ReasonRequired = false;
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Reject, Target), recorder), null, ct);

        // Only the note came back: no "reasons" key at all.
        var confirmation = await handler.HandleFormAsync(Submit("100", recorder.Forms.Single().Id, ("note", ["Not in the region."])), null, ct);

        Assert.NotNull(confirmation.Actions);
        Assert.Empty(services.Staff.Checks.Single().ReasonIds);
        Assert.Equal("Not in the region.", services.Staff.Checks.Single().Note);
    }

    [Fact]
    public async Task AYesWithATokenNothingIsWaitingFor_SendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        // A token nobody made.
        await handler.HandleButtonAsync(Press("100", StaffMenus.YesButton + PendingStaffActions.NewToken(), recorder), new FakeGateway(), ct);
        Assert.Equal(StaffInteractionHandler.RunOutMessage, recorder.Updates[^1].Text);

        // The token of a ban whose form was never sent: held, but not ready to confirm.
        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder), null, ct);
        var token = StaffMenus.TokenAfter(recorder.Forms.Single().Id, StaffMenus.ActForm)!;
        await handler.HandleButtonAsync(Press("100", StaffMenus.YesButton + token, recorder), new FakeGateway(), ct);
        Assert.Equal(StaffInteractionHandler.RunOutMessage, recorder.Updates[^1].Text);

        Assert.Equal(0, services.Staff.Runs);
    }

    [Fact]
    public void ACut_NeverSplitsACharacterWrittenAsTwoHalves()
    {
        var text = new string('a', 3) + "\U0001F600" + "b";

        Assert.Equal("aaa", StaffInteractionHandler.Cut(text, 4));
        Assert.Equal("aaa\U0001F600", StaffInteractionHandler.Cut(text, 5));
        Assert.Equal(text, StaffInteractionHandler.Cut(text, 50));
    }

    [Fact]
    public async Task AConfirmation_RunsOutAfterFifteenMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Ban, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Ban, Target), recorder), null, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit("100", recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), null, ct);

        services.Clock.Advance(PendingStaffActions.Lifetime);

        await handler.HandleButtonAsync(Press("100", confirmation.Actions![0].Id, recorder), null, ct);
        Assert.Equal(StaffInteractionHandler.RunOutMessage, recorder.Updates[^1].Text);
        Assert.Empty(services.Staff.Sent);
    }

    [Fact]
    public async Task Approve_TakesNoForm_AndGoesStraightToTheConfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.AnswerJoinRequests, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        var confirmation = await handler.HandleButtonAsync(
            Press("100", StaffMenus.ActButtonFor(StaffActionWords.Approve, Target), recorder, onCard: true), new FakeGateway(), ct);

        Assert.Empty(recorder.Forms);
        Assert.Equal("Let **jessie** into the group?", confirmation?.Text);
        Assert.Equal(DiscordButtonStyle.Main, confirmation!.Actions![0].Style);
        Assert.Single(services.Staff.Checks);

        await handler.HandleButtonAsync(Press("100", confirmation.Actions[0].Id, recorder), new FakeGateway(), ct);
        Assert.Equal("approve", Assert.Single(services.Staff.Sent).Action);
    }

    [Fact]
    public async Task AFailedAction_SaysWhatVRChatSaid_AndLeavesTheCardAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Kick, ct: ct);
        services.Staff.Answer = new StaffActionAnswer(false, "VRChat says they are not in the group.");
        var gateway = new FakeGateway();
        var recorder = new Recorder();

        using var scope = services.Scope();
        var handler = Handler(scope);

        await handler.HandleButtonAsync(Press("100", StaffMenus.ActButtonFor(StaffActionWords.Kick, Target), recorder, onCard: true), gateway, ct);
        var confirmation = await handler.HandleFormAsync(
            Submit("100", recorder.Forms.Single().Id, ("reasons", [services.Staff.Reasons[0].Id.ToString("N")])), gateway, ct);
        await handler.HandleButtonAsync(Press("100", confirmation.Actions![0].Id, recorder), gateway, ct);

        Assert.Equal("VRChat refused: VRChat says they are not in the group.", recorder.Updates[^1].Text);
        Assert.Empty(gateway.Handled);
        Assert.Equal("failed", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    // ── Look up ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A member Modbot has nothing on gets /lookup's own answer, and the note button.</summary>
    [Fact]
    public async Task LookUp_OnAMemberModbotHasNothingOn_SaysSo_AndOffersANote()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile | ModbotPermissions.WriteNotes | ModbotPermissions.Ban, ct: ct);
        var recorder = new Recorder();

        using var scope = services.Scope();
        var reply = await Handler(scope).HandleMenuAsync(
            Menu("100", StaffMenus.LookUp, recorder, new DiscordTargetUser(Member, "someone", false)), new FakeGateway(), ct);

        Assert.Equal(DiscordCommandHandler.NoDiscordRecordsMessage, reply!.Text);
        Assert.Empty(reply.Embeds);

        // A Discord account with no VRChat link can be noted, not banned from the group.
        var button = Assert.Single(reply.Actions!);
        Assert.Equal(StaffMenus.NoteButtonFor(onDiscord: true, Member), button.Id);
    }

    /// <summary>
    /// An unlinked member is the card /lookup discord: shows, with what the caller may see: their
    /// notes only for somebody who may read the audit log, as the web app has it.
    /// </summary>
    [Fact]
    public async Task LookUp_OnAnUnlinkedMember_IsLookupsCard_AndShowsNotesOnlyToAuditLogReaders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile | ModbotPermissions.WriteNotes, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, ct: ct);

        await using (var db = services.Database.NewContext())
        {
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = "424242",
                UserId = Member,
                Username = "someone",
                DisplayName = "Someone",
                AvatarUrl = $"https://cdn.discordapp.com/avatars/{Member}/a.png",
                FirstSeenAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.NoteAdded,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = Member,
            Source = FactSource.Manual,
            Data = new JsonObject { ["text"] = "Spams invites", ["description"] = "Spams invites" },
        }, ct);

        async Task<DiscordReply> LookUpAsAsync(string caller)
        {
            using var scope = services.Scope();
            return (await Handler(scope).HandleMenuAsync(
                Menu(caller, StaffMenus.LookUp, new Recorder(), new DiscordTargetUser(Member, "someone", false)), new FakeGateway(), ct))!;
        }

        var without = await LookUpAsAsync("100");
        var card = Assert.Single(without.Embeds);
        Assert.Equal("Not linked", card.Fields.Single(f => f.Name == "VRChat").Value);
        Assert.DoesNotContain(card.Fields, f => f.Name == "Notes");
        Assert.DoesNotContain(card.Fields, f => f.Value.Contains("Spams invites", StringComparison.Ordinal));
        Assert.Equal($"https://cdn.discordapp.com/avatars/{Member}/a.png", card.ThumbnailUrl);
        Assert.Equal(StaffMenus.NoteButtonFor(onDiscord: true, Member), Assert.Single(without.Actions!).Id);

        var with = Assert.Single((await LookUpAsAsync("200")).Embeds);
        Assert.Equal("1", with.Fields.Single(f => f.Name == "Notes").Value);
    }

    [Fact]
    public async Task LookUp_OnALinkedMember_IsTheirProfile_WithTheButtonsTheModeratorMayUse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile | ModbotPermissions.Kick, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);

        await using (var db = services.Database.NewContext())
        {
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = Member,
                VRChatUserId = Target,
                LinkedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var recorder = new Recorder();
        using var scope = services.Scope();
        var reply = await Handler(scope).HandleMenuAsync(
            Menu("100", StaffMenus.LookUp, recorder, new DiscordTargetUser(Member, "someone", false)), new FakeGateway(), ct);

        Assert.Equal("jessie", Assert.Single(reply!.Embeds).Title);

        // Kick only: no Write notes, no Ban.
        Assert.Equal(new[] { StaffMenus.ActButtonFor(StaffActionWords.Kick, Target) }, reply.Actions!.Select(a => a.Id));
    }

    // ── What is registered ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two member menus, hidden from members without Timeout Members. No message menu: the staff
    /// one that wrote a note ("Report this message") was dropped before it shipped, for a "Report to
    /// mods" open to everybody (Discord commands design, decision 3).
    /// </summary>
    [Fact]
    public void TheMenus_AreRegistered_HiddenFromMembersWithoutTimeoutMembers()
    {
        var menus = DiscordCommands.All.Where(c => c.Kind == DiscordCommandKind.User).ToList();

        Assert.Equal(new[] { StaffMenus.LookUp, StaffMenus.AddNote }, menus.Select(m => m.Name));
        Assert.All(menus, m => Assert.True(m.StaffOnly));
        Assert.All(menus, m => Assert.Equal(DiscordCommandKind.User, m.Kind));
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == StaffMenus.LookUp);
    }
}
