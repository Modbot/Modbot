using System.Globalization;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>A post that was written in the form and waits for Post now.</summary>
/// <param name="ChannelId">The channel picked on the command.</param>
/// <param name="Ready">The form was sent and checked, so Post now may save it.</param>
public sealed record PendingPost(
    string Token,
    string DiscordUserId,
    DateTimeOffset StartedAt,
    string ChannelId,
    string? Title,
    string Text,
    bool Ready) : IPendingConfirmation
{
    public PostDraft Draft => new(Title, Text, ChannelId);
}

/// <summary>
/// <c>/post</c>: write a Discord post now, and list the posts waiting to go out (Discord commands
/// design §3.7 and §4, step 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The Marketing tab's own code.</strong> Everything goes through <see cref="IPostActions"/>,
/// which the API implements with the code the composer's preview and Schedule use
/// (<c>PostRequests.CheckAsync</c>, <see cref="PostTexts"/>) and the post rows the Discord sender
/// (<c>PostDiscordSender</c>) takes. The preview is the message the sender will build, and Post now
/// saves a post that is due now: it does not send. The sender claims it, tries once and never sends
/// it again by itself, so a press, a second press, a crash and a restart can each make at most one
/// message.
/// </para>
/// <para>
/// <strong>The flow</strong> is <c>/post new channel:</c> → a form (Title, Text) → a private preview
/// with <strong>Post now</strong> and <strong>Cancel</strong> → a saved post. The caller's account and
/// permission are checked at the command, at the form and at the press. Only the Discord site is
/// offered; the VRChat and Bluesky choices belong to the composer.
/// </para>
/// <para>
/// <strong><c>list</c></strong> shows the next ten posts waiting to go out and the posts that failed,
/// each with its time. It needs See posts; <c>new</c> needs Manage posts, as in the web app.
/// </para>
/// </remarks>
public sealed class PostCommand
{
    /// <summary>The form: <c>modbot:form:post:&lt;token&gt;</c>.</summary>
    public const string FormPrefix = DiscordActionButton.Prefix + "form:post:";

    /// <summary>Every button of this command starts with this.</summary>
    public const string ButtonPrefix = DiscordActionButton.Prefix + "post:";

    /// <summary>The preview's Post now: <c>modbot:post:yes:&lt;token&gt;</c>.</summary>
    public const string YesButton = ButtonPrefix + "yes:";

    /// <summary>The preview's Cancel: <c>modbot:post:no:&lt;token&gt;</c>.</summary>
    public const string NoButton = ButtonPrefix + "no:";

    public const string TitleField = "title";
    public const string TextField = "text";

    public const string PickChannelMessage = "Pick a channel.";
    public const string WriteSomethingMessage = "Write some text.";
    public const string NothingWaitingMessage = "No posts are waiting.";
    public const string PostingMessage = "Posting…";
    public const string PostNowLabel = "Post now";
    public const string CancelLabel = "Cancel";
    public const string FailedMessage = "Something went wrong on Modbot's side. Check the posts in Modbot before pressing again.";

    /// <summary>How many posts each part of the list shows.</summary>
    public const int ListMost = 10;

    /// <summary>A line's own words, cut: a headline, then where, then what went wrong.</summary>
    private const int HeadlineLength = 80;

    private const int ErrorLength = 100;

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly PendingConfirmations<PendingPost, PostNowAnswer> _pending;
    private readonly IPostActions? _posts;

    public PostCommand(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        PendingConfirmations<PendingPost, PostNowAnswer> pending,
        IPostActions? posts = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pending);

        _db = db;
        _facts = facts;
        _clock = clock;
        _pending = pending;
        _posts = posts;
    }

    /// <summary>Whether a form is this command's.</summary>
    public static bool IsForm(string formId) => formId.StartsWith(FormPrefix, StringComparison.Ordinal);

    /// <summary>Whether a press is this command's.</summary>
    public static bool IsButton(string buttonId) => buttonId.StartsWith(ButtonPrefix, StringComparison.Ordinal);

    // ── The command ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/post new</c> opens the form and returns null; <c>/post list</c> and every refusal return
    /// the answer.
    /// </summary>
    public async Task<StaffCommandAnswer?> RunAsync(DiscordCommandCall call, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        if (_posts is null)
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), "refused");

        return call.Subcommand switch
        {
            DiscordCommands.PostList => await ListAsync(user, ct).ConfigureAwait(false),
            DiscordCommands.PostNew => await OpenFormAsync(call).ConfigureAwait(false),
            _ => new StaffCommandAnswer(DiscordReply.Say("Pick new or list."), "invalid"),
        };
    }

    private async Task<StaffCommandAnswer?> OpenFormAsync(DiscordCommandCall call)
    {
        var channel = call.Option(DiscordCommands.PostChannelOption)?.Trim() ?? string.Empty;
        if (channel.Length == 0)
            return new StaffCommandAnswer(DiscordReply.Say(PickChannelMessage), "invalid");

        var now = _clock.UtcNow;
        var pending = new PendingPost(
            PendingConfirmations<PendingPost, PostNowAnswer>.NewToken(),
            call.DiscordUserId,
            now,
            channel,
            null,
            string.Empty,
            Ready: false);

        if (!_pending.TryAdd(pending, now))
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.TooManyMessage), "refused");

        try
        {
            await call.ShowFormAsync(Form(pending.Token)).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Discord's three seconds are gone: the form can no longer be shown.
            _pending.Forget(pending.Token);
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.TooLateMessage), "refused");
        }
    }

    /// <summary>The form: a title that may be left empty, and the text. Public for tests.</summary>
    public static DiscordForm Form(string token)
        => new(
            "Write a post",
            FormPrefix + token,
            [
                new DiscordFormField(TitleField, "Title", DiscordFormFieldKind.ShortText, Required: false, MaxLength: Post.MaxTitleLength),
                new DiscordFormField(TextField, "Text", DiscordFormFieldKind.LongText, Required: true, MaxLength: PostTexts.DiscordLimit),
            ]);

    private async Task<StaffCommandAnswer> ListAsync(ModbotUser user, CancellationToken ct)
    {
        var list = await _posts!.ListAsync(ListMost, CommandAccess.Member(user), ct).ConfigureAwait(false);

        if (list.Waiting.Count == 0 && list.Failed.Count == 0)
            return new StaffCommandAnswer(DiscordReply.Say(NothingWaitingMessage), "answered");

        return new StaffCommandAnswer(ListReply(list), "answered");
    }

    /// <summary>The next waiting posts, then the failed ones, each with its time. Public for tests.</summary>
    public static DiscordReply ListReply(PostListAnswer list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var embeds = new List<DiscordEmbedContent>(2);

        if (list.Waiting.Count > 0)
            embeds.Add(Part("Waiting", list.Waiting, list.WaitingMore, CardColour.Violet));

        if (list.Failed.Count > 0)
            embeds.Add(Part("Failed", list.Failed, list.FailedMore, CardColour.Red));

        return new DiscordReply(null, embeds);
    }

    private static DiscordEmbedContent Part(string title, IReadOnlyList<PostLine> lines, int more, uint colour)
    {
        var text = lines.Select(Line).ToList();

        if (more > 0)
            text.Add($"and {more.ToString(CultureInfo.InvariantCulture)} more");

        return new DiscordEmbedContent(
            title,
            DiscordCommandHandler.WholeLines(text, 4096),
            colour,
            [],
            null,
            null,
            null);
    }

    /// <summary><c>**Headline** · &lt;t:…:f&gt; · Discord #channel</c>, and for a failed post what the site said.</summary>
    public static string Line(PostLine post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var line = $"**{CardText.EscapeName(CardText.Plain(post.Headline, HeadlineLength))}**";

        if (post.SendAt is { } at)
            line += " · " + DiscordTime.Absolute(at);

        if (post.Where.Length > 0)
            line += " · " + CardText.EscapeName(post.Where);

        if (!string.IsNullOrWhiteSpace(post.Error))
            line += ": " + CardText.Fit(CardText.EscapeText(CardText.Plain(post.Error, ErrorLength)), ErrorLength + 20);

        return line;
    }

    // ── The form ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The form was sent: check who is sending, check the post exactly as saving it would, and show
    /// what it would say with Post now and Cancel.
    /// </summary>
    public async Task<DiscordReply> HandleFormAsync(DiscordFormSubmit submit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submit);

        if (StaffMenus.TokenAfter(submit.FormId, FormPrefix) is not { } token)
            return DiscordReply.Say("Modbot does not know that form.");

        if (_pending.Get(token, _clock.UtcNow) is not { } pending)
            return DiscordReply.Say(StaffInteractionHandler.RunOutMessage);

        if (pending.DiscordUserId != submit.DiscordUserId)
            return DiscordReply.Say(StaffInteractionHandler.NotYoursMessage);

        var about = new JsonObject { ["subcommand"] = DiscordCommands.PostNew };

        var (user, refusal, outcome) = await Access(submit.DiscordUserId, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await Record(submit.DiscordUserId, user, outcome, about, ct).ConfigureAwait(false);
            return refusal;
        }

        if (_posts is null)
            return DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage);

        var title = submit.Text(TitleField);
        var text = submit.Text(TextField);

        if (text.Length == 0)
        {
            await Record(submit.DiscordUserId, user, "invalid", about, ct).ConfigureAwait(false);
            return DiscordReply.Say(WriteSomethingMessage);
        }

        var draft = new PostDraft(title.Length == 0 ? null : title, text, pending.ChannelId);
        var preview = await _posts.PreviewAsync(draft, CommandAccess.Member(user!), ct).ConfigureAwait(false);

        if (!preview.Ready)
        {
            await Record(submit.DiscordUserId, user, "refused", about, ct).ConfigureAwait(false);
            return DiscordReply.Say(string.Join('\n', preview.Problems.Select(CardText.EscapeText)));
        }

        _pending.Update(pending with { Title = draft.Title, Text = draft.Text, Ready = true });
        await Record(submit.DiscordUserId, user, "asked", about, ct).ConfigureAwait(false);

        return Preview(pending.Token, pending.ChannelId, preview);
    }

    /// <summary>
    /// The private preview: the message the sender will build, word for word, in an embed, with
    /// Post now and Cancel. Public for tests.
    /// </summary>
    public static DiscordReply Preview(string token, string channelId, PostPreviewAnswer preview)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(preview);

        var where = string.IsNullOrWhiteSpace(preview.ChannelName)
            ? "Post"
            : "Post to #" + CardText.Plain(preview.ChannelName, 100);

        return new DiscordReply(
            $"Post this to <#{channelId}>?",
            [new DiscordEmbedContent(where, preview.Content, CardColour.Violet, [], null, null, null)],
            null,
            null,
            [
                new DiscordActionButton(PostNowLabel, YesButton + token, DiscordButtonStyle.Main),
                new DiscordActionButton(CancelLabel, NoButton + token),
            ]);
    }

    // ── The press ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press on the preview's buttons. Null means it was answered already, by rewriting the
    /// preview; anything else is the reply to send.
    /// </summary>
    public async Task<DiscordReply?> HandleButtonAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (StaffMenus.TokenAfter(press.ButtonId, YesButton) is { } yes)
            return await ConfirmAsync(press, yes, ct).ConfigureAwait(false);

        if (StaffMenus.TokenAfter(press.ButtonId, NoButton) is { } no)
        {
            if (_pending.Get(no, _clock.UtcNow) is { } held && held.DiscordUserId != press.DiscordUserId)
                return DiscordReply.Say(StaffInteractionHandler.NotYoursMessage);

            _pending.Forget(no);
            await press.UpdateAsync(DiscordReply.Say(StaffInteractionHandler.CancelledMessage), ct).ConfigureAwait(false);
            return null;
        }

        return DiscordReply.Say("Modbot does not know that button.");
    }

    /// <summary>
    /// Post now. Checks who is pressing again, rewrites the preview so its buttons are gone at
    /// once, and saves the post once: a second press gets the first answer and saves nothing.
    /// </summary>
    private async Task<DiscordReply?> ConfirmAsync(DiscordButtonPress press, string token, CancellationToken ct)
    {
        if (_pending.Get(token, _clock.UtcNow) is not { Ready: true } pending)
        {
            await press.UpdateAsync(DiscordReply.Say(StaffInteractionHandler.RunOutMessage), ct).ConfigureAwait(false);
            return null;
        }

        if (pending.DiscordUserId != press.DiscordUserId)
            return DiscordReply.Say(StaffInteractionHandler.NotYoursMessage);

        // The first answer, before anything that reads the database: it is what removes the
        // buttons, and every later answer edits the same message.
        await press.UpdateAsync(DiscordReply.Say(PostingMessage), ct).ConfigureAwait(false);

        var about = new JsonObject { ["subcommand"] = DiscordCommands.PostNew, ["channel"] = pending.ChannelId };

        var (user, refusal, outcome) = await Access(press.DiscordUserId, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await Record(press.DiscordUserId, user, outcome, about, ct).ConfigureAwait(false);
            await press.UpdateAsync(refusal, ct).ConfigureAwait(false);
            return null;
        }

        if (_posts is null)
        {
            await press.UpdateAsync(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), ct).ConfigureAwait(false);
            return null;
        }

        PostNowAnswer answer;
        var first = true;

        try
        {
            var posts = _posts;
            var member = CommandAccess.Member(user!);

            // The post row's id is made from this key, so even a press that gets past the in-memory
            // claim (a restart in between) cannot make a second row.
            (answer, first) = await _pending
                .RunOnceAsync(token, () => posts.PostNowAsync("discord:" + token, pending.Draft, member, ct))
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Record(press.DiscordUserId, user, "error", about, ct).ConfigureAwait(false);
            await press.UpdateAsync(DiscordReply.Say(FailedMessage), ct).ConfigureAwait(false);
            return null;
        }

        await press.UpdateAsync(Answer(pending, answer), ct).ConfigureAwait(false);

        var result = answer switch
        {
            { Created: true } when first => "done",
            { Created: true } or { Repeat: true } => "repeat",
            _ => "refused",
        };

        await Record(press.DiscordUserId, user, result, about, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>What the preview says once the post is saved, or why it was not. Public for tests.</summary>
    public static DiscordReply Answer(PendingPost pending, PostNowAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(answer);

        if (answer.Created || answer.Repeat)
            return DiscordReply.Say($"Posting to <#{pending.ChannelId}>.");

        return DiscordReply.Say(string.Join('\n', answer.Problems.Select(CardText.EscapeText)));
    }

    // ── Who is acting ────────────────────────────────────────────────────────────────────────

    /// <summary>The account, enabled, with a VRChat link, holding Manage posts, and the command still switched on.</summary>
    private async Task<(ModbotUser? User, DiscordReply? Refusal, string Outcome)> Access(string discordUserId, CancellationToken ct)
    {
        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.Post, ct).ConfigureAwait(false))
            return (null, DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Post, menu: false)), "off");

        return await CommandAccess
            .StaffAsync(_db, _clock, discordUserId, ModbotPermissions.ManagePosts, writes: true, DiscordCommands.Post, ct)
            .ConfigureAwait(false);
    }

    private Task Record(string discordUserId, ModbotUser? user, string outcome, JsonObject about, CancellationToken ct)
        => CommandAccess.RecordAsync(_facts, _clock, discordUserId, user, DiscordCommands.Post, outcome, about, ct);
}
