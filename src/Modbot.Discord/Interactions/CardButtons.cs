using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Interactions;

/// <summary>The buttons under one log card: links first, then the ones the bot answers.</summary>
public sealed record CardButtonSet(IReadOnlyList<DiscordLinkButton> Links, IReadOnlyList<DiscordActionButton> Actions)
{
    public static CardButtonSet None { get; } = new([], []);

    public bool IsEmpty => Links.Count == 0 && Actions.Count == 0;
}

/// <summary>
/// Which buttons a log card carries (acting from Discord design §7). Pure: a fact's type and
/// subject in, buttons out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only a message that is one card gets any</strong>; the poster decides that, because only
/// it knows how the cards were batched. This decides which, by what the card is about.
/// </para>
/// <para>
/// A card in a channel is read by everybody in it, so its buttons are the same for everybody. The
/// press is where Modbot's permission is checked.
/// </para>
/// </remarks>
public static class CardButtons
{
    public const string AddNoteLabel = "Add note";
    public const string OpenCaseLabel = "Open case";

    /// <summary>Cards about a ban that stands: nothing to ban or kick again.</summary>
    private static readonly HashSet<string> Bans = new(StringComparer.Ordinal)
    {
        FactType.MemberBanned,
        FactType.ActionBan,
        FactType.AutoModGroupBan,
    };

    /// <summary>Cards about something a VRChat person did or had done to them.</summary>
    private static readonly HashSet<string> VRChatPeople = new(StringComparer.Ordinal)
    {
        FactType.MemberJoined,
        FactType.MemberLeft,
        FactType.MemberKicked,
        FactType.MemberUnbanned,
        FactType.RoleGranted,
        FactType.RoleRevoked,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.UserProfileFirstSeen,
        FactType.UserProfileChanged,
        FactType.UserAgeVerified,
        FactType.UserAgeFlagSet,
        FactType.UserAgeFlagCleared,
        FactType.InstanceJoined,
        FactType.InstanceLeft,
        FactType.AvatarChanged,
        FactType.ActionKick,
        FactType.ActionUnban,
        FactType.ActionJoinRequestApproved,
        FactType.ActionJoinRequestRejected,
        FactType.AutoModFlag,
        FactType.AutoModGroupRemove,
        FactType.NoteAdded,
    };

    /// <summary>Cards about something a Discord account did or had done to it, beyond the member events.</summary>
    private static readonly HashSet<string> DiscordPeople = new(StringComparer.Ordinal)
    {
        FactType.NoteAdded,
        FactType.AutoModFlag,
        FactType.AutoModTimeout,
        FactType.AutoModMessageDeleted,
    };

    /// <summary>Whether a card is about a ban that stands, so the poster looks for its case file.</summary>
    public static bool IsBan(string type) => Bans.Contains(type);

    /// <param name="caseUrl">Where the case file covering this ban opens in Modbot, when there is one.</param>
    public static CardButtonSet For(string type, FactPlatform subjectPlatform, string subjectId, string? caseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (string.IsNullOrWhiteSpace(subjectId))
            return CardButtonSet.None;

        if (subjectPlatform == FactPlatform.Discord)
        {
            var aboutPerson = type.StartsWith("discord.member.", StringComparison.Ordinal) || DiscordPeople.Contains(type);
            return aboutPerson ? Only(Note(onDiscord: true, subjectId)) : CardButtonSet.None;
        }

        if (subjectPlatform != FactPlatform.VRChat)
            return CardButtonSet.None;

        if (type == FactType.JoinRequestCreated)
        {
            return Only(
                Act(StaffActionWords.Approve, subjectId),
                Act(StaffActionWords.Reject, subjectId),
                Note(onDiscord: false, subjectId));
        }

        if (Bans.Contains(type))
        {
            var links = caseUrl is { Length: > 0 } ? new[] { new DiscordLinkButton(OpenCaseLabel, caseUrl) } : [];
            return new CardButtonSet(links, Fitting([Note(onDiscord: false, subjectId)]));
        }

        if (VRChatPeople.Contains(type))
        {
            return Only(
                Note(onDiscord: false, subjectId),
                Act(StaffActionWords.Kick, subjectId),
                Act(StaffActionWords.Ban, subjectId));
        }

        return CardButtonSet.None;
    }

    /// <summary>
    /// The buttons under a private lookup reply, for the moderator who asked: only what they could
    /// press on the web app, and no Ban or Kick for somebody already banned (the web app offers
    /// Unban there instead, which this version does not).
    /// </summary>
    public static IReadOnlyList<DiscordActionButton> ForLookup(
        bool onDiscord, string id, bool banned, Func<ModbotPermissions, bool> allows)
    {
        ArgumentNullException.ThrowIfNull(allows);

        var buttons = new List<DiscordActionButton>();

        if (allows(ModbotPermissions.WriteNotes))
            buttons.Add(Note(onDiscord, id));

        if (!onDiscord && !banned)
        {
            if (allows(ModbotPermissions.Kick))
                buttons.Add(Act(StaffActionWords.Kick, id));

            if (allows(ModbotPermissions.Ban))
                buttons.Add(Act(StaffActionWords.Ban, id));
        }

        return Fitting(buttons);
    }

    private static CardButtonSet Only(params DiscordActionButton[] actions) => new([], Fitting(actions));

    private static DiscordActionButton Note(bool onDiscord, string id)
        => new(AddNoteLabel, StaffMenus.NoteButtonFor(onDiscord, id));

    /// <summary>Grey like the rest: the red one is the confirmation's, the press that actually acts.</summary>
    private static DiscordActionButton Act(string action, string id)
        => new(StaffActionWords.Label(action), StaffMenus.ActButtonFor(action, id));

    /// <summary>Leaves off a button whose id would be too long for Discord rather than cutting the id.</summary>
    private static IReadOnlyList<DiscordActionButton> Fitting(IEnumerable<DiscordActionButton> buttons)
        => [.. buttons.Where(b => StaffMenus.Fits(b.Id))];
}
