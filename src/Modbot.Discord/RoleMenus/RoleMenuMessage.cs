using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.RoleMenus;

/// <summary>
/// What a role menu looks like in Discord, and the ids its buttons and drop-down list carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ids.</strong> A button is <c>modbot:rolemenu:&lt;menu&gt;:&lt;role&gt;</c> and the drop-down
/// list is <c>modbot:rolemenu:&lt;menu&gt;</c> with the roles as its values. The prefix is distinct from
/// every other feature's, so a press is told apart before anything is read. A press only ever names
/// a menu and a role by id: what the role is allowed to be is looked up again when it is pressed,
/// never taken from the message.
/// </para>
/// <para>
/// Discord's limits (five buttons to a row and five rows; 25 choices in a list) are the same 25
/// roles <see cref="DiscordRoleMenuLimits.MostRoles"/> holds, so a saved menu always fits.
/// </para>
/// </remarks>
public static class RoleMenuMessage
{
    public const string Prefix = DiscordActionButton.Prefix + "rolemenu:";

    public static bool IsPress(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The id of one role's button.</summary>
    public static string ButtonId(Guid menuId, string roleId) => ListId(menuId) + ":" + roleId;

    /// <summary>The id of the drop-down list.</summary>
    public static string ListId(Guid menuId) => Prefix + menuId.ToString("N");

    /// <summary>
    /// The menu a press is for, and the role when it was a button. False for an id that is not a role
    /// menu's or is mangled.
    /// </summary>
    public static bool TryRead(string? id, out Guid menuId, out string? roleId)
    {
        menuId = Guid.Empty;
        roleId = null;

        if (!IsPress(id))
            return false;

        var rest = id![Prefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        var menuText = colon < 0 ? rest : rest[..colon];

        if (!Guid.TryParseExact(menuText, "N", out menuId))
            return false;

        if (colon >= 0)
            roleId = rest[(colon + 1)..];

        return colon < 0 || roleId is { Length: > 0 };
    }

    /// <summary>The buttons for a menu laid out as buttons; none for a drop-down list.</summary>
    public static IReadOnlyList<DiscordActionButton> Buttons(DiscordRoleMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (menu.Style != DiscordRoleMenuStyles.Buttons)
            return [];

        return [.. Ordered(menu).Select(r => new DiscordActionButton(r.Label, ButtonId(menu.Id, r.RoleId), DiscordButtonStyle.Plain, r.Emoji))];
    }

    /// <summary>The drop-down list for a menu laid out as one; null for buttons.</summary>
    public static DiscordSelectMenu? Select(DiscordRoleMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (menu.Style != DiscordRoleMenuStyles.Select)
            return null;

        var choices = Ordered(menu).Select(r => new DiscordSelectChoice(r.Label, r.RoleId, r.Emoji)).ToList();

        return new DiscordSelectMenu(
            ListId(menu.Id),
            menu.SingleChoice ? "Pick a role" : "Pick roles",
            choices,
            menu.SingleChoice ? 1 : choices.Count);
    }

    private static IEnumerable<DiscordRoleMenuRole> Ordered(DiscordRoleMenu menu)
        => menu.Roles.OrderBy(r => r.Position).Take(DiscordRoleMenuLimits.MostRoles);
}
