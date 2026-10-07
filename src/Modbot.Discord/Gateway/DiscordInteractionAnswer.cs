namespace Modbot.Discord.Gateway;

/// <summary>
/// The few things an interaction can be answered with, as the answer needs them. One implementation
/// speaks to Discord.Net; the tests have another, so the order of answers can be checked without a
/// socket.
/// </summary>
public interface IInteractionSender
{
    /// <summary>Whether the message the interaction came from can be rewritten: a button press can.</summary>
    bool CanRewrite { get; }

    /// <summary>
    /// Acknowledges with "thinking…". <paramref name="privately"/> fixes who sees the answer that
    /// follows: Discord keeps the deferral's audience for the first follow-up.
    /// </summary>
    Task DeferAsync(bool privately);

    /// <summary>Shows a form as the first answer.</summary>
    Task ShowFormAsync(DiscordForm form);

    /// <summary>The first answer, as a reply.</summary>
    Task RespondAsync(DiscordReply reply, bool privately);

    /// <summary>An answer after the first one.</summary>
    Task FollowupAsync(DiscordReply reply, bool privately);

    /// <summary>The first answer, rewriting the message the button sits on. Only when <see cref="CanRewrite"/>.</summary>
    Task RewriteAsync(DiscordReply reply);

    /// <summary>Rewrites the message the first answer rewrote. Only when <see cref="CanRewrite"/>.</summary>
    Task RewriteAgainAsync(DiscordReply reply);
}

/// <summary>
/// The one answer an interaction gets, in whichever form the handler chooses: a form, a reply, or a
/// rewrite of the message a button sits on. Acknowledges on the handler's behalf when it has said
/// nothing within <see cref="Within"/>, so a slow database is a late answer rather than Discord's
/// "This interaction failed".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Public or private is fixed up front.</strong> Discord decides who sees a reply when the
/// interaction is acknowledged, so a command that replies in public has to be acknowledged as
/// public, and every other as private. The caller reads that off the command's definition before it
/// makes this (<see cref="DiscordCommandDefinition.RepliesInPublic"/>) and passes it as
/// <c>inPublic</c>: both an answer given in time and the acknowledgement made for a slow handler
/// follow it.
/// </para>
/// <para>
/// A form is never public, and a button's or a form's own answer is always private.
/// </para>
/// </remarks>
public sealed class DiscordInteractionAnswer
{
    /// <summary>
    /// How long a handler has to show a form, reply or rewrite the message before the gateway
    /// acknowledges the interaction on its behalf. Discord allows three seconds in all, and the
    /// interaction has already spent some of them reaching Modbot.
    /// </summary>
    public static readonly TimeSpan DefaultWithin = TimeSpan.FromSeconds(2);

    private readonly IInteractionSender _sender;
    private readonly Serilog.ILogger _log;
    private readonly bool _privately;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Discord has had its first answer: a reply, a form, a rewrite or a defer.</summary>
    private bool _answered;

    /// <summary>The first answer was a form; nothing can follow it.</summary>
    private bool _formShown;

    /// <summary>The first answer rewrote the button's message, so later rewrites edit it again.</summary>
    private bool _rewriting;

    public DiscordInteractionAnswer(
        IInteractionSender sender,
        Serilog.ILogger log,
        bool inPublic = false,
        TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(log);

        _sender = sender;
        _log = log;
        _privately = !inPublic;
        Within = within ?? DefaultWithin;
    }

    /// <summary>How long the handler has before this acknowledges on its behalf.</summary>
    public TimeSpan Within { get; }

    /// <summary>Whether Discord has had its first answer.</summary>
    public bool Answered => _answered;

    /// <summary>Starts the clock: acknowledges after <see cref="Within"/> unless something answered first.</summary>
    public void AcknowledgeIfSilent()
        => _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Within).ConfigureAwait(false);
                await _gate.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (_answered)
                        return;

                    await _sender.DeferAsync(_privately).ConfigureAwait(false);
                    _answered = true;
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception e)
            {
                _log.Debug(e, "Could not acknowledge a slow interaction");
            }
        });

    /// <summary>
    /// Acknowledges now, before the handler runs: the answer then arrives as a new private
    /// message, after Discord's "thinking…". For a press that never shows a form.
    /// </summary>
    public async Task AcknowledgeNowAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_answered)
                return;

            await _sender.DeferAsync(_privately).ConfigureAwait(false);
            _answered = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShowFormAsync(DiscordForm form)
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_answered)
                throw new InvalidOperationException("Too late to show a form: Discord's three seconds are up.");

            await _sender.ShowFormAsync(form).ConfigureAwait(false);
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
                _log.Debug("A reply after a form was dropped: Discord takes nothing after a form");
                return;
            }

            if (!_answered)
            {
                await _sender.RespondAsync(reply, _privately).ConfigureAwait(false);
                _answered = true;
                return;
            }

            await _sender.FollowupAsync(reply, _privately).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rewrites the message the button sits on: text, cards and buttons, with no buttons when the
    /// reply has none. Where the interaction has no such message, or the handler was slow and the
    /// press was acknowledged as "thinking…", the answer is a reply instead.
    /// </summary>
    public async Task UpdateAsync(DiscordReply reply)
    {
        if (!_sender.CanRewrite)
        {
            await ReplyAsync(reply).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);

        var deferredAsLoading = false;

        try
        {
            if (!_answered)
            {
                await _sender.RewriteAsync(reply).ConfigureAwait(false);
                _answered = true;
                _rewriting = true;
                return;
            }

            if (_rewriting)
            {
                await _sender.RewriteAgainAsync(reply).ConfigureAwait(false);
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
