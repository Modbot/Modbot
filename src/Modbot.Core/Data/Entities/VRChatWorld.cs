using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// One VRChat world Modbot has seen anybody in, and what its page said the last time it was read.
/// The table is <c>vrchat_world</c>.
/// </summary>
/// <remarks>
/// <para>
/// This table exists so that a timeline can say <em>The Black Cat</em> where it used to say
/// <c>wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b</c>. Presence facts have carried the world id
/// since M3; nothing has ever carried the name, so every screen showing a place has been showing
/// an id that means nothing to the person reading it.
/// </para>
/// <para>
/// <strong>The name comes from the API, never from the log.</strong> VRChat's log does print a
/// readable world name on the <c>Joining or Creating Room:</c> line, and taking it from there
/// would be free -- but it is the name as that one moderator's client had it, at that moment,
/// and Modbot would have no way to notice a rename or to learn a world it has only seen through
/// the group's instance list. The world page is the one answer everybody shares.
/// </para>
/// <para>
/// <strong>Rows are cheap and permanent.</strong> A group cycles through a handful of worlds, so
/// this table stays small -- tens of rows, not thousands. A world is read once when it is first
/// seen and then left alone: names change so rarely that polling for one would spend a real
/// budget on an answer that is almost always the same. <see cref="LastRefreshedAt"/> records when
/// it was read so that a moderator can ask for a fresh read by hand.
/// </para>
/// <para>
/// Everything time-stamped here comes from <c>IModbotClock</c>, and the world id is opaque text
/// stored exactly as VRChat sent it (foundation section 3.1.1).
/// </para>
/// </remarks>
public class VRChatWorld
{
    /// <summary>VRChat's id for the world. Opaque: never parsed, never validated.</summary>
    public string WorldId { get; set; } = string.Empty;

    // ── The page as last read. All null until the first successful read. ───────────────────

    /// <summary>What the world is called. The whole reason this table exists.</summary>
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The world author's VRChat user id. Opaque text, like every other id.</summary>
    public string? AuthorId { get; set; }

    /// <summary>The author's display name as the world page reported it.</summary>
    public string? AuthorName { get; set; }

    public string? ImageUrl { get; set; }

    public string? ThumbnailImageUrl { get; set; }

    /// <summary>
    /// How many people the world holds. <strong>Never treated as a limit Modbot enforces</strong>
    /// -- it is what the page said, and exemptions raise real capacity above it (foundation
    /// section 3.1).
    /// </summary>
    public int? Capacity { get; set; }

    /// <summary>The softer figure VRChat suggests, which is not the hard capacity.</summary>
    public int? RecommendedCapacity { get; set; }

    /// <summary>VRChat's tag list for the world, as a JSON array.</summary>
    [Column(TypeName = "jsonb")]
    public string? Tags { get; set; }

    /// <summary>
    /// Whether the world is public, private or in Labs, in VRChat's own words. Kept as text
    /// rather than an enum: a value this build has not seen is still a real world.
    /// </summary>
    public string? ReleaseStatus { get; set; }

    /// <summary>When VRChat says the world was first published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When VRChat says the world was last changed.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    // ── When Modbot saw it, and when it last asked VRChat about it ────────────────────────

    /// <summary>The first time this world id appeared anywhere Modbot looks.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The most recent time anybody was recorded in this world.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// When the world page was last read. Null means the row is a placeholder created from an id
    /// alone and the name is still unknown -- which is what the world sweep looks for.
    /// </summary>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>
    /// Why the last read failed, in VRChat's words, or null if it worked. A deleted or private
    /// world is the ordinary case here, not a fault: the row keeps the id and the UI keeps
    /// showing the id.
    /// </summary>
    public string? RefreshError { get; set; }
}
