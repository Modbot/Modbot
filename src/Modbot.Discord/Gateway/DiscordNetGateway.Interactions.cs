using System.Globalization;
using Discord;
using Discord.WebSocket;

namespace Modbot.Discord.Gateway;

/// <summary>
/// Right-click menus, buttons and forms: the interactions that can be answered with a form, and so
/// are not acknowledged before the handler runs (acting from Discord design §11).
/// </summary>
public sealed partial class DiscordNetGateway
{
    /// <summary>
    /// How long a handler has to show a form, reply or rewrite the message before the gateway
    /// acknowledges the interaction on its behalf. Discord allows three seconds in all, and the
    /// interaction has already spent some of them reaching Modbot.
    /// </summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(2);

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

    private async Task DispatchMenuAsync(SocketCommandBase command, DiscordCommandKind kind)
    {
        var answer = new InteractionAnswer(command, _log);
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
                target.GetJumpUrl());
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

    private async Task DispatchButtonAsync(SocketMessageComponent press, string id)
    {
        // Not acknowledged first: the press may open a form, which has to be the first answer.
        var answer = new InteractionAnswer(press, _log);
        answer.AcknowledgeIfSilent();

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
        var answer = new InteractionAnswer(form, _log);
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
    /// The one answer an interaction gets, in whichever form the handler chooses: a form, a reply,
    /// or a rewrite of the message a button sits on. Acknowledges on the handler's behalf when it
    /// has said nothing within <see cref="AnswerWithin"/>, so a slow database is a late answer
    /// rather than Discord's "This interaction failed".
    /// </summary>
    private sealed class InteractionAnswer(SocketInteraction interaction, Serilog.ILogger log)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Discord has had its first answer: a reply, a form, a rewrite or a defer.</summary>
        private bool _answered;

        /// <summary>The first answer was a form; nothing can follow it.</summary>
        private bool _formShown;

        /// <summary>The first answer rewrote the button's message, so later rewrites edit it again.</summary>
        private bool _rewriting;

        public void AcknowledgeIfSilent()
            => _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AnswerWithin).ConfigureAwait(false);
                    await _gate.WaitAsync().ConfigureAwait(false);

                    try
                    {
                        if (_answered)
                            return;

                        if (interaction is SocketMessageComponent press)
                            await press.DeferLoadingAsync(ephemeral: true).ConfigureAwait(false);
                        else
                            await interaction.DeferAsync(ephemeral: true).ConfigureAwait(false);

                        _answered = true;
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (Exception e)
                {
                    log.Debug(e, "Could not acknowledge a slow interaction");
                }
            });

        public async Task ShowFormAsync(DiscordForm form)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_answered)
                    throw new InvalidOperationException("Too late to show a form: Discord's three seconds are up.");

                await interaction.RespondWithModalAsync(ToModal(form)).ConfigureAwait(false);
                _answered = true;
                _formShown = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ReplyAsync(DiscordReply reply)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_formShown)
                {
                    log.Debug("A reply after a form was dropped: Discord takes nothing after a form");
                    return;
                }

                var embeds = reply.Embeds.Count == 0 ? null : reply.Embeds.Select(ToEmbed).ToArray();
                var buttons = Buttons(reply.Links, reply.Actions) is { Components.Count: > 0 } b ? b : null;
                var files = Files(reply.Pictures);

                try
                {
                    if (!_answered)
                    {
                        if (files.Count > 0)
                        {
                            await interaction.RespondWithFilesAsync(
                                    attachments: files,
                                    text: reply.Text,
                                    embeds: embeds,
                                    ephemeral: true,
                                    allowedMentions: AllowedMentions.None,
                                    components: buttons)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await interaction.RespondAsync(
                                    text: reply.Text,
                                    embeds: embeds,
                                    ephemeral: true,
                                    allowedMentions: AllowedMentions.None,
                                    components: buttons)
                                .ConfigureAwait(false);
                        }

                        _answered = true;
                        return;
                    }

                    if (files.Count > 0)
                    {
                        await interaction.FollowupWithFilesAsync(
                                attachments: files,
                                text: reply.Text,
                                embeds: embeds,
                                ephemeral: true,
                                allowedMentions: AllowedMentions.None,
                                components: buttons)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await interaction.FollowupAsync(
                                text: reply.Text,
                                embeds: embeds,
                                ephemeral: true,
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
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Rewrites the message the button sits on: text, cards and buttons, with no buttons when the
        /// reply has none. Pictures are not sent; nothing that rewrites a message today has one.
        /// </summary>
        public async Task UpdateAsync(DiscordReply reply)
        {
            if (interaction is not SocketMessageComponent press)
            {
                await ReplyAsync(reply).ConfigureAwait(false);
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);

            var deferredAsLoading = false;

            try
            {
                void Fill(MessageProperties m)
                {
                    m.Content = reply.Text ?? string.Empty;
                    m.Embeds = reply.Embeds.Select(ToEmbed).ToArray();
                    m.Components = Buttons(reply.Links, reply.Actions);
                    m.AllowedMentions = AllowedMentions.None;
                }

                if (!_answered)
                {
                    await press.UpdateAsync(Fill).ConfigureAwait(false);
                    _answered = true;
                    _rewriting = true;
                    return;
                }

                if (_rewriting)
                {
                    await press.ModifyOriginalResponseAsync(Fill).ConfigureAwait(false);
                    return;
                }

                // Acknowledged as "thinking…" because the handler was slow: the answer becomes a
                // new private message instead.
                deferredAsLoading = true;
            }
            finally
            {
                _gate.Release();
            }

            if (deferredAsLoading)
                await ReplyAsync(reply).ConfigureAwait(false);
        }
    }
}
