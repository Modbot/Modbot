using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/vote</c>: Manage posts and a VRChat link, a switch that is off by default, the form that
/// carries the channel, the role and the two choices in its id, and the vote made and posted when
/// the form is sent, with the permission, the switch and the role checked again then.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VoteCommandTests
{
    private const string Caller = "100";
    private const string Channel = "222222222222222222";
    private const string Regulars = "811";
    private const string Guild = "1";

    private readonly PostgresFixture _db;

    public VoteCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder
    {
        public List<DiscordReply> Replies { get; } = [];

        public List<DiscordForm> Forms { get; } = [];

        public bool FormsTooLate { get; set; }

        public Task Reply(DiscordReply reply, CancellationToken ct)
        {
            Replies.Add(reply);
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

    private static DiscordCommandCall Slash(string who, Recorder recorder, params (string Name, string Value)[] options)
        => new(
            who,
            "host",
            DiscordCommands.Vote,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            recorder.Reply,
            recorder.Show);

    private static DiscordFormSubmit Submit(string who, string formId, string question, string options)
        => new(
            who,
            "host",
            formId,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [VoteCommand.QuestionField] = [question],
                [VoteCommand.OptionsField] = [options],
            },
            (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply?> RunAsync(TestServices services, DiscordCommandCall call)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(call, Ct);
    }

    private static async Task<DiscordReply> SubmitAsync(TestServices services, DiscordFormSubmit submit)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<VoteCommand>().HandleFormAsync(submit, Ct);
    }

    private static async Task<string> LastOutcomeAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement.GetProperty("outcome").GetString()!;
    }

    private static async Task<TestServices> SetUpAsync(
        PostgresFixture db, ModbotPermissions held = ModbotPermissions.ManagePosts, bool on = true)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.LinkedAccountAsync(Caller, held, ct: Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;

            if (on)
                s.SwitchCommand(DiscordCommands.Vote, true);
        }, Ct);

        await using var context = services.Database.NewContext();
        var now = services.Clock.UtcNow;
        context.DiscordRoles.Add(new DiscordRole { RoleId = Regulars, GuildId = Guild, Name = "Regulars", FirstSeenAt = now, UpdatedAt = now });
        await context.SaveChangesAsync(Ct);

        return services;
    }

    private static async Task<List<Vote>> SavedAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.Votes.AsNoTracking().Include(v => v.Options).ToListAsync(Ct);
    }

    // ── Registration and who may run it ─────────────────────────────────────────────────────

    [Fact]
    public void Vote_is_off_by_default_and_needs_Manage_posts()
    {
        Assert.False(Modbot.Core.Discord.DiscordCommandSwitches.Find(DiscordCommands.Vote)?.OnByDefault);
        Assert.DoesNotContain(DiscordCommands.For(Modbot.Core.Discord.DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.Vote);
        Assert.Equal(ModbotPermissions.ManagePosts, DiscordCommands.Requires(DiscordCommands.Vote));
        Assert.True(DiscordCommands.CanUse(DiscordCommands.Vote, ModbotPermissions.ManagePosts));
        Assert.False(DiscordCommands.CanUse(DiscordCommands.Vote, ModbotPermissions.ViewPosts));
    }

    [Fact]
    public void Vote_takes_the_channel_the_role_and_two_yes_no_choices()
    {
        var vote = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Vote);

        Assert.Equal(
            [DiscordCommands.VoteChannelOption, DiscordCommands.VoteRoleOption, DiscordCommands.VoteShowCountsOption, DiscordCommands.VoteShowWhoOption],
            vote.Options.Select(o => o.Name));
        Assert.Equal([true, false, false, false], vote.Options.Select(o => o.Required));
        Assert.Equal("show_counts", DiscordCommands.VoteShowCountsOption);
        Assert.Equal("show_who_voted", DiscordCommands.VoteShowWhoOption);
    }

    [Fact]
    public async Task An_unlinked_caller_a_disabled_one_and_one_with_no_VRChat_link_are_refused_in_that_order()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder();
        var channel = (DiscordCommands.VoteChannelOption, Channel);

        var unlinked = await RunAsync(services, Slash("999", recorder, channel));
        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, unlinked?.Text);

        await services.LinkedAccountAsync("101", ModbotPermissions.Administrator, disabled: true, ct: Ct);
        var disabled = await RunAsync(services, Slash("101", recorder, channel));
        Assert.Equal("Your Modbot account is disabled.", disabled?.Text);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "no_vrchat", TestAccounts.Password, ModbotPermissions.Administrator, linked: false, Ct);
            user.DiscordUserId = "102";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var noLink = await RunAsync(services, Slash("102", recorder, channel));
        Assert.Equal(StaffInteractionHandler.NeedsVRChatMessage, noLink?.Text);
        Assert.Equal("no-vrchat", await LastOutcomeAsync(services));

        Assert.Empty(recorder.Forms);
    }

    [Fact]
    public async Task Somebody_with_only_See_posts_is_refused_and_no_form_opens()
    {
        await using var services = await SetUpAsync(_db, ModbotPermissions.ViewPosts);
        var recorder = new Recorder();

        var reply = await RunAsync(services, Slash(Caller, recorder, (DiscordCommands.VoteChannelOption, Channel)));

        Assert.Equal("You need the \"Manage posts\" permission in Modbot to use /vote.", reply?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("no-permission", await LastOutcomeAsync(services));
    }

    [Fact]
    public async Task A_command_switched_off_is_refused_and_no_form_opens()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var recorder = new Recorder();

        var reply = await RunAsync(services, Slash(Caller, recorder, (DiscordCommands.VoteChannelOption, Channel)));

        Assert.Equal("/vote is turned off on this server.", reply?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Equal("off", await LastOutcomeAsync(services));
    }

    // ── The form ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_id_carries_the_channel_the_role_and_the_two_choices_and_asks_for_the_question_and_options()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder();

        var reply = await RunAsync(services, Slash(
            Caller,
            recorder,
            (DiscordCommands.VoteChannelOption, Channel),
            (DiscordCommands.VoteRoleOption, Regulars),
            (DiscordCommands.VoteShowCountsOption, "true"),
            (DiscordCommands.VoteShowWhoOption, "true")));

        Assert.Null(reply);
        var form = Assert.Single(recorder.Forms);
        Assert.True(VoteCommand.IsForm(form.Id));
        Assert.Equal(VoteCommand.FormPrefix + $"{Channel}:{Regulars}:1:0", form.Id);
        Assert.Equal([VoteCommand.QuestionField, VoteCommand.OptionsField], form.Fields.Select(f => f.Id));
    }

    [Fact]
    public async Task Choices_left_out_make_the_form_anonymous_with_no_counts_and_for_everybody()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder();

        await RunAsync(services, Slash(Caller, recorder, (DiscordCommands.VoteChannelOption, Channel)));

        Assert.Equal(VoteCommand.FormPrefix + $"{Channel}:0:0:1", Assert.Single(recorder.Forms).Id);
    }

    [Fact]
    public async Task Choices_answered_no_are_the_same_as_left_out()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder();

        await RunAsync(services, Slash(
            Caller,
            recorder,
            (DiscordCommands.VoteChannelOption, Channel),
            (DiscordCommands.VoteShowCountsOption, "false"),
            (DiscordCommands.VoteShowWhoOption, "false")));

        Assert.Equal(VoteCommand.FormPrefix + $"{Channel}:0:0:1", Assert.Single(recorder.Forms).Id);
    }

    [Fact]
    public void The_form_helper_writes_the_same_id()
    {
        Assert.Equal(VoteCommand.FormPrefix + "5:0:0:1", VoteCommand.Form("5", null).Id);
        Assert.Equal(VoteCommand.FormPrefix + "5:7:1:0", VoteCommand.Form("5", "7", showCounts: true, anonymous: false).Id);
    }

    [Fact]
    public async Task A_missing_channel_asks_for_one_and_a_form_that_is_too_late_is_said_so()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder();

        var none = await RunAsync(services, Slash(Caller, recorder));
        Assert.Equal(VoteCommand.PickChannelMessage, none?.Text);
        Assert.Empty(recorder.Forms);

        recorder.FormsTooLate = true;
        var late = await RunAsync(services, Slash(Caller, recorder, (DiscordCommands.VoteChannelOption, Channel)));
        Assert.Equal(StaffInteractionHandler.TooLateMessage, late?.Text);
    }

    // ── Sending the form ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sending_the_form_makes_and_posts_the_vote_with_the_choices_from_its_id()
    {
        await using var services = await SetUpAsync(_db);
        var formId = VoteCommand.Form(Channel, Regulars, showCounts: true, anonymous: false).Id;

        var reply = await SubmitAsync(services, Submit(Caller, formId, "Which world?", "Forest\nDesert\n\nOcean"));

        Assert.Equal($"Vote posted in <#{Channel}> with 3 options.", reply.Text);
        Assert.Single(services.Votes.Posted);

        var saved = Assert.Single(await SavedAsync(services));
        Assert.Equal("Which world?", saved.Question);
        Assert.Equal(Channel, saved.ChannelId);
        Assert.Equal(Regulars, saved.RoleId);
        Assert.True(saved.ShowCounts);
        Assert.False(saved.Anonymous);
        Assert.Equal(["Forest", "Desert", "Ocean"], saved.Options.OrderBy(o => o.Position).Select(o => o.Label));
        Assert.Equal("done", await LastOutcomeAsync(services));
    }

    [Fact]
    public async Task A_form_for_everybody_with_the_defaults_makes_an_anonymous_vote_that_hides_its_counts()
    {
        await using var services = await SetUpAsync(_db);

        await SubmitAsync(services, Submit(Caller, VoteCommand.Form(Channel, null).Id, "Which world?", "Forest\nDesert"));

        var saved = Assert.Single(await SavedAsync(services));
        Assert.Null(saved.RoleId);
        Assert.False(saved.ShowCounts);
        Assert.True(saved.Anonymous);
    }

    [Fact]
    public async Task A_role_that_is_not_in_the_server_is_said_so_and_nothing_is_posted()
    {
        await using var services = await SetUpAsync(_db);
        var formId = VoteCommand.Form(Channel, "99999").Id;

        var reply = await SubmitAsync(services, Submit(Caller, formId, "Which world?", "Forest\nDesert"));

        Assert.Equal("That role is not in the Discord server.", reply.Text);
        Assert.Empty(services.Votes.Posted);
        Assert.Empty(await SavedAsync(services));
        Assert.Equal("refused", await LastOutcomeAsync(services));
    }

    [Fact]
    public async Task One_option_is_refused_in_words_and_nothing_is_posted()
    {
        await using var services = await SetUpAsync(_db);

        var reply = await SubmitAsync(services, Submit(Caller, VoteCommand.Form(Channel, null).Id, "Which world?", "Forest"));

        Assert.Equal("Add at least two options.", reply.Text);
        Assert.Empty(services.Votes.Posted);
    }

    [Fact]
    public async Task A_bot_that_cannot_post_gives_its_reason_and_no_vote_is_saved()
    {
        await using var services = await SetUpAsync(_db);
        services.Votes.PostOutcome = Modbot.Core.Discord.VoteSiteOutcome.Failed("Missing permissions.");

        var reply = await SubmitAsync(services, Submit(Caller, VoteCommand.Form(Channel, null).Id, "Which world?", "Forest\nDesert"));

        Assert.Equal("Missing permissions.", reply.Text);
        Assert.Empty(await SavedAsync(services));
    }

    [Fact]
    public async Task A_command_switched_off_before_the_form_was_sent_makes_no_vote()
    {
        await using var services = await SetUpAsync(_db);
        var formId = VoteCommand.Form(Channel, null).Id;
        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Vote, false), Ct);

        var reply = await SubmitAsync(services, Submit(Caller, formId, "Which world?", "Forest\nDesert"));

        Assert.Equal("/vote is turned off on this server.", reply.Text);
        Assert.Empty(services.Votes.Posted);
        Assert.Equal("off", await LastOutcomeAsync(services));
    }

    [Fact]
    public async Task A_permission_taken_away_before_the_form_was_sent_is_a_refusal_not_a_vote()
    {
        await using var services = await SetUpAsync(_db);
        var formId = VoteCommand.Form(Channel, null).Id;

        await using (var db = services.Database.NewContext())
        {
            var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.DiscordUserId == Caller, Ct);
            var roleId = await TestAccounts.RoleForAsync(db, ModbotPermissions.ViewPosts, Ct);

            foreach (var role in user.Roles.ToList())
                db.Remove(role);

            db.Add(new ModbotUserRole { UserId = user.Id, RoleId = roleId });
            await db.SaveChangesAsync(Ct);
        }

        var reply = await SubmitAsync(services, Submit(Caller, formId, "Which world?", "Forest\nDesert"));

        Assert.Equal("You need the \"Manage posts\" permission in Modbot to use /vote.", reply.Text);
        Assert.Empty(services.Votes.Posted);
        Assert.Equal("no-permission", await LastOutcomeAsync(services));
    }

    [Fact]
    public async Task A_form_sent_by_somebody_with_no_VRChat_link_makes_no_vote()
    {
        await using var services = await SetUpAsync(_db);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "no_vrchat", TestAccounts.Password, ModbotPermissions.ManagePosts, linked: false, Ct);
            user.DiscordUserId = "102";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var reply = await SubmitAsync(services, Submit("102", VoteCommand.Form(Channel, null).Id, "Which world?", "Forest\nDesert"));

        Assert.Equal(StaffInteractionHandler.NeedsVRChatMessage, reply.Text);
        Assert.Empty(services.Votes.Posted);
        Assert.Equal("no-vrchat", await LastOutcomeAsync(services));
    }

    [Theory]
    [InlineData("5:0:0")]
    [InlineData("5:0:0:1:1")]
    [InlineData(":0:0:1")]
    [InlineData("5::0:1")]
    public async Task A_form_id_that_is_not_Modbots_is_not_known(string rest)
    {
        await using var services = await SetUpAsync(_db);

        var reply = await SubmitAsync(services, Submit(Caller, VoteCommand.FormPrefix + rest, "Which world?", "Forest\nDesert"));

        Assert.Equal("Modbot does not know that form.", reply.Text);
        Assert.Empty(services.Votes.Posted);
    }

    [Fact]
    public void IsForm_matches_only_this_commands_forms()
    {
        Assert.True(VoteCommand.IsForm(VoteCommand.FormPrefix + "5:0:0:1"));
        Assert.False(VoteCommand.IsForm(PostCommand.FormPrefix + "abc"));
    }
}
