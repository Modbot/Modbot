using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/post</c> (Discord commands design §3.7 and §4, step 8): Manage posts for <c>new</c> and See
/// posts for <c>list</c>, the form and the private preview, Post now saving one post however many
/// times it is pressed, a permission checked again at the form and the press, and the list in the
/// order the Marketing tab shows it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostCommandTests
{
    private const string Caller = "100";
    private const string Channel = "222222222222222222";

    private readonly PostgresFixture _db;

    public PostCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static DiscordCommandCall Slash(string who, string step, Recorder recorder, params (string Name, string Value)[] options)
        => new(
            who,
            "host",
            DiscordCommands.Post,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            recorder.Reply,
            recorder.Show)
        {
            Subcommand = step,
        };

    private static DiscordButtonPress Press(string who, string buttonId, Recorder recorder)
        => new(who, "host", buttonId, recorder.Reply, recorder.Show, recorder.Update);

    private static DiscordFormSubmit Submit(string who, string formId, string title, string text)
        => new(
            who,
            "host",
            formId,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [PostCommand.TitleField] = [title],
                [PostCommand.TextField] = [text],
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
        return await scope.ServiceProvider.GetRequiredService<PostCommand>().HandleFormAsync(submit, Ct);
    }

    private static async Task<DiscordReply?> PressAsync(TestServices services, DiscordButtonPress press)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<PostCommand>().HandleButtonAsync(press, Ct);
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

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, ModbotPermissions held = ModbotPermissions.ManagePosts | ModbotPermissions.ViewPosts)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.LinkedAccountAsync(Caller, held, ct: Ct);
        return services;
    }

    private static async Task ChangePermissionsAsync(TestServices services, string discordUserId, ModbotPermissions permissions)
    {
        await using var db = services.Database.NewContext();
        var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.DiscordUserId == discordUserId, Ct);
        var roleId = await TestAccounts.RoleForAsync(db, permissions, Ct);

        foreach (var role in user.Roles.ToList())
            db.Remove(role);

        db.Add(new ModbotUserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Runs <c>/post new</c> and returns the form it opened.</summary>
    private static async Task<DiscordForm> OpenFormAsync(TestServices services, string who = Caller)
    {
        var recorder = new Recorder();
        var reply = await RunAsync(services, Slash(who, DiscordCommands.PostNew, recorder, (DiscordCommands.PostChannelOption, Channel)));

        Assert.Null(reply);
        return Assert.Single(recorder.Forms);
    }

    /// <summary>Writes a post in the form and returns the private preview with its buttons.</summary>
    private static async Task<(DiscordReply Preview, string Yes, string No, string Token)> PreviewAsync(
        TestServices services, string title = "Movie night", string text = "Friday at eight.", string who = Caller)
    {
        var form = await OpenFormAsync(services, who);
        var preview = await SubmitAsync(services, Submit(who, form.Id, title, text));

        return (
            preview,
            ButtonId(preview, PostCommand.PostNowLabel)!,
            ButtonId(preview, PostCommand.CancelLabel)!,
            form.Id[PostCommand.FormPrefix.Length..]);
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Post_IsAnEventHostsCommand_OnByDefault_AndPrivate()
    {
        var command = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.Post);

        Assert.Equal(DiscordShownTo.EventHosts, command.ShownTo);
        Assert.Equal(DiscordReplyKind.Private, command.Reply);
        Assert.Equal(DiscordCommandKind.Slash, command.Kind);
        Assert.Empty(command.Options);
        Assert.Equal("Posts", command.Description);
        Assert.True(DiscordCommandSwitches.Find(DiscordCommands.Post)?.OnByDefault);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.Post);
        Assert.False(DiscordCommands.IsForEveryone(DiscordCommands.Post));
    }

    [Fact]
    public void Post_HasTwoSteps_InTheDesignsWords()
    {
        var command = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Post);

        Assert.Equal(
            [("new", "Write a post now"), ("list", "Posts waiting to go out")],
            command.Subcommands!.Select(s => (s.Name, s.Description)));

        var channel = Assert.Single(command.Subcommands!.Single(s => s.Name == "new").Options);
        Assert.Equal(("channel", DiscordOptionKind.Channel, true), (channel.Name, channel.Kind, channel.Required));
        Assert.Empty(command.Subcommands!.Single(s => s.Name == "list").Options);

        Assert.Equal(
            ["`/post new channel:` — Write a post now", "`/post list` — Posts waiting to go out"],
            DiscordCommandHandler.HelpLines(command));
    }

    [Fact]
    public void NewNeedsManagePosts_ListNeedsSeePosts_AndBothAreLabelledAsTheWebAppLabelsThem()
    {
        Assert.Equal(ModbotPermissions.ManagePosts, DiscordCommands.RequiresFor(DiscordCommands.Post, DiscordCommands.PostNew));
        Assert.Equal(ModbotPermissions.ViewPosts, DiscordCommands.RequiresFor(DiscordCommands.Post, DiscordCommands.PostList));
        Assert.Equal("Manage posts", DiscordCommands.Label(ModbotPermissions.ManagePosts));
        Assert.Equal("See posts", DiscordCommands.Label(ModbotPermissions.ViewPosts));

        Assert.True(DiscordCommands.Writes(DiscordCommands.Post, DiscordCommands.PostNew));
        Assert.False(DiscordCommands.Writes(DiscordCommands.Post, DiscordCommands.PostList));
    }

    [Theory]
    [InlineData(ModbotPermissions.ManagePosts, true)]
    [InlineData(ModbotPermissions.ViewPosts, true)]
    [InlineData(ModbotPermissions.ManageCalendar | ModbotPermissions.WriteNotes, false)]
    [InlineData(ModbotPermissions.Administrator, true)]
    public void AHolderOfEitherPermission_CanUseTheCommand(ModbotPermissions held, bool usable)
        => Assert.Equal(usable, DiscordCommands.CanUse(DiscordCommands.Post, held));

    // ── Who may run it ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task New_AnUnlinkedCaller_ADisabledOne_AndOneWithNoVRChatLink_AreRefusedInThatOrder()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var recorder = new Recorder();
        var channel = (DiscordCommands.PostChannelOption, Channel);

        var unlinked = await RunAsync(services, Slash("999", DiscordCommands.PostNew, recorder, channel));
        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, unlinked?.Text);
        Assert.Equal("not-linked", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        await services.LinkedAccountAsync("101", ModbotPermissions.Administrator, disabled: true, ct: Ct);
        var disabled = await RunAsync(services, Slash("101", DiscordCommands.PostNew, recorder, channel));
        Assert.Equal("Your Modbot account is disabled.", disabled?.Text);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "no_vrchat", TestAccounts.Password, ModbotPermissions.Administrator, linked: false, Ct);
            user.DiscordUserId = "102";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var noLink = await RunAsync(services, Slash("102", DiscordCommands.PostNew, recorder, channel));
        Assert.Equal(StaffInteractionHandler.NeedsVRChatMessage, noLink?.Text);
        Assert.Equal("no-vrchat", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        Assert.Empty(recorder.Forms);
    }

    [Fact]
    public async Task New_NeedsManagePosts_SeePostsAloneIsRefused_AndNoFormOpens()
    {
        await using var services = await SetUpAsync(_db, ModbotPermissions.ViewPosts);
        var recorder = new Recorder();

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostNew, recorder, (DiscordCommands.PostChannelOption, Channel)));

        Assert.Equal("You need the \"Manage posts\" permission in Modbot to use /post.", reply?.Text);
        Assert.Empty(recorder.Forms);

        var fact = await LastCommandFactAsync(services);
        Assert.Equal("no-permission", fact.GetProperty("outcome").GetString());
        Assert.Equal("post", fact.GetProperty("command").GetString());
        Assert.Equal("new", fact.GetProperty("subcommand").GetString());
    }

    [Fact]
    public async Task List_NeedsSeePosts_ManagePostsAloneIsRefused_AndNothingIsRead()
    {
        await using var services = await SetUpAsync(_db, ModbotPermissions.ManagePosts);

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostList, new Recorder()));

        Assert.Equal("You need the \"See posts\" permission in Modbot to use /post.", reply?.Text);
        Assert.Empty(services.Posts.Lists);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task List_NeedsNoVRChatLink_BecauseItOnlyReads()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(db, "reader", TestAccounts.Password, ModbotPermissions.ViewPosts, linked: false, Ct);
            user.DiscordUserId = "103";
            user.DiscordUsername = "someone";
            user.DiscordVerifiedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var reply = await RunAsync(services, Slash("103", DiscordCommands.PostList, new Recorder()));

        Assert.Equal(PostCommand.NothingWaitingMessage, reply?.Text);
        Assert.Single(services.Posts.Lists);
    }

    [Theory]
    [InlineData(DiscordCommands.PostNew)]
    [InlineData(DiscordCommands.PostList)]
    public async Task ACommandSwitchedOff_IsRefused(string step)
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Post, false), Ct);
        var recorder = new Recorder();

        var reply = await RunAsync(services, Slash(Caller, step, recorder, (DiscordCommands.PostChannelOption, Channel)));

        Assert.Equal("/post is turned off on this server.", reply?.Text);
        Assert.Empty(recorder.Forms);
        Assert.Empty(services.Posts.Lists);
        Assert.Equal("off", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Help_ListsPost_ForSomebodyWhoMayUseAStepOfIt_AndNotForOthers()
    {
        await using var services = await SetUpAsync(_db, ModbotPermissions.ViewPosts);
        await services.LinkedAccountAsync("200", ModbotPermissions.WriteNotes, ct: Ct);

        async Task<string> HelpAsync(string who)
        {
            using var scope = services.Scope();
            var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(
                new DiscordCommandCall(who, "x", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask), Ct);
            return reply.Text!;
        }

        Assert.Contains("`/post list` — Posts waiting to go out", await HelpAsync(Caller), StringComparison.Ordinal);
        Assert.DoesNotContain("/post", await HelpAsync("200"), StringComparison.Ordinal);
    }

    // ── The form ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task New_OpensTheForm_AsTheFirstAnswer_AndRecordsNothingYet()
    {
        await using var services = await SetUpAsync(_db);

        var form = await OpenFormAsync(services);

        Assert.Equal("Write a post", form.Title);
        Assert.StartsWith("modbot:form:post:", form.Id, StringComparison.Ordinal);
        Assert.InRange(form.Id.Length, 1, StaffMenus.MaxIdLength);

        var title = form.Fields.Single(f => f.Id == PostCommand.TitleField);
        Assert.Equal(("Title", DiscordFormFieldKind.ShortText, false), (title.Label, title.Kind, title.Required));
        Assert.Equal(Post.MaxTitleLength, title.MaxLength);

        var text = form.Fields.Single(f => f.Id == PostCommand.TextField);
        Assert.Equal(("Text", DiscordFormFieldKind.LongText, true), (text.Label, text.Kind, text.Required));
        Assert.Equal(PostTexts.DiscordLimit, text.MaxLength);

        // A form being opened is not a finished step, and nothing is checked or saved yet.
        Assert.Empty(await OutcomesAsync(services));
        Assert.Empty(services.Posts.Previews);
        Assert.Empty(services.Posts.SaveCalls);
    }

    [Fact]
    public async Task New_WithNoChannel_AsksAgain()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder();

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostNew, recorder));

        Assert.Equal(PostCommand.PickChannelMessage, reply?.Text);
        Assert.Empty(recorder.Forms);
    }

    [Fact]
    public async Task New_WhenTheFormCanNoLongerBeShown_SaysSo()
    {
        await using var services = await SetUpAsync(_db);
        var recorder = new Recorder { FormsTooLate = true };

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostNew, recorder, (DiscordCommands.PostChannelOption, Channel)));

        Assert.Equal(StaffInteractionHandler.TooLateMessage, reply?.Text);
    }

    // ── The preview ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheForm_ShowsAPrivatePreview_WithPostNowAndCancel()
    {
        await using var services = await SetUpAsync(_db);
        var account = await ReadAccountAsync(services, Caller);

        var (preview, yes, no, _) = await PreviewAsync(services);

        Assert.Equal($"Post this to <#{Channel}>?", preview.Text);
        var card = Assert.Single(preview.Embeds);
        Assert.Equal("Post to #announcements", card.Title);
        Assert.Equal(["Post now", "Cancel"], preview.Actions!.Select(a => a.Label));
        Assert.Equal(DiscordButtonStyle.Main, preview.Actions![0].Style);
        Assert.StartsWith("modbot:post:yes:", yes, StringComparison.Ordinal);
        Assert.StartsWith("modbot:post:no:", no, StringComparison.Ordinal);
        Assert.All(preview.Actions!, a => Assert.True(a.Id.Length <= StaffMenus.MaxIdLength));

        // Checked, not saved.
        var (draft, by) = Assert.Single(services.Posts.Previews);
        Assert.Equal(new PostDraft("Movie night", "Friday at eight.", Channel), draft);
        Assert.Equal((account.Id, account.Username), (by.UserId, by.Username));
        Assert.Empty(services.Posts.SaveCalls);

        var fact = await LastCommandFactAsync(services);
        Assert.Equal("asked", fact.GetProperty("outcome").GetString());
        Assert.Equal("post", fact.GetProperty("command").GetString());
    }

    [Fact]
    public async Task ThePreview_IsTheMessageThePostsCodeWillSend_WordForWord()
    {
        await using var services = await SetUpAsync(_db);

        var (preview, yes, _, _) = await PreviewAsync(services, "Movie night", "Friday at eight.\r\nBring snacks.");

        // What the sender builds from the saved post: the same function the check used.
        var shown = Assert.Single(preview.Embeds).Description;
        await PressAsync(services, Press(Caller, yes, new Recorder()));

        var saved = Assert.Single(services.Posts.Saved);
        Assert.Equal(PostTexts.Discord(saved.Draft.Title, saved.Draft.Text, null), shown);
        Assert.Equal("**Movie night**\nFriday at eight.\nBring snacks.", shown);
    }

    [Fact]
    public async Task AnEmptyTitle_IsNoTitle()
    {
        await using var services = await SetUpAsync(_db);

        var (preview, _, _, _) = await PreviewAsync(services, title: "   ", text: "Doors open at eight.");

        Assert.Equal("Doors open at eight.", Assert.Single(preview.Embeds).Description);
        Assert.Null(Assert.Single(services.Posts.Previews).Draft.Title);
    }

    [Fact]
    public async Task APostWithNothingToSay_IsAskedAgain()
    {
        await using var services = await SetUpAsync(_db);
        var form = await OpenFormAsync(services);

        var reply = await SubmitAsync(services, Submit(Caller, form.Id, "A title", "   "));

        Assert.Equal(PostCommand.WriteSomethingMessage, reply.Text);
        Assert.Null(reply.Actions);
        Assert.Empty(services.Posts.Previews);
        Assert.Equal("invalid", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task EverythingWrong_IsSaidAtOnce_AndNothingCanBePosted()
    {
        await using var services = await SetUpAsync(_db);
        services.Posts.Problems.AddRange(["That channel is not in the Discord server.", "Posting is paused in Modbot."]);
        var form = await OpenFormAsync(services);

        var reply = await SubmitAsync(services, Submit(Caller, form.Id, "A title", "Some text"));

        Assert.Equal("That channel is not in the Discord server.\nPosting is paused in Modbot.", reply.Text);
        Assert.Null(reply.Actions);
        Assert.Empty(services.Posts.SaveCalls);
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task OnlyThePersonWhoStartedIt_CanSendTheForm()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync("300", ModbotPermissions.ManagePosts, ct: Ct);
        var form = await OpenFormAsync(services);

        var reply = await SubmitAsync(services, Submit("300", form.Id, "A title", "Some text"));

        Assert.Equal(StaffInteractionHandler.NotYoursMessage, reply.Text);
        Assert.Empty(services.Posts.Previews);
    }

    [Fact]
    public async Task AFormOlderThanFifteenMinutes_HasRunOut()
    {
        await using var services = await SetUpAsync(_db);
        var form = await OpenFormAsync(services);
        services.Clock.Advance(TimeSpan.FromMinutes(16));

        var reply = await SubmitAsync(services, Submit(Caller, form.Id, "A title", "Some text"));

        Assert.Equal(StaffInteractionHandler.RunOutMessage, reply.Text);
        Assert.Empty(services.Posts.Previews);
    }

    [Fact]
    public async Task APermissionTakenAwayBeforeTheFormIsSent_IsARefusal_NotAPreview()
    {
        await using var services = await SetUpAsync(_db);
        var form = await OpenFormAsync(services);
        await ChangePermissionsAsync(services, Caller, ModbotPermissions.ViewPosts);

        var reply = await SubmitAsync(services, Submit(Caller, form.Id, "A title", "Some text"));

        Assert.Equal("You need the \"Manage posts\" permission in Modbot to use /post.", reply.Text);
        Assert.Empty(services.Posts.Previews);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AFormForAnotherCommand_IsNotThisOnes()
    {
        await using var services = await SetUpAsync(_db);

        var reply = await SubmitAsync(services, Submit(Caller, "modbot:form:note:v:usr_x", "t", "x"));

        Assert.Equal("Modbot does not know that form.", reply.Text);
    }

    // ── Post now ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostNow_SavesThePost_AsTheAccount_UnderTheConfirmationsKey()
    {
        await using var services = await SetUpAsync(_db);
        var account = await ReadAccountAsync(services, Caller);
        var (_, yes, _, token) = await PreviewAsync(services);
        var recorder = new Recorder();

        var reply = await PressAsync(services, Press(Caller, yes, recorder));

        // The preview is rewritten at once, then says what was done; nothing is a new message.
        Assert.Null(reply);
        Assert.Equal(["Posting…", $"Posting to <#{Channel}>."], recorder.Updates.Select(u => u.Text));
        Assert.All(recorder.Updates, u => Assert.Null(u.Actions));
        Assert.Empty(recorder.Replies);

        var (key, draft, by) = Assert.Single(services.Posts.Saved);
        Assert.Equal("discord:" + token, key);
        Assert.Equal(new PostDraft("Movie night", "Friday at eight.", Channel), draft);
        Assert.Equal((account.Id, account.Username), (by.UserId, by.Username));
        Assert.Equal(["asked", "done"], await OutcomesAsync(services));
    }

    [Fact]
    public async Task APressRepeated_SavesNothingMore()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);

        var first = new Recorder();
        var second = new Recorder();
        await PressAsync(services, Press(Caller, yes, first));
        await PressAsync(services, Press(Caller, yes, second));

        Assert.Single(services.Posts.SaveCalls);
        Assert.Single(services.Posts.Saved);
        Assert.Equal(first.Updates[^1].Text, second.Updates[^1].Text);
        Assert.Equal(["asked", "done", "repeat"], await OutcomesAsync(services));
    }

    [Fact]
    public async Task TwoPressesAtOnce_SaveOnePost()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);

        await Task.WhenAll(
            PressAsync(services, Press(Caller, yes, new Recorder())),
            PressAsync(services, Press(Caller, yes, new Recorder())),
            PressAsync(services, Press(Caller, yes, new Recorder())));

        Assert.Single(services.Posts.SaveCalls);
        Assert.Single(services.Posts.Saved);
    }

    [Fact]
    public async Task Cancel_SavesNothing_AndThePreviewIsGone()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, no, _) = await PreviewAsync(services);
        var recorder = new Recorder();

        Assert.Null(await PressAsync(services, Press(Caller, no, recorder)));

        Assert.Equal([StaffInteractionHandler.CancelledMessage], recorder.Updates.Select(u => u.Text));
        Assert.Empty(services.Posts.SaveCalls);

        var late = new Recorder();
        await PressAsync(services, Press(Caller, yes, late));

        Assert.Equal([StaffInteractionHandler.RunOutMessage], late.Updates.Select(u => u.Text));
        Assert.Empty(services.Posts.SaveCalls);
    }

    [Fact]
    public async Task OnlyThePersonWhoStartedIt_CanAnswerThePreview()
    {
        await using var services = await SetUpAsync(_db);
        await services.LinkedAccountAsync("300", ModbotPermissions.ManagePosts, ct: Ct);
        var (_, yes, no, _) = await PreviewAsync(services);

        var recorder = new Recorder();
        var refused = await PressAsync(services, Press("300", yes, recorder));
        var refusedCancel = await PressAsync(services, Press("300", no, recorder));

        Assert.Equal(StaffInteractionHandler.NotYoursMessage, refused?.Text);
        Assert.Equal(StaffInteractionHandler.NotYoursMessage, refusedCancel?.Text);
        Assert.Empty(recorder.Updates);
        Assert.Empty(services.Posts.SaveCalls);

        await PressAsync(services, Press(Caller, yes, new Recorder()));
        Assert.Single(services.Posts.Saved);
    }

    [Fact]
    public async Task APostPressedBeforeItsFormWasSent_HasNothingToPost()
    {
        await using var services = await SetUpAsync(_db);
        var form = await OpenFormAsync(services);
        var token = form.Id[PostCommand.FormPrefix.Length..];
        var recorder = new Recorder();

        await PressAsync(services, Press(Caller, PostCommand.YesButton + token, recorder));

        Assert.Equal([StaffInteractionHandler.RunOutMessage], recorder.Updates.Select(u => u.Text));
        Assert.Empty(services.Posts.SaveCalls);
    }

    [Fact]
    public async Task APermissionTakenAwayBeforeThePress_IsARefusal_NotAPost()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);
        await ChangePermissionsAsync(services, Caller, ModbotPermissions.ViewPosts);

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("You need the \"Manage posts\" permission in Modbot to use /post.", recorder.Updates[^1].Text);
        Assert.Empty(services.Posts.SaveCalls);
        Assert.Equal("no-permission", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnAccountDisabledBeforeThePress_IsARefusal_NotAPost()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);

        await using (var db = services.Database.NewContext())
        {
            var account = await db.Users.SingleAsync(u => u.DiscordUserId == Caller, Ct);
            account.IsDisabled = true;
            await db.SaveChangesAsync(Ct);
        }

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("Your Modbot account is disabled.", recorder.Updates[^1].Text);
        Assert.Empty(services.Posts.SaveCalls);
    }

    [Fact]
    public async Task ACommandSwitchedOffBeforeThePress_IsNotCarriedOut()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);
        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Post, false), Ct);

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("/post is turned off on this server.", recorder.Updates[^1].Text);
        Assert.Empty(services.Posts.SaveCalls);
        Assert.Equal("off", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task APreviewOlderThanFifteenMinutes_HasRunOut()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);
        services.Clock.Advance(TimeSpan.FromMinutes(16));

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal([StaffInteractionHandler.RunOutMessage], recorder.Updates.Select(u => u.Text));
        Assert.Empty(services.Posts.SaveCalls);
    }

    [Fact]
    public async Task WhatStopsThePostAtThePress_IsShownInThePreviewsPlace()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);

        // Switched off between the preview and the press.
        services.Posts.Problems.Add("Discord posts are switched off in Modbot.");

        var recorder = new Recorder();
        await PressAsync(services, Press(Caller, yes, recorder));

        Assert.Equal("Discord posts are switched off in Modbot.", recorder.Updates[^1].Text);
        Assert.Empty(services.Posts.Saved);
        Assert.Equal("refused", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task APressThatFails_DoesNotLeavePostingOnScreen_AndIsNotSentAgain()
    {
        await using var services = await SetUpAsync(_db);
        var (_, yes, _, _) = await PreviewAsync(services);
        services.Posts.SaveThrows = new InvalidOperationException("The database went away.");

        var first = new Recorder();
        await PressAsync(services, Press(Caller, yes, first));

        Assert.Equal(PostCommand.FailedMessage, first.Updates[^1].Text);
        Assert.Equal("error", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());

        services.Posts.SaveThrows = null;
        await PressAsync(services, Press(Caller, yes, new Recorder()));
        Assert.Single(services.Posts.SaveCalls);
    }

    [Fact]
    public async Task AnUnknownButton_IsToldSo()
    {
        await using var services = await SetUpAsync(_db);

        var reply = await PressAsync(services, Press(Caller, "modbot:post:maybe:abc", new Recorder()));

        Assert.Equal("Modbot does not know that button.", reply?.Text);
    }

    // ── The list ────────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Noon = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task List_ShowsTheWaitingPostsThenTheFailedOnes_EachWithItsTime_InTheOrderItWasGiven()
    {
        await using var services = await SetUpAsync(_db);
        services.Posts.List = new PostListAnswer(
            [
                new PostLine(Guid.NewGuid(), "Movie night", Noon, "Discord #announcements", null),
                new PostLine(Guid.NewGuid(), "Karaoke", Noon.AddHours(5), "Discord #events, VRChat", null),
            ],
            WaitingMore: 3,
            [new PostLine(Guid.NewGuid(), "Giveaway", Noon.AddDays(-1), "Discord #news", "Missing Access")],
            FailedMore: 0);

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostList, new Recorder()));

        Assert.Equal(["Waiting", "Failed"], reply!.Embeds.Select(e => e.Title));

        var waiting = reply.Embeds[0].Description!.Split('\n');
        Assert.Equal(
            [
                $"**Movie night** · <t:{Noon.ToUnixTimeSeconds()}:f> · Discord #announcements",
                $"**Karaoke** · <t:{Noon.AddHours(5).ToUnixTimeSeconds()}:f> · Discord #events, VRChat",
                "and 3 more",
            ],
            waiting);

        Assert.Equal(
            [$"**Giveaway** · <t:{Noon.AddDays(-1).ToUnixTimeSeconds()}:f> · Discord #news: Missing Access"],
            reply.Embeds[1].Description!.Split('\n'));

        // Ten of each, soonest first, is asked for; the order is the Marketing tab's own.
        var (most, _) = Assert.Single(services.Posts.Lists);
        Assert.Equal(10, most);
        Assert.Equal("answered", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task List_WithNothingWaiting_SaysSo()
    {
        await using var services = await SetUpAsync(_db);

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostList, new Recorder()));

        Assert.Equal("No posts are waiting.", reply?.Text);
    }

    [Fact]
    public async Task List_WithOnlyFailedPosts_ShowsOnlyThose()
    {
        await using var services = await SetUpAsync(_db);
        services.Posts.List = new PostListAnswer(
            [], 0, [new PostLine(Guid.NewGuid(), "Giveaway", Noon, "Discord #news", null)], 0);

        var reply = await RunAsync(services, Slash(Caller, DiscordCommands.PostList, new Recorder()));

        Assert.Equal(["Failed"], reply!.Embeds.Select(e => e.Title));
    }

    [Fact]
    public void AListLine_EscapesWhatSomebodyWrote()
    {
        var line = PostCommand.Line(new PostLine(Guid.NewGuid(), "**@everyone** [x](http://y)", Noon, "Discord #a_b", "`oops` <@1>"));

        Assert.StartsWith("**\\*\\*@everyone\\*\\* \\[x\\]\\(http://y\\)**", line, StringComparison.Ordinal);
        Assert.Contains("Discord #a\\_b", line, StringComparison.Ordinal);
        Assert.EndsWith(": \\`oops\\` \\<@1>", line, StringComparison.Ordinal);
    }

    // ── Several Modbots on one bot ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("modbot:post:yes:abc")]
    [InlineData("modbot:post:no:abc")]
    public void ThePreviewsButtons_AreAnsweredInPlace_AndOnlyInThisModbotsServer(string id)
    {
        Assert.True(PostCommand.IsButton(id));
        Assert.True(DiscordNetGateway.AnswersInPlace(id));
        Assert.True(DiscordNetGateway.IsOurButton("424242", 424242UL, id));
        Assert.False(DiscordNetGateway.IsOurButton("424242", 999UL, id));
        Assert.False(DiscordNetGateway.IsOurButton("424242", null, id));
    }

    [Fact]
    public void TheForm_IsOnlyAnsweredInThisModbotsServer()
    {
        const string id = "modbot:form:post:abc";

        Assert.True(PostCommand.IsForm(id));
        Assert.False(PostCommand.IsForm("modbot:form:act:abc"));
        Assert.True(DiscordNetGateway.IsOurButton("424242", 424242UL, id));
        Assert.False(DiscordNetGateway.IsOurButton("424242", 999UL, id));
        Assert.False(DiscordNetGateway.IsOurButton("424242", null, id));
    }

    private static async Task<ModbotUser> ReadAccountAsync(TestServices services, string discordUserId)
    {
        await using var db = services.Database.NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.DiscordUserId == discordUserId, Ct);
    }
}
