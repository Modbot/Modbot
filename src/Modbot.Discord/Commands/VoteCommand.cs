using System.Globalization;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Votes;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/vote</c>: asks the members of the server to vote on a question, in a channel the caller picks.
/// </summary>
/// <remarks>
/// <para>
/// For event hosts: Modbot asks Manage posts, like <c>/post new</c>, and the caller's Modbot account
/// needs a VRChat link because it writes. The command takes the channel and, if the vote is not for
/// everybody, the role; it answers with a form for the question and the options, one to a line. The
/// channel and the role ride in the form's id, which Discord hands back unchanged, so nothing is
/// kept waiting in memory.
/// </para>
/// <para>
/// Sending the form posts the vote at once. Making and posting it is the same code the Modbot page
/// uses (<see cref="VoteService"/>).
/// </para>
/// </remarks>
public sealed class VoteCommand(
    ModbotContext db,
    IFactWriter facts,
    IModbotClock clock,
    VoteService? votes = null)
{
    /// <summary>The form's id is this, then the channel, a colon and the role (<c>0</c> for none).</summary>
    public const string FormPrefix = DiscordActionButton.Prefix + "form:vote:";

    public const string QuestionField = "question";
    public const string OptionsField = "options";

    public const string PickChannelMessage = "Pick a channel.";

    /// <summary>Whether a form is this command's.</summary>
    public static bool IsForm(string formId) => formId.StartsWith(FormPrefix, StringComparison.Ordinal);

    /// <summary>The form: the question on one line and the options, one to a line. Public for tests.</summary>
    public static DiscordForm Form(string channelId, string? roleId)
        => new(
            "Ask members to vote",
            FormPrefix + channelId + ":" + (roleId ?? "0"),
            [
                new DiscordFormField(QuestionField, "Question", DiscordFormFieldKind.ShortText, Required: true, MaxLength: VoteLimits.MostQuestionLength),
                new DiscordFormField(OptionsField, "Options, one on each line", DiscordFormFieldKind.LongText, Required: true, MaxLength: 4000),
            ]);

    /// <summary>Opens the form and returns null; every refusal returns the answer.</summary>
    public async Task<StaffCommandAnswer?> RunAsync(DiscordCommandCall call, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        if (votes is null)
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), "refused");

        var channel = call.Option(DiscordCommands.VoteChannelOption)?.Trim() ?? string.Empty;
        if (channel.Length == 0)
            return new StaffCommandAnswer(DiscordReply.Say(PickChannelMessage), "invalid");

        var role = call.Option(DiscordCommands.VoteRoleOption)?.Trim();

        try
        {
            await call.ShowFormAsync(Form(channel, string.IsNullOrEmpty(role) ? null : role), ct).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Discord's three seconds are gone: the form can no longer be shown.
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.TooLateMessage), "refused");
        }
    }

    /// <summary>The form was sent: check who is sending again, then make the vote and post it.</summary>
    public async Task<DiscordReply> HandleFormAsync(DiscordFormSubmit submit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submit);

        var rest = submit.FormId[FormPrefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0 || colon == rest.Length - 1)
            return DiscordReply.Say("Modbot does not know that form.");

        var channel = rest[..colon];
        var role = rest[(colon + 1)..] is { } r && r != "0" ? r : null;

        var about = new JsonObject { ["channel"] = channel };

        // The permission is checked again here: a role taken away between the command and the form
        // is a refusal, never an action.
        if (!await CommandSwitchSetting.IsOnAsync(db, DiscordCommands.Vote, ct).ConfigureAwait(false))
        {
            await RecordAsync(submit.DiscordUserId, null, "off", about, ct).ConfigureAwait(false);
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Vote, menu: false));
        }

        var (user, refusal, outcome) = await CommandAccess
            .StaffAsync(db, clock, submit.DiscordUserId, ModbotPermissions.ManagePosts, writes: true, DiscordCommands.Vote, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await RecordAsync(submit.DiscordUserId, user, outcome, about, ct).ConfigureAwait(false);
            return refusal;
        }

        if (votes is null)
            return DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage);

        var draft = new VoteDraft(submit.Text(QuestionField), channel, VoteService.Lines(submit.Text(OptionsField)), role);
        var made = await votes.CreateAsync(draft, user!.Id, ct).ConfigureAwait(false);

        if (made.Vote is not { } vote)
        {
            await RecordAsync(submit.DiscordUserId, user, "refused", about, ct).ConfigureAwait(false);
            return DiscordReply.Say(CardText.EscapeText(made.Problem ?? "Modbot could not post the vote."));
        }

        about["voteId"] = vote.Id.ToString();
        about["options"] = vote.Options.Count;
        await RecordAsync(submit.DiscordUserId, user, "done", about, ct).ConfigureAwait(false);

        return DiscordReply.Say($"Vote posted in <#{channel}> with {vote.Options.Count.ToString(CultureInfo.InvariantCulture)} options.");
    }

    private Task RecordAsync(string discordUserId, ModbotUser? user, string outcome, JsonObject about, CancellationToken ct)
        => CommandAccess.RecordAsync(facts, clock, discordUserId, user, DiscordCommands.Vote, outcome, about, ct);
}
