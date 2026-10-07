using System.Globalization;
using Discord;
using Discord.WebSocket;

namespace Modbot.Discord.Gateway;

/// <summary>
/// Slash commands, right-click menus, buttons and forms: the interactions that can be answered with
/// a form, and so are not acknowledged before the handler runs (acting from Discord design §11).
/// </summary>
public sealed partial class DiscordNetGateway
{
    /// <summary>The line above a card that says who dealt with it.</summary>
    private const int HandledTextLength = 2000;

    public event Func<DiscordFormSubmit, Task>? FormSubmitted;

    private Task OnUserCommand(SocketUserCommand command)
    {
        if (!IsForThisServer(_guildId, command.GuildId))
        {
            // Not even acknowledged: another Modbot on the same bot may own it (see IsForThisServer).
            _log.Debug("Ignored the {Menu} menu from outside this Modbot's server", command.Data.Name);
            return Task.CompletedTask;
        }

        _ = Task.Run(() => Guard(DispatchMenuAsync(command, DiscordCommandKind.User), "menu"));
        return Task.CompletedTask;
    }

    private Task OnMessageCommand(SocketMessageCommand command)
    {
        if (!IsForThisServer(_guildId, command.GuildId))
        {
            _log.Debug("Ignored the {Menu} menu from outside this Modbot's server", command.Data.Name);
            return Task.CompletedTask;
        }

        _ = Task.Run(() => Guard(DispatchMenuAsync(command, DiscordCommandKind.Message), "menu"));
        return Task.CompletedTask;
    }

    private Task OnFormSubmitted(SocketModal form)
    {
        // The same test as a button: this server, and an id that is Modbot's.
        if (!IsOurButton(_guildId, form.GuildId, form.Data.CustomId))
        {
            _log.Debug("Ignored a form that is not this Modbot's to answer");
            return Task.CompletedTask;
        }

        _ = Task.Run(() => Guard(DispatchFormAsync(form), "form"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A slash command. Not acknowledged first, so it can answer with a form; acknowledged after two
    /// seconds if it has said nothing, and then as public or private by its definition. That is read
    /// here, <em>before</em> anything is acknowledged: Discord fixes a reply's audience when the
    /// interaction is acknowledged.
    /// </summary>
    private async Task DispatchAsync(SocketSlashCommand command)
    {
        var (subcommand, options) = ReadOptions(command.Data.Options);

        var answer = new DiscordInteractionAnswer(
            new SocketSender(command),
            _log,
            inPublic: RepliesInPublic(_registered, command.Data.Name, options, command.ChannelId, _publicReplies, _clock.UtcNow));
        answer.AcknowledgeIfSilent();

        var call = new DiscordCommandCall(
            Text(command.User.Id),
            command.User.Username,
            command.Data.Name,
            options,
            (reply, _) => answer.ReplyAsync(reply),
            (form, _) => answer.ShowFormAsync(form))
        {
            Subcommand = subcommand,
        };

        var handler = CommandReceived;
        if (handler is not null)
            await handler(call).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the answer to this run of a command is for the whole channel, from the commands
    /// registered and the options as typed. A command that is not registered, or not known, answers
    /// in private: nothing is made public by accident.
    /// </summary>
    public static bool RepliesInPublic(
        IReadOnlyList<DiscordCommandDefinition> registered, string commandName, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(options);

        var definition = registered.FirstOrDefault(c =>
            c.Kind == DiscordCommandKind.Slash && string.Equals(c.Name, commandName, StringComparison.Ordinal));

        return definition?.RepliesInPublic(options) ?? false;
    }

    /// <summary>
    /// <see cref="RepliesInPublic(IReadOnlyList{DiscordCommandDefinition}, string, IReadOnlyDictionary{string, string})"/>
    /// with the channel rule on top: a command that limits how often it answers in public
    /// (<see cref="DiscordCommandDefinition.PublicOncePer"/>) answers in private when it already
    /// answered in public in this channel inside that time. Still decided before anything is
    /// acknowledged, because the deferral fixes the audience.
    /// </summary>
    /// <param name="channelId">The channel the command was run in; null when Discord did not say.</param>
    /// <param name="now">From <c>IModbotClock</c>.</param>
    public static bool RepliesInPublic(
        IReadOnlyList<DiscordCommandDefinition> registered,
        string commandName,
        IReadOnlyDictionary<string, string> options,
        ulong? channelId,
        PublicReplyWindow window,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!RepliesInPublic(registered, commandName, options))
            return false;

        var definition = registered.First(c =>
            c.Kind == DiscordCommandKind.Slash && string.Equals(c.Name, commandName, StringComparison.Ordinal));

        // Nothing to count against without a channel, and no limit without a window.
        if (definition.PublicOncePer is not { } once || channelId is not { } channel)
            return true;

        return window.TryTake(definition.Name, channel, now, once);
    }

    /// <summary>
    /// What was typed after the command's name: the subcommand, when there is one, and every option
    /// by name. The options of a subcommand sit beside each other, not under it.
    /// </summary>
    private static (string? Subcommand, Dictionary<string, string> Options) ReadOptions(
        IEnumerable<SocketSlashCommandDataOption> typed)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? subcommand = null;

        void Walk(IEnumerable<SocketSlashCommandDataOption> level)
        {
            foreach (var option in level)
            {
                if (option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup)
                {
                    subcommand = subcommand is null ? option.Name : subcommand + " " + option.Name;
                    Walk(option.Options);
                    continue;
                }

                options[option.Name] = OptionText(option.Value);
            }
        }

        Walk(typed);
        return (subcommand, options);
    }

    /// <summary>
    /// One option's value as the text a <see cref="DiscordCommandCall"/> carries. A member or a
    /// channel picked from Discord's list arrives as the thing itself, and is carried by its id: the
    /// name it shows is somebody's to change, the id is not. A yes/no is <c>true</c> or <c>false</c>.
    /// </summary>
    public static string OptionText(object? value) => value switch
    {
        IUser user => Text(user.Id),
        IChannel channel => Text(channel.Id),
        IRole role => Text(role.Id),
        bool yes => yes ? "true" : "false",
        var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// What the person has already filled in while typing into another option: the subcommand, and
    /// every option but the one being typed and the ones left empty.
    /// </summary>
    public static (string? Subcommand, Dictionary<string, string> Options) OtherOptions(
        IEnumerable<(string Name, bool IsSubcommand, object? Value, bool Focused)> typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? subcommand = null;

        foreach (var (name, isSubcommand, value, focused) in typed)
        {
            if (isSubcommand)
            {
                subcommand = subcommand is null ? name : subcommand + " " + name;
                continue;
            }

            if (focused)
                continue;

            var text = OptionText(value);
            if (text.Length > 0)
                options[name] = text;
        }

        return (subcommand, options);
    }

    /// <summary>The options of an autocomplete request in the shape <see cref="OtherOptions"/> reads.</summary>
    private static (string? Subcommand, Dictionary<string, string> Options) OtherOptions(IEnumerable<AutocompleteOption> typed)
        => OtherOptions(typed.Select(o => (
            o.Name,
            o.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup,
            (object?)o.Value,
            o.Focused)));

    private async Task DispatchMenuAsync(SocketCommandBase command, DiscordCommandKind kind)
    {
        var answer = new DiscordInteractionAnswer(new SocketSender(command), _log);
        answer.AcknowledgeIfSilent();

        DiscordTargetUser? user = null;
        DiscordTargetMessage? message = null;

        if (command is SocketUserCommand onUser && onUser.Data.Member is { } member)
            user = new DiscordTargetUser(Text(member.Id), member.Username, member.IsBot || member.IsWebhook);

        if (command is SocketMessageCommand onMessage && onMessage.Data.Message is { } target)
        {
            message = new DiscordTargetMessage(
                Text(target.Id),
                Text(target.Channel?.Id ?? command.ChannelId ?? 0),
                target.Channel?.Name,
                Text(target.Author.Id),
                target.Author.Username,
                target.Author.IsBot || target.Author.IsWebhook || target.Source == MessageSource.Webhook,
                target.Content ?? string.Empty,
                target.Timestamp,
                target.GetJumpUrl(),
                [.. target.Attachments.Select(a => a.Filename)]);
        }

        var call = new DiscordCommandCall(
            Text(command.User.Id),
            command.User.Username,
            command.CommandName,
            new Dictionary<string, string>(StringComparer.Ordinal),
            (reply, _) => answer.ReplyAsync(reply),
            (form, _) => answer.ShowFormAsync(form))
        {
            Kind = kind,
            TargetUser = user,
            TargetMessage = message,
        };

        var handler = CommandReceived;
        if (handler is not null)
            await handler(call).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a press may be answered with a form or by rewriting the message its button sits on,
    /// and so must not be acknowledged before the handler runs: the staff buttons under cards,
    /// lookups and confirmations (acting from Discord design §11).
    /// </summary>
    /// <remarks>
    /// Every other button -- the join gate's, in the server and in a direct message, and
    /// <c>/me</c>'s -- answers with a new private message, so it is acknowledged the moment it
    /// arrives, as it always was. The join gate takes a lock and reads the settings, the member and
    /// their row before it answers; the acknowledge-after-two-seconds rule would leave it a second
    /// of Discord's three.
    /// </remarks>
    public static bool AnswersInPlace(string buttonId)
        => buttonId is { Length: > 0 }
            && (Interactions.StaffMenus.IsStaffButton(buttonId)
                || Commands.EventCommand.IsButton(buttonId)
                || Commands.PostCommand.IsButton(buttonId));

    private async Task DispatchButtonAsync(SocketMessageComponent press, string id)
    {
        var answer = new DiscordInteractionAnswer(new SocketSender(press), _log);

        // A staff button may open a form, which has to be the first answer, so it is not
        // acknowledged first; everything else is, straight away.
        if (AnswersInPlace(id))
            answer.AcknowledgeIfSilent();
        else
            await answer.AcknowledgeNowAsync().ConfigureAwait(false);

        // A card in a channel, as opposed to a reply only the presser sees: only a card can be
        // marked as dealt with afterwards.
        var card = press.Message is { } on && !(on.Flags is { } flags && flags.HasFlag(MessageFlags.Ephemeral))
            ? on
            : null;
        var channelId = card?.Channel?.Id ?? press.ChannelId;

        var call = new DiscordButtonPress(
            Text(press.User.Id),
            press.User.Username,
            id,
            (reply, _) => answer.ReplyAsync(reply),
            (form, _) => answer.ShowFormAsync(form),
            (reply, _) => answer.UpdateAsync(reply))
        {
            CardChannelId = card is not null && channelId is { } c ? Text(c) : null,
            CardMessageId = card is not null ? Text(card.Id) : null,
        };

        var handler = ButtonPressed;
        if (handler is not null)
            await handler(call).ConfigureAwait(false);
    }

    private async Task DispatchFormAsync(SocketModal form)
    {
        var answer = new DiscordInteractionAnswer(new SocketSender(form), _log);
        answer.AcknowledgeIfSilent();

        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var field in form.Data.Components)
        {
            if (string.IsNullOrEmpty(field.CustomId))
                continue;

            // A list left empty arrives with no values, or with one empty one: either way it is
            // nothing picked, never a pick of "".
            values[field.CustomId] = field.Type == ComponentType.TextInput
                ? [field.Value ?? string.Empty]
                : [.. (field.Values ?? []).Where(v => !string.IsNullOrEmpty(v))];
        }

        var submit = new DiscordFormSubmit(
            Text(form.User.Id),
            form.User.Username,
            form.Data.CustomId,
            values,
            (reply, _) => answer.ReplyAsync(reply));

        var handler = FormSubmitted;
        if (handler is not null)
            await handler(submit).ConfigureAwait(false);
    }

    public async Task<DiscordPostOutcome> MarkHandledAsync(
        string channelId, string messageId, string line, string removeButtonsStarting, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(removeButtonsStarting);

        if (!ulong.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return DiscordPostOutcome.Failed("That is not a Discord message id.", permanent: true);

        return await InChannelAsync(channelId, async channel =>
        {
            if (await channel.GetMessageAsync(id).ConfigureAwait(false) is not IUserMessage card
                || _client.CurrentUser is not { } me
                || card.Author.Id != me.Id)
            {
                return DiscordPostOutcome.Failed("That message is gone, or was not posted by the bot.", permanent: true);
            }

            // A line above the card rather than a change inside it: the card's embed and its
            // picture are left exactly as they were posted, and a second action adds a second line.
            var text = string.IsNullOrEmpty(card.Content) ? line : card.Content + "\n" + line;
            if (text.Length > HandledTextLength)
                text = text[..HandledTextLength];

            var buttons = new ComponentBuilder();
            foreach (var row in card.Components.OfType<ActionRowComponent>())
            {
                foreach (var button in row.Components.OfType<ButtonComponent>())
                {
                    if (button.CustomId is { } custom && custom.StartsWith(removeButtonsStarting, StringComparison.Ordinal))
                        continue;

                    buttons.WithButton(button.ToBuilder());
                }
            }

            await card.ModifyAsync(m =>
            {
                m.Content = text;
                m.Components = buttons.Build();
                m.AllowedMentions = AllowedMentions.None;
            }).ConfigureAwait(false);

            return DiscordPostOutcome.Posted(messageId);
        }).ConfigureAwait(false);
    }

    /// <summary>A form as Discord's builder wants it. Every field sits under its own label.</summary>
    internal static Modal ToModal(DiscordForm form)
    {
        var builder = new ModalBuilder()
            .WithTitle(Cut(form.Title, 45))
            .WithCustomId(form.Id);

        foreach (var field in form.Fields)
        {
            IMessageComponentBuilder input = field.Kind == DiscordFormFieldKind.Choice
                ? new SelectMenuBuilder()
                    .WithCustomId(field.Id)
                    .WithPlaceholder(field.Placeholder is { Length: > 0 } hint ? Cut(hint, 150) : null)
                    .WithMinValues(field.Required ? 1 : 0)
                    .WithMaxValues(Math.Clamp(field.MaxChoices, 1, Math.Max(1, field.Choices?.Count ?? 1)))
                    .WithRequired(field.Required)
                    .WithOptions([.. (field.Choices ?? []).Take(25).Select(c => new SelectMenuOptionBuilder(
                        Cut(c.Label, 100),
                        c.Value,
                        c.Description is { Length: > 0 } d ? Cut(d, 100) : null))])
                : new TextInputBuilder()
                    .WithCustomId(field.Id)
                    .WithStyle(field.Kind == DiscordFormFieldKind.LongText ? TextInputStyle.Paragraph : TextInputStyle.Short)
                    .WithRequired(field.Required)
                    .WithMaxLength(Math.Clamp(field.MaxLength ?? 4000, 1, 4000))
                    .WithPlaceholder(field.Placeholder is { Length: > 0 } hint2 ? Cut(hint2, 100) : null);

            builder.AddLabel(new LabelBuilder(Cut(field.Label, 45), input));
        }

        return builder.Build();
    }

    /// <summary>Discord's limit, never splitting an emoji in half (Discord refuses half of one).</summary>
    private static string Cut(string text, int length) => Interactions.StaffInteractionHandler.Cut(text, length);

    /// <summary>
    /// A Discord.Net interaction as <see cref="DiscordInteractionAnswer"/> talks to it. A button
    /// press is acknowledged as "thinking…" in place of a plain defer, and is the one kind whose
    /// message can be rewritten.
    /// </summary>
    private sealed class SocketSender(SocketInteraction interaction) : IInteractionSender
    {
        private SocketMessageComponent? Press => interaction as SocketMessageComponent;

        public bool CanRewrite => Press is not null;

        public Task DeferAsync(bool privately)
            => Press is { } press
                ? press.DeferLoadingAsync(privately)
                : interaction.DeferAsync(privately);

        public Task ShowFormAsync(DiscordForm form)
            => interaction.RespondWithModalAsync(ToModal(form));

        public Task RespondAsync(DiscordReply reply, bool privately)
            => SendAsync(reply, privately, first: true);

        public Task FollowupAsync(DiscordReply reply, bool privately)
            => SendAsync(reply, privately, first: false);

        private async Task SendAsync(DiscordReply reply, bool privately, bool first)
        {
            var embeds = reply.Embeds.Count == 0 ? null : reply.Embeds.Select(ToEmbed).ToArray();
            var buttons = Buttons(reply.Links, reply.Actions) is { Components.Count: > 0 } b ? b : null;
            var files = Files(reply.Pictures);

            try
            {
                if (files.Count > 0)
                {
                    if (first)
                    {
                        await interaction.RespondWithFilesAsync(
                                attachments: files,
                                text: reply.Text,
                                embeds: embeds,
                                ephemeral: privately,
                                allowedMentions: AllowedMentions.None,
                                components: buttons)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await interaction.FollowupWithFilesAsync(
                                attachments: files,
                                text: reply.Text,
                                embeds: embeds,
                                ephemeral: privately,
                                allowedMentions: AllowedMentions.None,
                                components: buttons)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                if (first)
                {
                    await interaction.RespondAsync(
                            text: reply.Text,
                            embeds: embeds,
                            ephemeral: privately,
                            allowedMentions: AllowedMentions.None,
                            components: buttons)
                        .ConfigureAwait(false);
                }
                else
                {
                    await interaction.FollowupAsync(
                            text: reply.Text,
                            embeds: embeds,
                            ephemeral: privately,
                            allowedMentions: AllowedMentions.None,
                            components: buttons)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var file in files)
                    file.Dispose();
            }
        }

        /// <summary>Text, cards and buttons, with no buttons when the reply has none. Pictures are not sent.</summary>
        private static void Fill(MessageProperties m, DiscordReply reply)
        {
            m.Content = reply.Text ?? string.Empty;
            m.Embeds = reply.Embeds.Select(ToEmbed).ToArray();
            m.Components = Buttons(reply.Links, reply.Actions);
            m.AllowedMentions = AllowedMentions.None;
        }

        public Task RewriteAsync(DiscordReply reply)
            => Press!.UpdateAsync(m => Fill(m, reply));

        public Task RewriteAgainAsync(DiscordReply reply)
            => Press!.ModifyOriginalResponseAsync(m => Fill(m, reply));
    }
}
