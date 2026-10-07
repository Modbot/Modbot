using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/event</c> (Discord commands design §3.3, §3.7 and §4, step 7): who may run each step, that
/// the date is checked before it is asked about, that the question and its two buttons work as the
/// design words them, that a press checks the account again and cancels once, and that the event
/// and date suggestions follow the options already filled in.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventCommandTests
{
    private const string Caller = "100";

    private static readonly Guid Movie = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Friday = new(2026, 9, 18, 20, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public EventCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Setting the scene ───────────────────────────────────────────────────────────────────

    private sealed class Recorder
    {
        public List<DiscordReply> Replies { get; } = [];

        public List<DiscordReply> Updates { get; } = [];

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
    }

    private static DiscordCommandCall Slash(string who, string step, params (string Name, string Value)[] options)
        => new(
            who,
            "host",
            DiscordCommands.Event,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            (_, _) => Task.CompletedTask)
        {
            Subcommand = step,
        };

    private static DiscordButtonPress Press(string who, string buttonId, Recorder recorder)
        => new(who, "host", buttonId, recorder.Reply, null, recorder.Update);

    private static string Planned(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Runs the command as the bot does: through the switch, then the handler.</summary>
    private static async Task<DiscordReply?> RunAsync(TestServices services, DiscordCommandCall call)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(call, Ct);
    }

    private static async Task<DiscordReply?> PressAsync(TestServices services, DiscordButtonPress press)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<EventCommand>().HandleButtonAsync(press, Ct);
    }

    private static string? ButtonId(DiscordReply reply, string label)
        => reply.Actions?.SingleOrDefault(a => a.Label == label)?.Id;

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    private static async Task<List<string>> OutcomesAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return [.. facts.Select(f => JsonDocument.Parse(f.Data).RootElement.GetProperty("outcome").GetString()!)];
    }

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, ModbotPermissions held = ModbotPermissions.ManageCalendar | ModbotPermissions.ViewCalendar)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.LinkedAccountAsync(Caller, held, ct: Ct);
        return services;
    }

    private static async Task ChangePermissionsAsync(TestServices services, ModbotUser account, ModbotPermissions permissions)
    {
        await using var db = services.Database.NewContext();
        var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.Id == account.Id, Ct);
        var roleId = await TestAccounts.RoleForAsync(db, permissions, Ct);

        foreach (var role in user.Roles.ToList())
            db.Remove(role);

        db.Add(new ModbotUserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Asks to cancel one date and returns the question's buttons and what it said.</summary>
    private static async Task<(DiscordReply Question, string Yes, string No)> AskToCancelAsync(
        TestServices services, bool sayIt = false)
    {
        var options = new List<(string, string)>
        {
            (DiscordCommands.EventOption, Movie.ToString()),
            (DiscordCommands.EventDateOption, Planned(Friday)),
        };

        if (sayIt)
            options.Add((DiscordCommands.EventSayOption, "true"));

        var question = await RunAsync(services, Slash(Caller, DiscordCommands.EventCancelDate, [.. options]));

        Assert.NotNull(question);
        var yes = ButtonId(question, EventCommand.CancelDateLabel) ?? ButtonId(question, EventCommand.CancelEventLabel);
        return (question, yes!, ButtonId(question, EventCommand.KeepItLabel)!);
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Event_IsAnEventHostsCommand_OnByDefault_PrivateAndNeedsManageCalendar()
    {
        var command = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.Event);

        Assert.Equal(DiscordShownTo.EventHosts, command.ShownTo);
        Assert.Equal(DiscordReplyKind.Private, command.Reply);
        Assert.Equal(DiscordCommandKind.Slash, command.Kind);
        Assert.Empty(command.Options);
        Assert.True(DiscordCommandSwitches.Find(DiscordCommands.Event)?.OnByDefault);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.Event);
        Assert.False(DiscordCommands.IsForEveryone(DiscordCommands.Event));

        Assert.Equal(ModbotPermissions.ManageCalendar, DiscordCommands.Requires(DiscordCommands.Event));
        Assert.Equal(ModbotPermissions.ManageCalendar, DiscordCommands.RequiresFor(DiscordCommands.Event, DiscordCommands.EventOpen));
        Assert.Equal("Manage calendar", DiscordCommands.Label(ModbotPermissions.ManageCalendar));
        Assert.Equal("See calendar", DiscordCommands.Label(ModbotPermissions.ViewCalendar));

        Assert.True(DiscordCommands.Writes(DiscordCommands.Event, DiscordCommands.EventOpen));
        Assert.True(DiscordCommands.Writes(DiscordCommands.Event, DiscordCommands.EventCancelDate));
    }

    [Fact]
    public void Event_HasTwoSteps_InTheDesignsWords()
    {
        var command = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Event);

        Assert.Equal("Calendar events", command.Description);
        Assert.Equal(
            [("open", "Open the instance now"), ("cancel-date", "Cancel one date")],
            command.Subcommands!.Select(s => (s.Name, s.Description)));

        var open = command.Subcommands!.Single(s => s.Name == "open");
        var eventOption = Assert.Single(open.Options);
        Assert.Equal(("event", DiscordOptionKind.Text, true, true), (eventOption.Name, eventOption.Kind, eventOption.Required, eventOption.Suggests));

        var cancel = command.Subcommands!.Single(s => s.Name == "cancel-date");
        Assert.Equal(["event", "date", "say-so"], cancel.Options.Select(o => o.Name));
        Assert.True(cancel.Options[0].Required);
        Assert.True(cancel.Options[1].Required);
        Assert.All(cancel.Options.Take(2), o => Assert.True(o.Suggests));
        Assert.Equal(DiscordOptionKind.YesNo, cancel.Options[2].Kind);
        Assert.False(cancel.Options[2].Required);
        Assert.Equal("Say so in the event's channel", cancel.Options[2].Description);
    }

    [Fact]
    public void Help_NamesBothSteps()
    {
        var command = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Event);

        Assert.Equal(
            [
                "`/event open event:` — Open the instance now",
                "`/event cancel-date event: date: say-so:` — Cancel one date",
            ],
            DiscordCommandHandler.HelpLines(command));
    }

    // ── Who may run it ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiscordCommands.EventOpen)]
    [InlineData(DiscordCommands.EventCancelDate)]
    public async Task AnUnlinkedCaller_ADisabledOne_AndOneWithNoVRChatLink_AreRefusedInThatOrder(string step)
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var options = new[] { (DiscordCommands.EventOption, Movie.ToString()), (DiscordCommands.EventDateOption, Planned(Friday)) };

        var unlinked = await RunAsync(services, Slash("999", step, options));
        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, unlinked?.Text);
        Assert.Equal("not-linked", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        await services.LinkedAccountAsync("101", ModbotPermissions.Administrator, disabled: true, ct: Ct);
        var disabled = await RunAsync(services, Slash("101", step, options));
        Assert.Equal("Your Modbot account is disabled.", disabled?.Text);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "no_vrchat", TestAccounts.Password, ModbotPermissions.Administrator, linked: false, Ct);
            user.DiscordUserId = "102";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var noLink = await RunAsync(services, Slash("102", step, options));
        Assert.Equal(StaffInteractionHandler.NeedsVRChatMessage, noLink?.Text);
        Assert.Equal("no-vrchat", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        Assert.Empty(services.Calendar.Opens);
        Assert.Empty(services.Calendar.Plans);
    }

    [Theory]
    [InlineData(DiscordCommands.EventOpen)]
    [InlineData(DiscordCommands.EventCancelDate)]
    public async Task WithoutManageCalendar_EachStepIsRefusedByName_AndRecorded(string step)
    {
        await using var services = await SetUpAsync(_db, ModbotPermissions.ViewCalendar | ModbotPermissions.Kick);

        var reply = await RunAsync(services, Slash(
            Caller, step, (DiscordCommands.EventOption, Movie.ToString()), (DiscordCommands.EventDateOption, Planned(Friday))));

        Assert.Equal("You need the \"Manage calendar\" permission in Modbot to use /event.", reply?.Text);
        Assert.Empty(services.Calendar.Opens);
        Assert.Empty(services.Calendar.Plans);

        var fact = await LastCommandFactAsync(services);
        Assert.Equal("no-permission", fact.GetProperty("outcome").GetString());
        Assert.Equal("event", fact.GetProperty("command").GetString());
        Assert.Equal(step, fact.GetProperty("subcommand").GetString());
    }

    [Fact]
    public async Task ACommandSwitchedOff_IsRefused()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Event, false), Ct);

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.EventOpen, (DiscordCommands.EventOption, Movie.ToString())));

        Assert.Equal("/event is turned off on this server.", reply?.Text);
        Assert.Empty(services.Calendar.Opens);
        Assert.Equal("off", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Help_ListsEvent_ForSomebodyWhoManagesTheCalendar_AndNotForOthers()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync("200", ModbotPermissions.WriteNotes, ct: Ct);

        async Task<string> HelpAsync(string who)
        {
            using var scope = services.Scope();
            var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(
                new DiscordCommandCall(who, "x", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask), Ct);
            return reply.Text!;
        }

        Assert.Contains("`/event open event:` — Open the instance now", await HelpAsync(Caller), StringComparison.Ordinal);
        Assert.DoesNotContain("/event", await HelpAsync("200"), StringComparison.Ordinal);
    }

    // ── Open now ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_OpensTheEventsInstance_AsTheAccount_AndSaysSo()
    {
        await using var services = await SetUpAsync(_db);
        var account = await services.LinkedAccountAsync("300", ModbotPermissions.ManageCalendar, ct: Ct);

        var reply = await RunAsync(services, Slash("300", DiscordCommands.EventOpen, (DiscordCommands.EventOption, Movie.ToString())));

        Assert.Equal("Opened **Movie night**.", reply?.Text);

        var (eventId, by) = Assert.Single(services.Calendar.Opens);
        Assert.Equal(Movie, eventId);
        Assert.Equal(account.Id, by.UserId);
        Assert.Equal(account.Username, by.Username);

        var fact = await LastCommandFactAsync(services);
        Assert.Equal("answered", fact.GetProperty("outcome").GetString());
        Assert.Equal("open", fact.GetProperty("subcommand").GetString());
    }

    [Theory]
    [InlineData("That event does not exist.")]
    [InlineData("Pick a managed group first.")]
    [InlineData("The event has no world.")]
    [InlineData("It is too early to open the instance.")]
    [InlineData("That event has already ended.")]
    [InlineData("The instance is already open.")]
    [InlineData("Checking whether VRChat opened the instance.")]
    [InlineData("VRChat refused: The world is not available.")]
    public async Task Open_ShowsTheCalendarsRefusal_InItsOwnWords(string words)
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.OpenAnswer = new CalendarOpenAnswer(false, words, "Movie night");

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.EventOpen, (DiscordCommands.EventOption, Movie.ToString())));

        Assert.Equal(words, reply?.Text);
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Open_EscapesWhatVRChatSaid_AndAnEventTitle()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.OpenAnswer = new CalendarOpenAnswer(false, "VRChat refused: **@everyone** [x](http://y)", "*Movie*");

        var refused = await RunAsync(services, Slash(Caller, DiscordCommands.EventOpen, (DiscordCommands.EventOption, Movie.ToString())));
        Assert.DoesNotContain("**@everyone**", refused?.Text, StringComparison.Ordinal);
        Assert.Contains("\\*\\*@everyone\\*\\*", refused?.Text, StringComparison.Ordinal);

        services.Calendar.OpenAnswer = new CalendarOpenAnswer(true, string.Empty, "*Movie*");
        var opened = await RunAsync(services, Slash(Caller, DiscordCommands.EventOpen, (DiscordCommands.EventOption, Movie.ToString())));
        Assert.Equal("Opened **\\*Movie\\***.", opened?.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an id")]
    public async Task AnEventThatIsNotOnTheList_IsAskedAgain_AndNothingIsOpened(string typed)
    {
        await using var services = await SetUpAsync(_db);

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.EventOpen, (DiscordCommands.EventOption, typed)));

        Assert.Equal(EventCommand.PickEventMessage, reply?.Text);
        Assert.Empty(services.Calendar.Opens);
        Assert.Equal("invalid", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    // ── Cancel one date: the question ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelDate_AsksFirst_WithTheDesignsWords_AndCancelsNothing()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Plan = new CalendarCancelPlan(true, null, "Movie night", Friday, WholeEvent: false);

        var (question, yes, no) = await AskToCancelAsync(services);

        Assert.Equal($"Cancel **Movie night** on <t:{Friday.ToUnixTimeSeconds()}:f>?", question.Text);
        Assert.Equal(["Cancel this date", "Keep it"], question.Actions!.Select(a => a.Label));
        Assert.Equal(DiscordButtonStyle.Danger, question.Actions![0].Style);
        Assert.Equal(DiscordButtonStyle.Plain, question.Actions![1].Style);
        Assert.StartsWith("modbot:event:yes:", yes, StringComparison.Ordinal);
        Assert.StartsWith("modbot:event:no:", no, StringComparison.Ordinal);
        Assert.All(question.Actions!, a => Assert.True(a.Id.Length <= StaffMenus.MaxIdLength));

        // Asked, not done.
        Assert.Empty(services.Calendar.CancelCalls);
        var (eventId, planned, sayIt, _) = Assert.Single(services.Calendar.Plans);
        Assert.Equal((Movie, Friday, false), (eventId, planned, sayIt));

        var fact = await LastCommandFactAsync(services);
        Assert.Equal("asked", fact.GetProperty("outcome").GetString());
        Assert.Equal("cancel-date", fact.GetProperty("subcommand").GetString());
    }

    [Fact]
    public async Task CancelDate_PassesSayingSoOn_ToTheChecks_AndToTheCancel()
    {
        await using var services = await SetUpAsync(_db);

        var (_, yes, _) = await AskToCancelAsync(services, sayIt: true);
        Assert.True(Assert.Single(services.Calendar.Plans).SayIt);

        await PressAsync(services, Press(Caller, yes, new Recorder()));
        Assert.True(Assert.Single(services.Calendar.Cancelled).SayIt);
    }

    [Fact]
    public async Task AnEventThatIsTheOnlyDate_IsCancelledWhole_AndTheButtonSaysSo()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Plan = new CalendarCancelPlan(true, null, "Movie night", Friday, WholeEvent: true);

        var (question, yes, _) = await AskToCancelAsync(services);

        Assert.Equal(["Cancel the event", "Keep it"], question.Actions!.Select(a => a.Label));

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("Cancelled **Movie night**.", recorder.Updates[^1].Text);
    }

    [Theory]
    [InlineData("That date is cancelled already.")]
    [InlineData("That date has already ended.")]
    [InlineData("That event has no date then.")]
    [InlineData("The event has no channel to post in.")]
    [InlineData("That event is cancelled already.")]
    [InlineData("That event does not exist.")]
    public async Task WhatTheCancelWouldRefuse_IsSaidBeforeAnythingIsAskedAbout(string words)
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Plan = CalendarCancelPlan.Refused(words);

        var reply = await RunAsync(services, Slash(
            Caller,
            DiscordCommands.EventCancelDate,
            (DiscordCommands.EventOption, Movie.ToString()),
            (DiscordCommands.EventDateOption, Planned(Friday))));

        Assert.Equal(words, reply?.Text);
        Assert.Null(reply?.Actions);
        Assert.Empty(services.Calendar.CancelCalls);
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow-ish")]
    public async Task ADateThatIsNotOnTheList_IsAskedAgain_AndNothingIsChecked(string typed)
    {
        await using var services = await SetUpAsync(_db);

        var reply = await RunAsync(services, Slash(
            Caller,
            DiscordCommands.EventCancelDate,
            (DiscordCommands.EventOption, Movie.ToString()),
            (DiscordCommands.EventDateOption, typed)));

        Assert.Equal(EventCommand.PickDateMessage, reply?.Text);
        Assert.Empty(services.Calendar.Plans);
    }

    [Fact]
    public async Task AnEventTitle_IsEscapedInTheQuestion()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Plan = new CalendarCancelPlan(true, null, "**Big** @here night", Friday, WholeEvent: false);

        var (question, _, _) = await AskToCancelAsync(services);

        Assert.StartsWith("Cancel **\\*\\*Big\\*\\* @here night** on ", question.Text, StringComparison.Ordinal);
    }

    // ── Cancel one date: the press ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Yes_CancelsTheDate_AsTheAccount_AndSaysWhat()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);
        var recorder = new Recorder();

        await using var db = services.Database.NewContext();
        var account = await db.Users.SingleAsync(u => u.DiscordUserId == Caller, Ct);

        var reply = await PressAsync(services, Press(Caller, yes, recorder));

        // The question is rewritten at once, then says what was done; nothing is a new message.
        Assert.Null(reply);
        Assert.Equal(
            ["Cancelling…", $"Cancelled **Movie night** on <t:{Friday.ToUnixTimeSeconds()}:f>."],
            recorder.Updates.Select(u => u.Text));
        Assert.All(recorder.Updates, u => Assert.Null(u.Actions));
        Assert.Empty(recorder.Replies);

        var (eventId, planned, _, by) = Assert.Single(services.Calendar.Cancelled);
        Assert.Equal((Movie, Friday), (eventId, planned));
        Assert.Equal((account.Id, account.Username), (by.UserId, by.Username));
        Assert.Equal(["asked", "done"], await OutcomesAsync(services));
    }

    [Fact]
    public async Task APressRepeated_AnswersWithTheFirst_AndCancelsOnce()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);

        var first = new Recorder();
        var second = new Recorder();
        await PressAsync(services, Press(Caller, yes, first));
        await PressAsync(services, Press(Caller, yes, second));

        Assert.Single(services.Calendar.CancelCalls);
        Assert.Single(services.Calendar.Cancelled);
        Assert.Equal(first.Updates[^1].Text, second.Updates[^1].Text);
        Assert.Equal(["asked", "done", "repeat"], await OutcomesAsync(services));
    }

    [Fact]
    public async Task TwoPressesAtOnce_CancelOnce()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);

        await Task.WhenAll(
            PressAsync(services, Press(Caller, yes, new Recorder())),
            PressAsync(services, Press(Caller, yes, new Recorder())),
            PressAsync(services, Press(Caller, yes, new Recorder())));

        Assert.Single(services.Calendar.CancelCalls);
        Assert.Single(services.Calendar.Cancelled);
    }

    [Fact]
    public async Task ADateTheCalendarSaysWasCancelledAlready_IsSaidSo()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);

        // Cancelled in the web app between the question and the press.
        await services.Calendar.CancelDateAsync(
            Movie, Friday, false, new StaffMember(Guid.NewGuid(), "someone", ModbotPermissions.Administrator), Ct);

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal(EventCommand.AlreadyCancelledMessage, recorder.Updates[^1].Text);
        Assert.Equal("repeat", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task KeepIt_CancelsNothing_AndTheQuestionIsGone()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, no) = await AskToCancelAsync(services);
        var recorder = new Recorder();

        Assert.Null(await PressAsync(services, Press(Caller, no, recorder)));

        Assert.Equal([EventCommand.NothingCancelledMessage], recorder.Updates.Select(u => u.Text));
        Assert.Empty(services.Calendar.CancelCalls);

        // A press on the old Cancel button now finds nothing waiting.
        var late = new Recorder();
        await PressAsync(services, Press(Caller, yes, late));

        Assert.Equal([StaffInteractionHandler.RunOutMessage], late.Updates.Select(u => u.Text));
        Assert.Empty(services.Calendar.CancelCalls);
    }

    [Fact]
    public async Task OnlyThePersonWhoAsked_CanAnswer()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync("300", ModbotPermissions.ManageCalendar | ModbotPermissions.ViewCalendar, ct: Ct);
        var (_, yes, no) = await AskToCancelAsync(services);

        var recorder = new Recorder();
        var refused = await PressAsync(services, Press("300", yes, recorder));
        var refusedKeep = await PressAsync(services, Press("300", no, recorder));

        Assert.Equal(StaffInteractionHandler.NotYoursMessage, refused?.Text);
        Assert.Equal(StaffInteractionHandler.NotYoursMessage, refusedKeep?.Text);
        Assert.Empty(recorder.Updates);
        Assert.Empty(services.Calendar.CancelCalls);

        // The real one can still answer.
        await PressAsync(services, Press(Caller, yes, new Recorder()));
        Assert.Single(services.Calendar.Cancelled);
    }

    [Fact]
    public async Task APermissionTakenAwayBeforeThePress_IsARefusal_NotACancel()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);

        await using (var db = services.Database.NewContext())
        {
            var account = await db.Users.SingleAsync(u => u.DiscordUserId == Caller, Ct);
            await ChangePermissionsAsync(services, account, ModbotPermissions.ViewCalendar);
        }

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("You need the \"Manage calendar\" permission in Modbot to use /event.", recorder.Updates[^1].Text);
        Assert.Empty(services.Calendar.CancelCalls);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnAccountDisabledBeforeThePress_IsARefusal_NotACancel()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);

        await using (var db = services.Database.NewContext())
        {
            var account = await db.Users.SingleAsync(u => u.DiscordUserId == Caller, Ct);
            account.IsDisabled = true;
            await db.SaveChangesAsync(Ct);
        }

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("Your Modbot account is disabled.", recorder.Updates[^1].Text);
        Assert.Empty(services.Calendar.CancelCalls);
        Assert.Equal("disabled", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ACommandSwitchedOffBeforeThePress_IsNotCarriedOut()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);
        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Event, false), Ct);

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("/event is turned off on this server.", recorder.Updates[^1].Text);
        Assert.Empty(services.Calendar.CancelCalls);
        Assert.Equal("off", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AQuestionOlderThanFifteenMinutes_HasRunOut()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);

        services.Clock.Advance(TimeSpan.FromMinutes(16));

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal([StaffInteractionHandler.RunOutMessage], recorder.Updates.Select(u => u.Text));
        Assert.Empty(services.Calendar.CancelCalls);
    }

    [Fact]
    public async Task ARefusalAtThePress_IsShownInTheQuestionsPlace_AndRecorded()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);
        services.Calendar.CancelRefusal = "That date has already ended.";

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("That date has already ended.", recorder.Updates[^1].Text);
        Assert.Empty(services.Calendar.Cancelled);
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task APressThatFails_DoesNotLeaveCancellingOnScreen_AndIsNotSentAgain()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _) = await AskToCancelAsync(services);
        services.Calendar.CancelThrows = new InvalidOperationException("The database went away.");

        var first = new Recorder();
        await PressAsync(services, Press(Caller, yes, first));

        Assert.Equal(EventCommand.FailedMessage, first.Updates[^1].Text);
        Assert.Equal("error", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        // Even a throw is kept: a second press cannot send what the first may already have sent.
        services.Calendar.CancelThrows = null;
        await PressAsync(services, Press(Caller, yes, new Recorder()));
        Assert.Single(services.Calendar.CancelCalls);
    }

    [Fact]
    public async Task AnUnknownButton_IsToldSo()
    {
        await using var services = await SetUpAsync(_db);

        var reply = await PressAsync(services, Press(Caller, "modbot:event:maybe:abc", new Recorder()));

        Assert.Equal("Modbot does not know that button.", reply?.Text);
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    private static DiscordSuggestionAsk Ask(
        string who, string option, string typed, List<IReadOnlyList<DiscordSuggestion>> answers,
        string step = DiscordCommands.EventCancelDate, params (string Name, string Value)[] others)
        => new(
            who,
            DiscordCommands.Event,
            option,
            typed,
            (suggestions, _) =>
            {
                answers.Add(suggestions);
                return Task.CompletedTask;
            },
            step,
            others.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal));

    private static async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(TestServices services, DiscordSuggestionAsk ask)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(ask, Ct);
    }

    [Fact]
    public async Task TheEventList_OffersTheUpcomingEvents_ByTitle_WithTheIdAsTheValue()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Events.Add(new CalendarEventChoice(Movie, "Movie night", Friday, "Fri 18 Sep, 20:00 UTC"));
        services.Calendar.Events.Add(new CalendarEventChoice(Guid.NewGuid(), "Karaoke", Friday.AddDays(1), "Sat 19 Sep, 20:00 UTC"));
        var answers = new List<IReadOnlyList<DiscordSuggestion>>();

        var all = await SuggestAsync(services, Ask(Caller, DiscordCommands.EventOption, string.Empty, answers));
        Assert.Equal(["Movie night · Fri 18 Sep, 20:00 UTC", "Karaoke · Sat 19 Sep, 20:00 UTC"], all.Select(s => s.Name));
        Assert.Equal(Movie.ToString(), all[0].Value);

        var typed = await SuggestAsync(services, Ask(Caller, DiscordCommands.EventOption, "movie", answers));
        Assert.Equal(["Movie night · Fri 18 Sep, 20:00 UTC"], typed.Select(s => s.Name));

        var (asked, most, _) = services.Calendar.UpcomingAsked[^1];
        Assert.Equal("movie", asked);
        Assert.Equal(DiscordSuggestion.Most, most);
    }

    [Fact]
    public async Task TheEventList_KeepsToDiscordsLimits()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Events.Add(new CalendarEventChoice(Movie, new string('x', 300), Friday, "Fri 18 Sep, 20:00 UTC"));

        var suggestions = await SuggestAsync(services, Ask(Caller, DiscordCommands.EventOption, string.Empty, []));

        var one = Assert.Single(suggestions);
        Assert.InRange(one.Name.Length, 1, DiscordSuggestion.Longest);
        Assert.InRange(one.Value.Length, 1, DiscordSuggestion.Longest);
    }

    [Fact]
    public async Task TheDateList_ReadsTheEventAlreadyPicked_AndOffersItsNextTenDates()
    {
        await using var services = await SetUpAsync(_db);
        var dates = Enumerable.Range(0, 14)
            .Select(i => new CalendarDateChoice(Friday.AddDays(7 * i), Friday.AddDays(7 * i), "week " + i))
            .ToList();

        // The second date was moved: it is listed at its own time, and marked.
        dates[1] = dates[1] with { StartsAt = dates[1].PlannedStartsAt.AddHours(2) };
        services.Calendar.Dates[Movie] = dates;

        var suggestions = await SuggestAsync(
            services, Ask(Caller, DiscordCommands.EventDateOption, string.Empty, [], DiscordCommands.EventCancelDate, (DiscordCommands.EventOption, Movie.ToString())));

        Assert.Equal(10, suggestions.Count);
        Assert.Equal("week 0", suggestions[0].Name);
        Assert.Equal("week 1 (moved)", suggestions[1].Name);
        Assert.Equal(Planned(Friday), suggestions[0].Value);
        Assert.Equal(Planned(Friday.AddDays(7)), suggestions[1].Value);
        Assert.All(suggestions, s => Assert.InRange(s.Value.Length, 1, DiscordSuggestion.Longest));

        var (eventId, most, _) = Assert.Single(services.Calendar.DatesAsked);
        Assert.Equal((Movie, 10), (eventId, most));
    }

    [Fact]
    public async Task TheDateList_IsEmptyUntilAnEventIsPicked_OrWhenItIsNotAnId()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Dates[Movie] = [new CalendarDateChoice(Friday, Friday, "week 0")];

        Assert.Empty(await SuggestAsync(services, Ask(Caller, DiscordCommands.EventDateOption, string.Empty, [])));
        Assert.Empty(await SuggestAsync(
            services, Ask(Caller, DiscordCommands.EventDateOption, string.Empty, [], DiscordCommands.EventCancelDate, (DiscordCommands.EventOption, "not an id"))));
        Assert.Empty(services.Calendar.DatesAsked);
    }

    [Fact]
    public async Task Suggestions_AreForCallersTheCommandWouldAnswer_AndNobodyElse()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Events.Add(new CalendarEventChoice(Movie, "Movie night", Friday, "Fri 18 Sep, 20:00 UTC"));
        services.Calendar.Dates[Movie] = [new CalendarDateChoice(Friday, Friday, "week 0")];

        await services.LinkedAccountAsync("201", ModbotPermissions.ViewCalendar, ct: Ct);
        await services.LinkedAccountAsync("202", ModbotPermissions.Administrator, disabled: true, ct: Ct);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "no_vrchat", TestAccounts.Password, ModbotPermissions.Administrator, linked: false, Ct);
            user.DiscordUserId = "203";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        foreach (var who in new[] { "999", "201", "202", "203" })
        {
            Assert.Empty(await SuggestAsync(services, Ask(who, DiscordCommands.EventOption, string.Empty, [])));
            Assert.Empty(await SuggestAsync(
                services, Ask(who, DiscordCommands.EventDateOption, string.Empty, [], DiscordCommands.EventCancelDate, (DiscordCommands.EventOption, Movie.ToString()))));
        }

        Assert.Empty(services.Calendar.UpcomingAsked);
        Assert.Empty(services.Calendar.DatesAsked);

        // Not recorded: Discord asks on every key press.
        Assert.Empty(await OutcomesAsync(services));
    }

    [Fact]
    public async Task AnyOtherOptionOfEvent_SuggestsNothing()
    {
        await using var services = await SetUpAsync(_db);
        services.Calendar.Events.Add(new CalendarEventChoice(Movie, "Movie night", Friday, "Fri 18 Sep, 20:00 UTC"));

        Assert.Empty(await SuggestAsync(services, Ask(Caller, DiscordCommands.EventSayOption, string.Empty, [])));
    }

    // ── Several Modbots on one bot ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("modbot:event:yes:abc")]
    [InlineData("modbot:event:no:abc")]
    public void TheQuestionsButtons_AreAnsweredInPlace_AndOnlyInThisModbotsServer(string id)
    {
        Assert.True(EventCommand.IsButton(id));
        Assert.True(DiscordNetGateway.AnswersInPlace(id));

        // This server's, and nobody else's: another server, or a direct message, is left alone.
        Assert.True(DiscordNetGateway.IsOurButton("424242", 424242UL, id));
        Assert.False(DiscordNetGateway.IsOurButton("424242", 999UL, id));
        Assert.False(DiscordNetGateway.IsOurButton("424242", null, id));
    }
}
