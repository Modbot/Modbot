using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// A saved list: a name and a rule tree, asked again every time somebody looks. The table is
/// <c>saved_list</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A definition, not a snapshot</strong> (M7 §2.2). Nothing here records who was in the
/// list; opening it next month gives next month's answer, which is what makes "our regulars" a
/// thing a group can keep rather than a list that goes stale. So there is nothing to purge here
/// either: a purged person is gone from the tables the rules read, and from every list with them.
/// </para>
/// <para>
/// The rules are the giveaway rule tree, the same JSON and the same checker (giveaways design
/// §2.6, lists design §2). A list is never a second answer to "how many hours has this person
/// spent here".
/// </para>
/// <para>
/// A list never does anything by itself (M7 §7). Exporting it is a person's action, recorded as a
/// fact; a giveaway or auto-invites can name it as one of their rules, and then the list is asked
/// at the moment they would have asked their own rules.
/// </para>
/// </remarks>
public class SavedList
{
    public const int MaxNameLength = 100;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The rule tree, as JSON. See <c>GiveawayRules</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string Rules { get; set; } = "{\"kind\":\"allOf\",\"rules\":[]}";

    /// <summary>The account that made it. Null once that account is gone, or for one Modbot made.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When it was deleted. Kept rather than removed so a giveaway that once named it can still
    /// say which list it was; nothing lists or asks a deleted list.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
