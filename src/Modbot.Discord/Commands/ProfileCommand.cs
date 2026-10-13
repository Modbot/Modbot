using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/profile</c> (member profiles design §4): a member's own card with Edit and Who sees what, and
/// another member's card with only the fields the viewer may read.
/// </summary>
/// <remarks>
/// <para>
/// For every member, with no Modbot account, and private. Only a staff account -- a Modbot account
/// that proved its Discord account and may see members -- reads the fields a member kept to Staff or
/// to themselves. The buttons and forms only ever change the profile of the person who pressed them.
/// </para>
/// <para>
/// A form holds five boxes at most, so the fields go in pages of <see cref="PerForm"/>. With more than
/// one page the button first answers with a button for each page.
/// </para>
/// </remarks>
public sealed class ProfileCommand
{
    public const string NoFieldsMessage = "No profile fields have been set up.";
    public const string BotMessage = "Bots have no profile.";
    public const string TooLateMessage = "That took too long. Try again.";
    public const string FieldsChangedMessage = "Those fields have changed. Open it again.";

    /// <summary>How many boxes one Discord form holds.</summary>
    public const int PerForm = 5;

    public const string ButtonPrefix = DiscordActionButton.Prefix + "profile:";
    public const string EditButton = ButtonPrefix + "edit";
    public const string SeeButton = ButtonPrefix + "see";
    public const string FindableButton = ButtonPrefix + "findable";

    /// <summary>A profile form's id: <c>modbot:profile:form:edit:&lt;page&gt;</c> or <c>modbot:profile:form:see:&lt;page&gt;</c>.</summary>
    public const string FormPrefix = ButtonPrefix + "form:";

    private const string EditStep = "edit";
    private const string SeeStep = "see";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public ProfileCommand(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>Whether a press is this command's.</summary>
    public static bool IsButton(string buttonId) => buttonId.StartsWith(ButtonPrefix, StringComparison.Ordinal);

    /// <summary>Whether a form is this command's.</summary>
    public static bool IsForm(string formId) => formId.StartsWith(FormPrefix, StringComparison.Ordinal);

    // ── /profile ─────────────────────────────────────────────────────────────────────────────

    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var target = call.Option(DiscordCommands.MemberOption)?.Trim();

        return string.IsNullOrEmpty(target) || string.Equals(target, call.DiscordUserId, StringComparison.Ordinal)
            ? await OwnCardAsync(call.DiscordUserId, call.DiscordUsername, ct).ConfigureAwait(false)
            : await OtherCardAsync(call.DiscordUserId, target, ct).ConfigureAwait(false);
    }

    /// <summary>The caller's own card: every field, answered or not, with the level beside each answer and the two buttons.</summary>
    private async Task<DiscordReply> OwnCardAsync(string userId, string fallbackName, CancellationToken ct)
    {
        var lines = await MemberProfiles.LinesAsync(_db, userId, ProfileViewer.Owner, ct).ConfigureAwait(false);

        if (lines.Count == 0)
            return DiscordReply.Say(NoFieldsMessage);

        var member = await FindMemberAsync(userId, ct).ConfigureAwait(false);
        var card = Card(member?.DisplayName ?? fallbackName, member?.AvatarUrl, lines, showLevels: true);
        var findable = await MemberProfiles.IsFindableAsync(_db, userId, ct).ConfigureAwait(false);

        return new DiscordReply(
            null,
            [card],
            Actions:
            [
                new DiscordActionButton("Edit", EditButton, DiscordButtonStyle.Main),
                new DiscordActionButton("Who sees what", SeeButton),
                new DiscordActionButton(findable ? "Findable: On" : "Findable: Off", FindableButton),
            ]);
    }

    /// <summary>Another member's card, with the fields the viewer may read; nothing is said about the ones they may not.</summary>
    public async Task<DiscordReply> OtherCardAsync(string viewerId, string targetId, CancellationToken ct)
    {
        var member = await FindMemberAsync(targetId, ct).ConfigureAwait(false);

        if (member is null)
            return DiscordReply.Say(DiscordCommandHandler.NoDiscordRecordsMessage);

        if (member.IsBot)
            return DiscordReply.Say(BotMessage);

        var staff = await IsStaffAsync(viewerId, ct).ConfigureAwait(false);
        var lines = await MemberProfiles.LinesAsync(_db, targetId, staff ? ProfileViewer.Staff : ProfileViewer.Member, ct)
            .ConfigureAwait(false);

        return lines.Count == 0
            ? DiscordReply.Say($"{member.DisplayName} has nothing to show.")
            : DiscordReply.Card(Card(member.DisplayName, member.AvatarUrl, lines, showLevels: staff));
    }

    /// <summary>Staff here means a proven Modbot account that may see members (design §3).</summary>
    public async Task<bool> IsStaffAsync(string discordUserId, CancellationToken ct)
        => await StaffDiscord.AccountForAsync(_db, discordUserId, _clock.UtcNow, ct).ConfigureAwait(false)
            is { IsDisabled: false } account
            && DiscordCommands.Allows(account.EffectivePermissions, ModbotPermissions.ViewMembers);

    private Task<DiscordMember?> FindMemberAsync(string userId, CancellationToken ct)
        => _db.DiscordMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.LeftAt != null)
            .FirstOrDefaultAsync(ct);

    private static DiscordEmbedContent Card(string name, string? avatarUrl, IReadOnlyList<ProfileLine> lines, bool showLevels)
        => new(
            name,
            null,
            CardColour.Violet,
            [.. lines.Select(line => new DiscordEmbedField(
                line.Name,
                line.Answer is null
                    ? "Not set"
                    : showLevels ? $"{line.Answer}\n*{LevelWords(line.Visibility)}*" : line.Answer))],
            null,
            null,
            null,
            ThumbnailUrl: avatarUrl);

    public static string LevelWords(ProfileVisibility level) => level switch
    {
        ProfileVisibility.OnlyMe => "Only me",
        ProfileVisibility.Members => "Members",
        _ => "Staff",
    };

    // ── The buttons ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Edit and Who sees what. Null means a form was shown; anything else is the reply to send.
    /// </summary>
    public async Task<DiscordReply?> HandleButtonAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.Profile, ct).ConfigureAwait(false))
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Profile, menu: false));

        // The Findable switch flips and the card is rewritten in place, so the button shows the new
        // state; it needs no form.
        if (press.ButtonId == FindableButton)
        {
            var findable = await MemberProfiles.IsFindableAsync(_db, press.DiscordUserId, ct).ConfigureAwait(false);
            await MemberProfiles.SetFindableAsync(_db, press.DiscordUserId, !findable, _clock.UtcNow, ct).ConfigureAwait(false);

            await press.UpdateAsync(
                await OwnCardAsync(press.DiscordUserId, press.DiscordUsername, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return null;
        }

        if (!TryStep(press.ButtonId, ButtonPrefix, out var step, out var page))
            return DiscordReply.Say("Modbot does not know that button.");

        var fields = await MemberProfiles.FieldsAsync(_db, ct).ConfigureAwait(false);

        if (fields.Count == 0)
            return DiscordReply.Say(NoFieldsMessage);

        var pages = (fields.Count + PerForm - 1) / PerForm;

        // Several pages and none picked yet: one button for each page.
        if (page is null && pages > 1)
        {
            return new DiscordReply(
                "Which fields?",
                [],
                Actions: [.. Enumerable.Range(0, pages).Select(p => new DiscordActionButton(PageWords(p, fields.Count), $"{ButtonPrefix}{step}:{p}"))]);
        }

        var form = await FormAsync(press.DiscordUserId, step, page ?? 0, fields, ct).ConfigureAwait(false);

        if (form is null)
            return DiscordReply.Say(FieldsChangedMessage);

        try
        {
            await press.ShowFormAsync(form, ct).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException)
        {
            return DiscordReply.Say(TooLateMessage);
        }
    }

    /// <summary>"Fields 1 to 5", "Fields 6 to 7": the page's place in the list.</summary>
    private static string PageWords(int page, int fieldCount)
    {
        var first = page * PerForm + 1;
        var last = Math.Min(fieldCount, first + PerForm - 1);
        return first == last ? $"Field {first}" : $"Fields {first} to {last}";
    }

    /// <summary>The form for one page, or null when the page is gone because fields were removed meanwhile.</summary>
    private async Task<DiscordForm?> FormAsync(
        string userId, string step, int page, IReadOnlyList<ProfileField> fields, CancellationToken ct)
    {
        var onPage = fields.Skip(page * PerForm).Take(PerForm).ToList();

        if (onPage.Count == 0)
            return null;

        var values = await MemberProfiles.ValuesAsync(_db, userId, ct).ConfigureAwait(false);
        var id = $"{FormPrefix}{step}:{page}";

        if (step == SeeStep)
        {
            return new DiscordForm(
                "Who sees what",
                id,
                [.. onPage.Select(field =>
                {
                    values.TryGetValue(field.Id, out var value);
                    var now = MemberProfiles.EffectiveVisibility(field, value);

                    return new DiscordFormField(
                        FieldKey(field),
                        field.Name,
                        DiscordFormFieldKind.Choice,
                        Required: true,
                        Choices:
                        [
                            .. new[] { ProfileVisibility.OnlyMe, ProfileVisibility.Staff, ProfileVisibility.Members }
                                .Select(level => new DiscordChoice(LevelWords(level), LevelKey(level), IsDefault: level == now)),
                        ]);
                })]);
        }

        return new DiscordForm(
            "Edit your profile",
            id,
            [.. onPage.Select(field =>
            {
                values.TryGetValue(field.Id, out var value);

                if (field.Kind == ProfileFieldKind.Text)
                {
                    return new DiscordFormField(
                        FieldKey(field),
                        field.Name,
                        DiscordFormFieldKind.LongText,
                        Required: false,
                        MaxLength: ProfileField.MaxTextLength,
                        Value: value?.Text);
                }

                return new DiscordFormField(
                    FieldKey(field),
                    field.Name,
                    DiscordFormFieldKind.Choice,
                    Required: false,
                    Choices: [.. field.Options.Select(o => new DiscordChoice(o.Label, o.Id.ToString(), IsDefault: value?.OptionId == o.Id))]);
            })]);
    }

    private static string FieldKey(ProfileField field) => field.Id.ToString("N", CultureInfo.InvariantCulture);

    private static string LevelKey(ProfileVisibility level) => level switch
    {
        ProfileVisibility.OnlyMe => "only-me",
        ProfileVisibility.Members => "members",
        _ => "staff",
    };

    private static bool TryLevel(string? key, out ProfileVisibility level)
    {
        level = ProfileVisibility.Staff;

        switch (key)
        {
            case "only-me":
                level = ProfileVisibility.OnlyMe;
                return true;
            case "staff":
                return true;
            case "members":
                level = ProfileVisibility.Members;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads <c>edit</c> or <c>see</c>, and the page when there is one, after <paramref name="prefix"/>.</summary>
    private static bool TryStep(string id, string prefix, out string step, out int? page)
    {
        step = string.Empty;
        page = null;

        var parts = id[prefix.Length..].Split(':');

        if (parts[0] is not (EditStep or SeeStep) || parts.Length > 2)
            return false;

        step = parts[0];

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 100)
                return false;

            page = number;
        }

        return true;
    }

    // ── The forms ────────────────────────────────────────────────────────────────────────────

    /// <summary>An edit form or a Who sees what form was sent: saved, and answered with the refreshed card.</summary>
    public async Task<DiscordReply> HandleFormAsync(DiscordFormSubmit submit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submit);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.Profile, ct).ConfigureAwait(false))
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Profile, menu: false));

        if (!TryStep(submit.FormId, FormPrefix, out var step, out _))
            return DiscordReply.Say("Modbot does not know that form.");

        var fields = (await MemberProfiles.FieldsAsync(_db, ct).ConfigureAwait(false)).ToDictionary(f => FieldKey(f));
        var now = _clock.UtcNow;

        if (step == SeeStep)
        {
            var levels = new Dictionary<Guid, ProfileVisibility>();

            foreach (var key in submit.Values.Keys)
            {
                if (fields.TryGetValue(key, out var field) && TryLevel(submit.Picked(key).FirstOrDefault(), out var level))
                    levels[field.Id] = level;
            }

            await MemberProfiles.SetVisibilityAsync(_db, submit.DiscordUserId, levels, now, ct).ConfigureAwait(false);
        }
        else
        {
            var answers = new Dictionary<Guid, string?>();

            foreach (var key in submit.Values.Keys)
            {
                if (!fields.TryGetValue(key, out var field))
                    continue;

                answers[field.Id] = field.Kind == ProfileFieldKind.Text
                    ? submit.Text(key)
                    : submit.Picked(key).FirstOrDefault();
            }

            await MemberProfiles.SetAnswersAsync(_db, submit.DiscordUserId, answers, now, ct).ConfigureAwait(false);
        }

        return await OwnCardAsync(submit.DiscordUserId, submit.DiscordUsername, ct).ConfigureAwait(false);
    }
}
