using Discord;
using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// The command layer every later command is built on (Discord commands design §3.1, §3.2, §3.7,
/// §3.8): which commands are registered for which switches, who sees a reply, subcommands, the
/// option kinds, and what an autocomplete request carries.
/// </summary>
public class CommandLayerTests
{
    // ── The switches and the commands are one list ─────────────────────────────────────────

    [Fact]
    public void EveryCommandHasASwitch_AndEverySwitchACommand()
    {
        var commands = DiscordCommands.All.Select(c => c.Name).Order().ToList();
        var switches = DiscordCommandSwitches.All.Select(c => c.Name).Order().ToList();

        Assert.Equal(switches, commands);
    }

    [Fact]
    public void ASlashCommandsSwitchSaysWhatTheCommandSays_AndMenusAreMarkedAsMenus()
    {
        foreach (var command in DiscordCommands.All)
        {
            var found = DiscordCommandSwitches.Find(command.Name);
            Assert.NotNull(found);
            Assert.Equal(command.Kind != DiscordCommandKind.Slash, found.Menu);

            if (command.Kind == DiscordCommandKind.Slash)
                Assert.Equal(command.Description, found.Description);
        }
    }

    // ── Registration by the set of names ───────────────────────────────────────────────────

    [Fact]
    public void WithNothingStored_TheDefaultsAreRegistered_AndMeIsLeftOut()
    {
        var registered = DiscordCommands.For(DiscordCommandSwitches.Empty);

        Assert.DoesNotContain(registered, c => c.Name == DiscordCommands.Me);
        Assert.Equal(DiscordCommands.All.Count - 1, registered.Count);
        Assert.Equal(registered.Select(c => c.Name).ToHashSet(), DiscordCommands.NamesFor(null));
    }

    [Fact]
    public void ACommandSwitchedOff_IsLeftOut_AndOneSwitchedOn_IsPut_InWithoutMovingTheOthers()
    {
        var json = DiscordCommandSwitches.Write(new Dictionary<string, bool>
        {
            ["recent"] = false,
            ["me"] = true,
            ["Add a note"] = false,
        });

        var names = DiscordCommands.NamesFor(json);

        Assert.DoesNotContain("recent", names);
        Assert.DoesNotContain("Add a note", names);
        Assert.Contains("me", names);
        Assert.Contains("lookup", names);
        Assert.Contains("Look up in Modbot", names);
    }

    [Fact]
    public void TwoSettingsThatLeaveTheSameCommandsOn_AreTheSameRegistration()
    {
        // A stored name that is not a command, and a stored choice equal to the default, change nothing.
        var same = DiscordCommands.NamesFor("{\"nonsense\":true,\"lookup\":true}");

        Assert.True(same.SetEquals(DiscordCommands.NamesFor(DiscordCommandSwitches.Empty)));
        Assert.False(same.SetEquals(DiscordCommands.NamesFor("{\"me\":true}")));
    }

    // ── What Discord is given ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDefinition_FollowsDiscordsOwnRules()
    {
        foreach (var command in DiscordCommands.All)
        {
            Assert.InRange(command.Name.Length, 1, 32);

            if (command.Kind != DiscordCommandKind.Slash)
                continue;

            Assert.Equal(command.Name.ToLowerInvariant(), command.Name);
            Assert.DoesNotContain(' ', command.Name);
            Assert.InRange(command.Description.Length, 1, 100);

            // Discord takes options or subcommands, never both.
            Assert.False(command.Options.Count > 0 && command.Subcommands is { Count: > 0 }, command.Name);

            CheckOptions(command.Name, command.Options);

            foreach (var step in command.Subcommands ?? [])
            {
                Assert.InRange(step.Description.Length, 1, 100);
                CheckOptions(command.Name + " " + step.Name, step.Options);
            }
        }
    }

    private static void CheckOptions(string where, IReadOnlyList<DiscordCommandOption> options)
    {
        Assert.InRange(options.Count, 0, 25);

        // Discord refuses a required option after an optional one.
        var sawOptional = false;
        foreach (var option in options)
        {
            Assert.InRange(option.Description.Length, 1, 100);
            Assert.False(sawOptional && option.Required, $"{where}: {option.Name} is required after an optional one");
            sawOptional |= !option.Required;

            if (option.Kind == DiscordOptionKind.Choice)
                Assert.InRange(option.Choices?.Count ?? 0, 1, DiscordOptionChoice.Most);
            else
                Assert.Null(option.Choices);

            Assert.False(option.Suggests && option.Kind != DiscordOptionKind.Text, $"{where}: only text options suggest");
        }
    }

    // ── Who sees a reply ───────────────────────────────────────────────────────────────────

    private static readonly DiscordCommandDefinition PrivateOne = new("quiet", "A private one", []);

    private static readonly DiscordCommandDefinition PublicOne = new("loud", "A public one", [], Reply: DiscordReplyKind.Public);

    private static readonly DiscordCommandDefinition ChosenOne = new(
        "events",
        "Upcoming events",
        [new DiscordCommandOption("private", "Only show me", DiscordOptionKind.YesNo, Required: false)],
        Reply: DiscordReplyKind.Chosen,
        PrivateOption: "private");

    private static IReadOnlyDictionary<string, string> Typed(params (string Name, string Value)[] options)
        => options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);

    [Fact]
    public void EveryCommandThereIsToday_RepliesInPrivate()
    {
        Assert.All(DiscordCommands.All, c => Assert.Equal(DiscordReplyKind.Private, c.Reply));
        Assert.All(DiscordCommands.All, c => Assert.False(c.RepliesInPublic(Typed())));
    }

    [Fact]
    public void ACommandThatRepliesInPublic_Does_AndOneThatIsPrivate_DoesNot()
    {
        Assert.True(PublicOne.RepliesInPublic(Typed()));
        Assert.False(PrivateOne.RepliesInPublic(Typed()));
    }

    [Fact]
    public void WhenTheOptionChoosesTheReply_PublicIsTheDefault_AndYesToPrivateMakesItPrivate()
    {
        Assert.True(ChosenOne.RepliesInPublic(Typed()));
        Assert.True(ChosenOne.RepliesInPublic(Typed(("private", "false"))));
        Assert.False(ChosenOne.RepliesInPublic(Typed(("private", "true"))));
        Assert.False(ChosenOne.RepliesInPublic(Typed(("private", "True"))));
    }

    /// <summary>The gateway reads this before it acknowledges, so the deferral has the right audience.</summary>
    [Fact]
    public void TheGatewayReadsTheReplyFromTheRegisteredCommand_AndAnythingUnknownIsPrivate()
    {
        IReadOnlyList<DiscordCommandDefinition> registered = [PrivateOne, PublicOne, ChosenOne];

        Assert.False(DiscordNetGateway.RepliesInPublic(registered, "quiet", Typed()));
        Assert.True(DiscordNetGateway.RepliesInPublic(registered, "loud", Typed()));
        Assert.True(DiscordNetGateway.RepliesInPublic(registered, "events", Typed()));
        Assert.False(DiscordNetGateway.RepliesInPublic(registered, "events", Typed(("private", "true"))));

        // Nothing is made public by accident: not registered, or a menu of the same name.
        Assert.False(DiscordNetGateway.RepliesInPublic(registered, "unknown", Typed()));
        Assert.False(DiscordNetGateway.RepliesInPublic([], "loud", Typed()));
        Assert.False(DiscordNetGateway.RepliesInPublic(
            [new("loud", string.Empty, [], DiscordCommandKind.User, Reply: DiscordReplyKind.Public)], "loud", Typed()));
    }

    // ── Who sees the command ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheStaffCommandsAreHidden_WithTimeoutMembers_AndAnEventHostsOneIsAFurtherKind()
    {
        Assert.Equal(DiscordShownTo.Moderators, DiscordCommands.All.Single(c => c.Name == DiscordCommands.Lookup).ShownTo);
        Assert.Equal(DiscordShownTo.Everyone, DiscordCommands.All.Single(c => c.Name == DiscordCommands.Link).ShownTo);

        var eventHosts = new DiscordCommandDefinition("event", "Calendar events", [], ShownTo: DiscordShownTo.EventHosts);
        Assert.True(eventHosts.StaffOnly);
        Assert.False(PrivateOne.StaffOnly);
    }

    // ── /help for whatever is registered ───────────────────────────────────────────────────

    [Fact]
    public void HelpHasOneLine_ForACommandWithOptions()
    {
        var lookup = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Lookup);

        Assert.Equal(
            ["`/lookup user: discord:` — Look up a person in Modbot's records"],
            DiscordCommandHandler.HelpLines(lookup));
    }

    [Fact]
    public void HelpHasOneLine_ForEachSubcommand()
    {
        var gate = new DiscordCommandDefinition(
            "gate",
            "The join gate",
            [],
            Subcommands:
            [
                new DiscordSubcommand("waiting", "Who is waiting", []),
                new DiscordSubcommand(
                    "let-in",
                    "Let someone in",
                    [new DiscordCommandOption("member", "Who", DiscordOptionKind.Member, Required: true)]),
            ]);

        Assert.Equal(
            [
                "`/gate waiting` — Who is waiting",
                "`/gate let-in member:` — Let someone in",
            ],
            DiscordCommandHandler.HelpLines(gate));
    }

    // ── Autocomplete carries the other options ─────────────────────────────────────────────

    private static (string Name, bool IsSubcommand, object? Value, bool Focused) Filled(
        ApplicationCommandOptionType type, string name, object? value, bool focused = false)
        => (name, type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup, value, focused);

    [Fact]
    public void AnAutocompleteRequest_CarriesTheOtherOptionsAlreadyFilledIn()
    {
        var (subcommand, others) = DiscordNetGateway.OtherOptions(
        [
            Filled(ApplicationCommandOptionType.SubCommand, "cancel-date", null),
            Filled(ApplicationCommandOptionType.String, "event", "3f2a-event-id"),
            Filled(ApplicationCommandOptionType.String, "date", "20", focused: true),
            Filled(ApplicationCommandOptionType.Boolean, "say-so", true),
        ]);

        Assert.Equal("cancel-date", subcommand);
        Assert.Equal("3f2a-event-id", others["event"]);
        Assert.Equal("true", others["say-so"]);
        Assert.False(others.ContainsKey("date"));
        Assert.Equal(2, others.Count);
    }

    [Fact]
    public void AnOptionLeftEmpty_IsNotCarried()
    {
        var (subcommand, others) = DiscordNetGateway.OtherOptions(
        [
            Filled(ApplicationCommandOptionType.String, "event", string.Empty),
            Filled(ApplicationCommandOptionType.String, "date", "20", focused: true),
        ]);

        Assert.Null(subcommand);
        Assert.Empty(others);
    }

    [Fact]
    public async Task TheSuggestionAsk_HandsTheFilledInOptionsToWhoeverAnswers()
    {
        IReadOnlyList<DiscordSuggestion>? sent = null;

        var ask = new DiscordSuggestionAsk(
            "999",
            "event",
            "date",
            "2",
            (suggestions, _) =>
            {
                sent = suggestions;
                return Task.CompletedTask;
            },
            subcommand: "cancel-date",
            options: new Dictionary<string, string> { ["event"] = "e-1" });

        Assert.Equal("cancel-date", ask.Subcommand);
        Assert.Equal("e-1", ask.Option("event"));
        Assert.Null(ask.Option("date"));

        await ask.AnswerAsync([new DiscordSuggestion("Mon 20 Oct", "2026-10-20")], TestContext.Current.CancellationToken);
        Assert.Equal("2026-10-20", Assert.Single(sent!).Value);
    }

    [Fact]
    public void ASuggestionAskWithNoOptions_HasNone_AndNoSubcommand()
    {
        var ask = new DiscordSuggestionAsk("999", "lookup", "user", "jes", (_, _) => Task.CompletedTask);

        Assert.Empty(ask.Options);
        Assert.Null(ask.Subcommand);
    }

    // ── The option kinds as text ───────────────────────────────────────────────────────────

    [Fact]
    public void ACallReadsAYesNoOptionAsAFlag()
    {
        var call = new DiscordCommandCall(
            "999",
            "someone",
            "events",
            Typed(("private", "true")),
            (_, _) => Task.CompletedTask);

        Assert.True(call.Flag("private"));
        Assert.False(call.Flag("other"));
        Assert.Null(call.Subcommand);
    }

    [Fact]
    public void ADiscordValueBecomesTheTextACommandCarries()
    {
        Assert.Equal("true", DiscordNetGateway.OptionText(true));
        Assert.Equal("false", DiscordNetGateway.OptionText(false));
        Assert.Equal("12", DiscordNetGateway.OptionText(12L));
        Assert.Equal("hello", DiscordNetGateway.OptionText("hello"));
        Assert.Equal(string.Empty, DiscordNetGateway.OptionText(null));
    }
}
