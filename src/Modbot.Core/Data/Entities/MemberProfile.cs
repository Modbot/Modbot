namespace Modbot.Core.Data.Entities;

/// <summary>What kind of answer a profile field takes.</summary>
public enum ProfileFieldKind
{
    /// <summary>One of the options the admin set.</summary>
    PickList = 0,

    /// <summary>The member's own words.</summary>
    Text = 1,
}

/// <summary>Who may read a member's answer to a profile field (member profiles design §3).</summary>
public enum ProfileVisibility
{
    /// <summary>The member alone. Staff still see it.</summary>
    OnlyMe = 0,

    /// <summary>Modbot staff.</summary>
    Staff = 1,

    /// <summary>Everyone in the Discord server.</summary>
    Members = 2,
}

/// <summary>
/// One question the admin put on every member's profile. The table is <c>member_profile_field</c>
/// (member profiles design §2).
/// </summary>
public class ProfileField
{
    public const int MaxFields = 25;
    public const int MaxNameLength = 40;
    public const int MaxTextLength = 300;
    public const int MaxOptions = 25;
    public const int MaxOptionLength = 60;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    /// <summary>Set when the field is added and not changed: an answer of one kind means nothing in the other.</summary>
    public ProfileFieldKind Kind { get; set; }

    /// <summary>Who sees the answer of a member who has not chosen for themselves.</summary>
    public ProfileVisibility DefaultVisibility { get; set; } = ProfileVisibility.Staff;

    /// <summary>The order members see the fields in.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProfileOption> Options { get; set; } = [];
}

/// <summary>
/// One answer a pick-list field offers. The table is <c>member_profile_option</c>. The id stays the
/// same when the option is renamed or moved, so a later pairing of Discord roles to options keeps
/// working (member profiles design §8).
/// </summary>
public class ProfileOption
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid FieldId { get; set; }

    public string Label { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>
    /// The Discord role whose holders get this answer from "Copy from roles", or null for none. Text,
    /// never parsed. Kept when the role is deleted in Discord; the copy then finds nobody.
    /// </summary>
    public string? DiscordRoleId { get; set; }
}

/// <summary>
/// One member's answer to one field, and who they let read it. The table is
/// <c>member_profile_value</c>; the row is gone once it holds neither (member profiles design §6).
/// </summary>
public class ProfileValue
{
    public Guid FieldId { get; set; }

    /// <summary>The member's Discord user id. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>The option picked, for a pick-list field.</summary>
    public Guid? OptionId { get; set; }

    /// <summary>The member's words, for a text field.</summary>
    public string? Text { get; set; }

    /// <summary>The member's own choice of who sees it, or null to follow the field's default.</summary>
    public ProfileVisibility? Visibility { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A member's own switches on their profile. The table is <c>member_profile</c>; a member with no row
/// has every switch off, which is how everyone starts (member profiles design §8). A row is kept only
/// while at least one switch is on.
/// </summary>
public class MemberProfile
{
    /// <summary>The member's Discord user id. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>Whether <c>/find</c> may list them.</summary>
    public bool Findable { get; set; }

    /// <summary>Whether the weekly spotlight may feature them (spotlight design §2).</summary>
    public bool Spotlight { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
