using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/find</c> (find design): a member looks for other members by what their profiles say, such as
/// the singers in UTC-5.
/// </summary>
/// <remarks>
/// <para>
/// For every member, with no Modbot account, and private. One field and a value, and a second pair
/// if the member wants one; a member is listed when every pair matches. Only members who turned on
/// Findable are listed, and a member matches a field only through an answer the searcher may read
/// (<see cref="MemberProfiles.FindAsync"/>), so the list never gives away a hidden answer. The
/// reply names up to <see cref="Most"/> members, each with a button that shows their card exactly as
/// <c>/profile</c> would.
/// </para>
/// </remarks>
public sealed class FindCommand
{
    /// <summary>The key of the per-person limit, apart from the other commands'.</summary>
    public const string LimitsKey = "find";

    public const string TooFastMessage = "Slow down. Try again in a minute.";
    public const string NoFieldMessage = "There is no profile field with that name.";
    public const string HalfAPairMessage = "Fill in both a field and its value.";
    public const string NobodyMessage = "Nobody found.";

    /// <summary>How many members one reply lists: the most buttons a message holds.</summary>
    public const int Most = 25;

    /// <summary>The View profile button's id: <c>modbot:find:view:&lt;Discord user id&gt;</c>.</summary>
    public const string ViewButton = DiscordActionButton.Prefix + "find:view:";

    private const string ButtonPrefix = DiscordActionButton.Prefix + "find:";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;
    private readonly ProfileCommand _profile;

    public FindCommand(
        ModbotContext db,
        IModbotClock clock,
        [FromKeyedServices(LimitsKey)] MemberCommandLimits limits,
        ProfileCommand profile)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(profile);

        _db = db;
        _clock = clock;
        _limits = limits;
        _profile = profile;
    }

    /// <summary>Counts one use for this person and says whether it is allowed. See <see cref="MemberCommandLimits"/>.</summary>
    public bool TryUse(string discordUserId) => _limits.TryUse(discordUserId, _clock.UtcNow);

    /// <summary>Whether a press is this command's.</summary>
    public static bool IsButton(string buttonId) => buttonId.StartsWith(ButtonPrefix, StringComparison.Ordinal);

    // ── The suggestions ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What to suggest under <c>field</c> and <c>value</c> (and the second pair): the fields' names,
    /// and for a pick-list field the labels of its options. A text field suggests nothing, so
    /// whatever is typed is used as it is. Nothing here is about a member.
    /// </summary>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(DiscordSuggestionAsk ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var fields = await MemberProfiles.FieldsAsync(_db, ct).ConfigureAwait(false);
        var word = ask.Typed.Trim();

        if (ask.OptionName is DiscordCommands.FindFieldOption or DiscordCommands.FindAlsoFieldOption)
        {
            return
            [
                .. fields
                    .Where(f => f.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new DiscordSuggestion(f.Name, f.Name)),
            ];
        }

        var fieldOption = ask.OptionName == DiscordCommands.FindValueOption
            ? DiscordCommands.FindFieldOption
            : DiscordCommands.FindAlsoFieldOption;

        if (ask.OptionName is not (DiscordCommands.FindValueOption or DiscordCommands.FindAlsoValueOption)
            || ResolveField(fields, ask.Option(fieldOption)) is not { Kind: ProfileFieldKind.PickList } field)
        {
            return [];
        }

        return
        [
            .. field.Options
                .Where(o => o.Label.Contains(word, StringComparison.OrdinalIgnoreCase))
                .Select(o => new DiscordSuggestion(o.Label, o.Label)),
        ];
    }

    // ── /find ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs <c>/find</c>. The caller has already checked the switch and counted the use.</summary>
    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var fields = await MemberProfiles.FieldsAsync(_db, ct).ConfigureAwait(false);

        if (fields.Count == 0)
            return DiscordReply.Say(ProfileCommand.NoFieldsMessage);

        var searches = new List<ProfileSearch>();

        var first = PairOf(call, fields, DiscordCommands.FindFieldOption, DiscordCommands.FindValueOption);
        var second = PairOf(call, fields, DiscordCommands.FindAlsoFieldOption, DiscordCommands.FindAlsoValueOption);

        if (first.State != PairState.Whole)
            return DiscordReply.Say(first.State == PairState.Unknown ? NoFieldMessage : HalfAPairMessage);

        searches.Add(first.Search!);

        if (second.State == PairState.Unknown)
            return DiscordReply.Say(NoFieldMessage);

        if (second.State == PairState.Half)
            return DiscordReply.Say(HalfAPairMessage);

        if (second.Search is { } also)
            searches.Add(also);

        var staff = await _profile.IsStaffAsync(call.DiscordUserId, ct).ConfigureAwait(false);
        var found = await MemberProfiles.FindAsync(_db, call.DiscordUserId, staff, searches, Most, ct).ConfigureAwait(false);

        if (found.Total == 0)
            return DiscordReply.Say(NobodyMessage);

        var lines = found.Members
            .Select((m, i) => $"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {CardText.EscapeName(CardText.Plain(m.Name, CardText.MaxNameLength))}")
            .ToList();

        if (found.Total > found.Members.Count)
            lines.Add($"and {(found.Total - found.Members.Count).ToString(CultureInfo.InvariantCulture)} more");

        return new DiscordReply(
            null,
            [new DiscordEmbedContent($"Found {found.Total.ToString(CultureInfo.InvariantCulture)}", string.Join('\n', lines), CardColour.Violet, [], null, null, null)],
            Actions:
            [
                .. found.Members.Select((m, i) => new DiscordActionButton(
                    found.Members.Count == 1 ? "View profile" : $"View profile {(i + 1).ToString(CultureInfo.InvariantCulture)}",
                    ViewButton + m.DiscordUserId)),
            ]);
    }

    private enum PairState
    {
        /// <summary>Neither the field nor the value was filled in.</summary>
        Empty,

        /// <summary>The field or the value was filled in without the other.</summary>
        Half,

        /// <summary>The field is not one of the profile fields.</summary>
        Unknown,

        /// <summary>A field and a value.</summary>
        Whole,
    }

    private readonly record struct Pair(PairState State, ProfileSearch? Search);

    private static Pair PairOf(DiscordCommandCall call, IReadOnlyList<ProfileField> fields, string fieldOption, string valueOption)
    {
        var name = call.Option(fieldOption)?.Trim() ?? string.Empty;
        var value = call.Option(valueOption)?.Trim() ?? string.Empty;

        if (name.Length == 0 && value.Length == 0)
            return new Pair(PairState.Empty, null);

        if (name.Length == 0 || value.Length == 0)
            return new Pair(PairState.Half, null);

        return ResolveField(fields, name) is { } field
            ? new Pair(PairState.Whole, new ProfileSearch(field.Id, value))
            : new Pair(PairState.Unknown, null);
    }

    /// <summary>The field called this, letters compared without case; failing that, the one field whose name holds the words.</summary>
    private static ProfileField? ResolveField(IReadOnlyList<ProfileField> fields, string? typed)
    {
        var name = typed?.Trim();

        if (string.IsNullOrEmpty(name))
            return null;

        var exact = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
            return exact;

        var holding = fields.Where(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return holding.Count == 1 ? holding[0] : null;
    }

    // ── View profile ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press on View profile: the member's card, exactly as <c>/profile</c> shows it to the one who
    /// pressed. Follows the switch of <c>/find</c>.
    /// </summary>
    public async Task<DiscordReply> HandleButtonAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.Find, ct).ConfigureAwait(false))
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Find, menu: false));

        if (!press.ButtonId.StartsWith(ViewButton, StringComparison.Ordinal))
            return DiscordReply.Say("Modbot does not know that button.");

        var id = press.ButtonId[ViewButton.Length..];

        if (id.Length is 0 or > 25 || !id.All(char.IsAsciiDigit))
            return DiscordReply.Say("Modbot does not know that button.");

        return await _profile.OtherCardAsync(press.DiscordUserId, id, ct).ConfigureAwait(false);
    }
}
