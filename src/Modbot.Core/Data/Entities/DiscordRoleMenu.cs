namespace Modbot.Core.Data.Entities;

/// <summary>How a role menu is laid out under its message.</summary>
public static class DiscordRoleMenuStyles
{
    /// <summary>One button for each role, five to a row.</summary>
    public const string Buttons = "buttons";

    /// <summary>One drop-down list holding every role.</summary>
    public const string Select = "select";

    public static bool IsKnown(string? value) => value is Buttons or Select;
}

/// <summary>What a role menu is limited to, in one place for the page, the save and the post.</summary>
public static class DiscordRoleMenuLimits
{
    /// <summary>The most menus the server keeps.</summary>
    public const int MostMenus = 25;

    /// <summary>
    /// The most roles on one menu: Discord's five rows of five buttons, and its drop-down list of 25
    /// choices, come to the same number.
    /// </summary>
    public const int MostRoles = 25;

    public const int MostNameLength = 80;

    /// <summary>A message holds 2,000 characters.</summary>
    public const int MostTextLength = 2000;

    /// <summary>A button's label holds 80 characters; a drop-down choice's holds 100.</summary>
    public const int MostLabelLength = 80;

    public const int MostEmojiLength = 64;
}

/// <summary>
/// A message Modbot posts with buttons or a drop-down list that give people roles. The table is
/// <c>discord_role_menu</c>.
/// </summary>
public class DiscordRoleMenu
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What the menu is called in Modbot. Never shown in Discord.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The channel the message is posted in. Text, never parsed.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The words above the buttons. Posted with mentions off.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>One of <see cref="DiscordRoleMenuStyles"/>.</summary>
    public string Style { get; set; } = DiscordRoleMenuStyles.Buttons;

    /// <summary>
    /// Pick one: choosing a role takes away the others on this menu. Off, every role is its own
    /// choice and pressing a role toggles it.
    /// </summary>
    public bool SingleChoice { get; set; }

    /// <summary>The posted message, or null while the menu has not been posted. Text, never parsed.</summary>
    public string? MessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the message was last posted or rewritten, or null if never.</summary>
    public DateTimeOffset? PostedAt { get; set; }

    public List<DiscordRoleMenuRole> Roles { get; set; } = [];
}

/// <summary>
/// One role on a role menu. The table is <c>discord_role_menu_role</c>.
/// </summary>
public class DiscordRoleMenuRole
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid MenuId { get; set; }

    /// <summary>The Discord role handed out. Text, never parsed.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>What the button or choice says.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>A Unicode emoji or a server emoji (<c>&lt;:name:id&gt;</c>) beside the label, or null.</summary>
    public string? Emoji { get; set; }

    /// <summary>The role's place on the menu, from 0.</summary>
    public int Position { get; set; }
}
