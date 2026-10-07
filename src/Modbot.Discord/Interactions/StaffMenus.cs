using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Interactions;

/// <summary>
/// The right-click menus, and how every button and form the staff interactions use is named
/// (acting from Discord design §2, §7).
/// </summary>
/// <remarks>
/// <para>
/// Every id starts with <see cref="DiscordActionButton.Prefix"/>, so the gateway's own-server guard
/// lets it through and a press on somebody else's button is left alone. The rest says what the
/// press is for, and carries either the person's id or the token of a waiting confirmation; nothing
/// else travels in an id, because Discord shows it to whoever cares to look.
/// </para>
/// <para>
/// Discord allows 100 characters. An id that would be longer is not made: the button is left off
/// rather than given a cut id that names somebody else (<see cref="Fits"/>).
/// </para>
/// </remarks>
public static class StaffMenus
{
    /// <summary>Right-click a member: their profile, privately.</summary>
    public const string LookUp = "Look up in Modbot";

    /// <summary>Right-click a member: a note about them.</summary>
    public const string AddNote = "Add a note";

    /// <summary>The two menus, both hidden from members without Timeout Members until an admin says otherwise.</summary>
    public static IReadOnlyList<DiscordCommandDefinition> All { get; } =
    [
        new(LookUp, string.Empty, [], DiscordCommandKind.User, ShownTo: DiscordShownTo.Moderators),
        new(AddNote, string.Empty, [], DiscordCommandKind.User, ShownTo: DiscordShownTo.Moderators),
    ];

    /// <summary>The permission each menu needs, as the web app's own page or button asks for it.</summary>
    public static ModbotPermissions? Requires(string menu) => menu switch
    {
        LookUp => ModbotPermissions.ViewProfile,
        AddNote => ModbotPermissions.WriteNotes,
        _ => null,
    };

    // ── Ids ──────────────────────────────────────────────────────────────────────────────────

    private const string P = DiscordActionButton.Prefix;

    /// <summary>A note button: <c>modbot:note:v:&lt;VRChat id&gt;</c> or <c>modbot:note:d:&lt;Discord id&gt;</c>.</summary>
    public const string NoteButton = P + "note:";

    /// <summary>An action button: <c>modbot:act:ban:&lt;VRChat id&gt;</c>. What a handled card loses.</summary>
    public const string ActButton = P + "act:";

    /// <summary>The confirmation's yes: <c>modbot:yes:&lt;token&gt;</c>.</summary>
    public const string YesButton = P + "yes:";

    /// <summary>The confirmation's cancel: <c>modbot:no:&lt;token&gt;</c>.</summary>
    public const string NoButton = P + "no:";

    /// <summary>The note form: <c>modbot:form:note:v:&lt;id&gt;</c>.</summary>
    public const string NoteForm = P + "form:note:";

    /// <summary>The reasons form: <c>modbot:form:act:&lt;token&gt;</c>.</summary>
    public const string ActForm = P + "form:act:";

    public const string VRChatMark = "v";
    public const string DiscordMark = "d";

    /// <summary>The longest id Discord takes for a button or a form.</summary>
    public const int MaxIdLength = 100;

    /// <summary>Whether a button press is one of these, rather than one of <c>/me</c>'s.</summary>
    public static bool IsStaffButton(string id)
        => id.StartsWith(NoteButton, StringComparison.Ordinal)
            || id.StartsWith(ActButton, StringComparison.Ordinal)
            || id.StartsWith(YesButton, StringComparison.Ordinal)
            || id.StartsWith(NoButton, StringComparison.Ordinal);

    public static bool Fits(string id) => id.Length <= MaxIdLength;

    /// <summary>The note button for a person: <paramref name="onDiscord"/> says which kind of id it is.</summary>
    public static string NoteButtonFor(bool onDiscord, string id) => NoteButton + Mark(onDiscord) + ":" + id;

    public static string NoteFormFor(bool onDiscord, string id) => NoteForm + Mark(onDiscord) + ":" + id;

    public static string ActButtonFor(string action, string vrchatUserId) => ActButton + action + ":" + vrchatUserId;

    /// <summary>
    /// Reads <c>&lt;mark&gt;:&lt;id&gt;</c> after a prefix. The id is everything after the first
    /// colon that follows the mark, whatever it holds: ids are opaque (foundation §3.1.1).
    /// </summary>
    public static bool TryReadPerson(string id, string prefix, out bool onDiscord, out string personId)
    {
        onDiscord = false;
        personId = string.Empty;

        if (!id.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = id[prefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == rest.Length - 1)
            return false;

        var mark = rest[..colon];
        if (mark is not (VRChatMark or DiscordMark))
            return false;

        onDiscord = mark == DiscordMark;
        personId = rest[(colon + 1)..];
        return true;
    }

    /// <summary>Reads <c>modbot:act:&lt;action&gt;:&lt;id&gt;</c>.</summary>
    public static bool TryReadAction(string id, out string action, out string vrchatUserId)
    {
        action = string.Empty;
        vrchatUserId = string.Empty;

        if (!id.StartsWith(ActButton, StringComparison.Ordinal))
            return false;

        var rest = id[ActButton.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == rest.Length - 1)
            return false;

        action = rest[..colon];
        vrchatUserId = rest[(colon + 1)..];
        return StaffActionWords.Known(action);
    }

    /// <summary>The token after a prefix, or null.</summary>
    public static string? TokenAfter(string id, string prefix)
        => id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length ? id[prefix.Length..] : null;

    private static string Mark(bool onDiscord) => onDiscord ? DiscordMark : VRChatMark;
}

/// <summary>The actions a card button can start, in the web app's own words, and how each one reads.</summary>
public static class StaffActionWords
{
    public const string Ban = "ban";
    public const string Kick = "kick";
    public const string Approve = "approve";
    public const string Reject = "reject";

    public static bool Known(string action) => action is Ban or Kick or Approve or Reject;

    /// <summary>The permission each action needs, as the web app's endpoints require it.</summary>
    public static ModbotPermissions Requires(string action) => action switch
    {
        Ban => ModbotPermissions.Ban,
        Kick => ModbotPermissions.Kick,
        _ => ModbotPermissions.AnswerJoinRequests,
    };

    /// <summary>
    /// The permission an action needs on the Discord server, apart from the group's: "Ban on
    /// Discord" and "Remove from Discord". None for the actions that have no Discord side.
    /// </summary>
    public static ModbotPermissions RequiresOnDiscord(string action) => action switch
    {
        Ban => ModbotPermissions.DiscordBan,
        Kick => ModbotPermissions.DiscordKick,
        _ => ModbotPermissions.None,
    };

    /// <summary>The word on the button and on the confirmation's yes.</summary>
    public static string Label(string action) => action switch
    {
        Ban => "Ban",
        Kick => "Kick",
        Approve => "Approve",
        _ => "Reject",
    };

    /// <summary>Red for the ones that keep somebody out.</summary>
    public static DiscordButtonStyle Style(string action)
        => action == Approve ? DiscordButtonStyle.Main : DiscordButtonStyle.Danger;

    /// <summary>The confirmation's question, with the name already escaped.</summary>
    public static string Question(string action, string name) => action switch
    {
        Ban => $"Ban **{name}** from the group?",
        Kick => $"Kick **{name}** from the group?",
        Approve => $"Let **{name}** into the group?",
        _ => $"Turn down **{name}**'s request to join?",
    };

    /// <summary>What shows while it is being sent.</summary>
    public static string Doing(string action, string name) => action switch
    {
        Ban => $"Banning **{name}**…",
        Kick => $"Kicking **{name}**…",
        Approve => $"Letting **{name}** in…",
        _ => $"Turning down **{name}**…",
    };

    /// <summary>What it says once VRChat said yes.</summary>
    public static string Done(string action, string name) => action switch
    {
        Ban => $"Banned **{name}**.",
        Kick => $"Kicked **{name}**.",
        Approve => $"Let **{name}** into the group.",
        _ => $"Turned down **{name}**'s request to join.",
    };

    /// <summary>
    /// The confirmation's question for an action that may act on the Discord server, with the name
    /// already escaped. The group alone is the card button's own wording, unless the ban takes the
    /// linked Discord account with it.
    /// </summary>
    public static string Question(PendingStaffAction pending, string name)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return (pending.Action, pending.Where) switch
        {
            (Ban, StaffActionWhere.Discord) => $"Ban **{name}** from the Discord server?",
            (Ban, StaffActionWhere.VRChat) when pending.AlsoBansDiscord => $"Ban **{name}** from the VRChat group and the Discord server?",
            (Kick, StaffActionWhere.Discord) => $"Remove **{name}** from the Discord server?",
            (Kick, StaffActionWhere.Both) => $"Kick **{name}** from the VRChat group and remove them from the Discord server?",
            _ => Question(pending.Action, name),
        };
    }

    /// <summary>The word on the confirmation's yes for an action that may act on the Discord server.</summary>
    public static string Label(PendingStaffAction pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return (pending.Action, pending.Where) switch
        {
            (Kick, StaffActionWhere.Discord) => "Remove",
            (Kick, StaffActionWhere.Both) => "Kick and remove",
            _ => Label(pending.Action),
        };
    }

    /// <summary>What shows while an action that may act on the Discord server is being sent.</summary>
    public static string Doing(PendingStaffAction pending, string name)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return (pending.Action, pending.Where) switch
        {
            (Kick, StaffActionWhere.Discord) => $"Removing **{name}**…",
            _ => Doing(pending.Action, name),
        };
    }

    /// <summary>What an action on the Discord server alone says once Discord said yes.</summary>
    public static string DoneOnDiscord(string action, string name, bool unchanged) => (action, unchanged) switch
    {
        (Ban, false) => $"Banned **{name}** from the Discord server.",
        (Ban, true) => $"**{name}** is already banned on Discord.",
        (_, false) => $"Removed **{name}** from the Discord server.",
        _ => $"**{name}** is not in the Discord server.",
    };

    /// <summary>The line a handled card gets, with the moderator's name already escaped.</summary>
    public static string Handled(string action, string by) => action switch
    {
        Ban => $"Banned by **{by}**",
        Kick => $"Kicked by **{by}**",
        Approve => $"Approved by **{by}**",
        _ => $"Rejected by **{by}**",
    };

    /// <summary>The form's title, before the name.</summary>
    public static string FormTitle(string action, string name) => action switch
    {
        Ban => $"Ban {name}",
        Kick => $"Kick {name}",
        Approve => $"Approve {name}",
        _ => $"Reject {name}",
    };
}
