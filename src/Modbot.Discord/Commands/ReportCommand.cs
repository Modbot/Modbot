using Modbot.Analytics.Reports;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/report</c> and the "Report to mods" message menu: any member tells the mods about someone,
/// privately (Discord commands design §3.4, step 9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reporter is never named in Discord.</strong> Every reply here is private and says
/// nothing about who else was told or who reported. The reporter's id and name are kept in the report
/// row and shown only on the Reports page.
/// </para>
/// <para>
/// <strong>No <c>modbot.discord.command</c> fact</strong>, for a run, a refusal or a switched-off
/// command: its subject would be the reporter. The report's own fact
/// (<see cref="FactType.MemberReportOpened"/>, written by <see cref="MemberReports"/>) has the
/// reported person as its subject and carries no reporter and no words.
/// </para>
/// <para>
/// <strong>Limits are in the database</strong> (3 in 10 minutes, 10 a day, per reporter), so this
/// class keeps none of its own. Refused: yourself, the bot, and the bot's own messages; the same
/// message twice; a second open report on the same person.
/// </para>
/// <para>
/// The menu opens a form ("What's wrong?"). A form's id has room for 100 characters, not for a
/// message, so the message waits in <see cref="PendingReports"/> under a token the id carries.
/// </para>
/// </remarks>
public sealed class ReportCommand
{
    public const string SentMessage = "Sent to the mods.";
    public const string YourselfMessage = "You can't report yourself.";
    public const string BotMessage = "You can't report Modbot.";
    public const string SameMessageMessage = "You already reported this.";
    public const string AlreadyOpenMessage = "The mods already have your report.";
    public const string TooManyMessage = "Slow down. Try again in a few minutes.";
    public const string NothingWrittenMessage = "Write what happened first.";
    public const string TooLongMessage = "Keep it to 1,000 characters.";
    public const string PickSomeoneMessage = "Pick who you are reporting.";
    public const string NoMessageMessage = "Discord did not say which message that was.";
    public const string RunOutMessage = "That has run out. Use the menu on the message again.";
    public const string NotYoursMessage = "Only the person who opened this can send it.";
    public const string NotSetUpMessage = "This Modbot is not set up to take reports.";
    public const string TooLateMessage = "That took too long. Try again.";
    public const string TooManyWaitingMessage = "Too many things are waiting. Try again in a few minutes.";

    public const string FormTitle = "Report to mods";
    public const string WhatLabel = "What's wrong?";
    public const string WhatField = "what";

    /// <summary>A report form's id: <c>modbot:form:report:&lt;token&gt;</c>.</summary>
    public const string FormPrefix = DiscordActionButton.Prefix + "form:report:";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberReports _reports;
    private readonly PendingReports _pending;

    public ReportCommand(ModbotContext db, IModbotClock clock, MemberReports reports, PendingReports pending)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(pending);

        _db = db;
        _clock = clock;
        _reports = reports;
        _pending = pending;
    }

    /// <summary>Whether a form is a report form, rather than one of the staff forms.</summary>
    public static bool IsReportForm(string formId)
        => formId.StartsWith(FormPrefix, StringComparison.Ordinal) && formId.Length > FormPrefix.Length;

    /// <summary>The words that say why a report was not kept.</summary>
    public static string Words(MemberReportResult result) => result switch
    {
        MemberReportResult.Sent => SentMessage,
        MemberReportResult.Yourself => YourselfMessage,
        MemberReportResult.AlreadyReportedMessage => SameMessageMessage,
        MemberReportResult.AlreadyOpen => AlreadyOpenMessage,
        MemberReportResult.TooMany => TooManyMessage,
        MemberReportResult.NothingWritten => NothingWrittenMessage,
        _ => TooLongMessage,
    };

    // ── /report member: what: ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/report</c>. The caller has not been recorded and must not be: nothing here writes a
    /// <c>modbot.discord.command</c> fact.
    /// </summary>
    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.Report, ct).ConfigureAwait(false))
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Report, menu: false));

        var member = call.Option(DiscordCommands.MemberOption)?.Trim() ?? string.Empty;
        if (member.Length == 0)
            return DiscordReply.Say(PickSomeoneMessage);

        if (IsBot(member, gateway))
            return DiscordReply.Say(BotMessage);

        var outcome = await _reports.OpenAsync(
            new NewMemberReport(
                call.DiscordUserId,
                call.DiscordUsername,
                member,
                ReportedName: null,
                call.Option(DiscordCommands.ReportWhatOption) ?? string.Empty),
            ct).ConfigureAwait(false);

        return DiscordReply.Say(Words(outcome.Result));
    }

    // ── The message menu and its form ────────────────────────────────────────────────────────

    /// <summary>
    /// "Report to mods". Null means the form was shown; anything else is the reply to send. Anything
    /// that would refuse the report is said now, before the form: nobody writes a report that was
    /// always going to be turned away.
    /// </summary>
    public async Task<DiscordReply?> HandleMenuAsync(DiscordCommandCall call, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.ReportMenu, ct).ConfigureAwait(false))
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.ReportMenu, menu: true));

        if (call.TargetMessage is not { } message)
            return DiscordReply.Say(NoMessageMessage);

        // Modbot's own messages are the bot's, whoever the menu was used by.
        if (IsBot(message.AuthorId, gateway))
            return DiscordReply.Say(BotMessage);

        if (await _reports.CheckAsync(call.DiscordUserId, message.AuthorId, message.Id, ct).ConfigureAwait(false) is { } refusal)
            return DiscordReply.Say(Words(refusal));

        var token = PendingReports.NewToken();
        if (!_pending.TryAdd(new PendingReport(token, call.DiscordUserId, message, _clock.UtcNow), _clock.UtcNow))
            return DiscordReply.Say(TooManyWaitingMessage);

        try
        {
            await call.ShowFormAsync(Form(token), ct).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException)
        {
            return DiscordReply.Say(TooLateMessage);
        }
    }

    /// <summary>The form: one box, "What's wrong?", 1 to 1,000 characters.</summary>
    public static DiscordForm Form(string token)
        => new(
            FormTitle,
            FormPrefix + token,
            [
                new DiscordFormField(
                    WhatField,
                    WhatLabel,
                    DiscordFormFieldKind.LongText,
                    Required: true,
                    MaxLength: MemberReport.MaxTextLength),
            ]);

    /// <summary>The report form was sent.</summary>
    public async Task<DiscordReply> HandleFormAsync(DiscordFormSubmit submit, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submit);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.ReportMenu, ct).ConfigureAwait(false))
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.ReportMenu, menu: true));

        var token = submit.FormId[FormPrefix.Length..];
        if (_pending.Find(token, _clock.UtcNow) is not { } pending)
            return DiscordReply.Say(RunOutMessage);

        if (!string.Equals(pending.ReporterDiscordId, submit.DiscordUserId, StringComparison.Ordinal))
            return DiscordReply.Say(NotYoursMessage);

        var message = pending.Message;
        if (IsBot(message.AuthorId, gateway))
            return DiscordReply.Say(BotMessage);

        var outcome = await _reports.OpenAsync(
            new NewMemberReport(
                submit.DiscordUserId,
                submit.DiscordUsername,
                message.AuthorId,
                message.AuthorName,
                submit.Text(WhatField),
                new ReportedMessage(
                    message.Id,
                    message.ChannelId,
                    message.ChannelName,
                    message.SentAt,
                    message.Text,
                    message.AttachmentNames ?? [],
                    message.Url)),
            ct).ConfigureAwait(false);

        return DiscordReply.Say(Words(outcome.Result));
    }

    private static bool IsBot(string discordUserId, IDiscordGateway? gateway)
        => gateway?.BotUserId is { } bot && string.Equals(bot, discordUserId, StringComparison.Ordinal);
}
