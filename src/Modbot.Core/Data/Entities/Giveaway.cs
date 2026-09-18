using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>The words stored in <see cref="Giveaway.State"/> (giveaways design §3.1).</summary>
public static class GiveawayStates
{
    /// <summary>Saved, published nowhere, nobody can enter.</summary>
    public const string Draft = "draft";

    /// <summary>Open for entries.</summary>
    public const string Open = "open";

    /// <summary>Closed to entries and not yet drawn.</summary>
    public const string Closed = "closed";

    /// <summary>Drawn at least once.</summary>
    public const string Drawn = "drawn";

    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Draft, Open, Closed, Drawn, Cancelled];

    /// <summary>States a giveaway is still being run in: it shows in Discord and the scheduler looks at it.</summary>
    public static bool IsLive(string state) => state is Open or Closed;
}

/// <summary>How people get into a giveaway (giveaways design §4).</summary>
public static class GiveawayEntryWays
{
    /// <summary>Everybody who passes the rules is in. Nobody does anything.</summary>
    public const string Automatic = "automatic";

    /// <summary>A person enters by reacting to the giveaway's Discord post.</summary>
    public const string React = "react";

    public static readonly IReadOnlyList<string> All = [Automatic, React];
}

/// <summary>
/// One giveaway: what is being given away, who may enter, how, and by when. The table is
/// <c>giveaway</c>.
/// </summary>
/// <remarks>
/// <para>
/// The giveaway holds the <em>plan</em>. What actually happened is in <see cref="GiveawayDraw"/>
/// and <see cref="GiveawayEntrant"/>, which are written once and never changed: a draw is a fact,
/// and re-drawing makes a second draw beside the first rather than overwriting it (giveaways
/// design §5.3).
/// </para>
/// <para>
/// <see cref="SeedPromise"/> is published before any draw and <see cref="SeedEncrypted"/> holds the
/// seed it promises. The pair is replaced the moment a draw uses it, so a re-draw has a promise
/// standing before anybody asks for it — which is the only order in which a promise means
/// anything.
/// </para>
/// <para>
/// Every time here comes from <c>IModbotClock</c>, and every id is opaque text (foundation §3.1.1).
/// </para>
/// </remarks>
public class Giveaway
{
    public const int MaxNameLength = 100;
    public const int MaxPrizeLength = 500;

    /// <summary>The most winners one draw may pick. A prize list longer than this is several giveaways.</summary>
    public const int MaxWinners = 100;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>What is being given away, in the organiser's own words. Optional.</summary>
    public string Prize { get; set; } = string.Empty;

    // ── When ─────────────────────────────────────────────────────────────────────────────

    /// <summary>When entries open. Before this the giveaway is a draft in everything but name.</summary>
    public DateTimeOffset OpensAt { get; set; }

    /// <summary>When entries close.</summary>
    public DateTimeOffset ClosesAt { get; set; }

    /// <summary>When Modbot draws by itself. Null means somebody presses Draw.</summary>
    public DateTimeOffset? DrawAt { get; set; }

    public int WinnerCount { get; set; } = 1;

    // ── Who ──────────────────────────────────────────────────────────────────────────────

    /// <summary>One of <see cref="GiveawayEntryWays"/>.</summary>
    public string EntryWay { get; set; } = GiveawayEntryWays.Automatic;

    /// <summary>The emoji a person reacts with, for <see cref="GiveawayEntryWays.React"/>.</summary>
    public string Emoji { get; set; } = "🎉";

    /// <summary>The rule tree, as JSON. See <c>GiveawayRules</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string Rules { get; set; } = "{\"kind\":\"allOf\",\"rules\":[]}";

    /// <summary>Who is kept out and why, as JSON. See <c>GiveawayExclusions</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string Exclusions { get; set; } = "{}";

    /// <summary>One of <c>GiveawayWeights</c>.</summary>
    public string Weighting { get; set; } = "uniform";

    /// <summary>The most weight one person may hold, or null for no cap.</summary>
    public long? WeightCap { get; set; }

    // ── Where it goes ────────────────────────────────────────────────────────────────────

    public bool PostToChannel { get; set; }

    public string? ChannelId { get; set; }

    // ── Where it is now ──────────────────────────────────────────────────────────────────

    /// <summary>One of <see cref="GiveawayStates"/>.</summary>
    public string State { get; set; } = GiveawayStates.Draft;

    /// <summary>SHA-256 of the seed the next draw will use, hex. Shown before the draw.</summary>
    public string SeedPromise { get; set; } = string.Empty;

    /// <summary>The seed itself, encrypted, until a draw reveals it.</summary>
    public string SeedEncrypted { get; set; } = string.Empty;

    /// <summary>How many draws have been made. The next one is this plus one.</summary>
    public int DrawCount { get; set; }

    /// <summary>Goes up on every change a person makes.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Set when deleted: hidden from the page, kept for the facts that point at it.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>The words stored in <see cref="GiveawayPost.State"/>.</summary>
public static class GiveawayPostStates
{
    public const string Waiting = "waiting";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Removed = "removed";
}

/// <summary>
/// The giveaway's Discord post, and what was last written to it. The table is
/// <c>giveaway_post</c>, one row per giveaway.
/// </summary>
/// <remarks>
/// The same shape as <see cref="CalendarEventPlace"/> and for the same reason: the post is written
/// only when what it should say differs from <see cref="SentFingerprint"/>, so an edit, the
/// giveaway closing and the winners being announced all reach Discord by one path and a quiet pass
/// costs no Discord calls (calendar design §3).
/// </remarks>
public class GiveawayPost
{
    public Guid GiveawayId { get; set; }

    /// <summary>One of <see cref="GiveawayPostStates"/>.</summary>
    public string State { get; set; } = GiveawayPostStates.Waiting;

    /// <summary>Discord's id for the message.</summary>
    public string? MessageId { get; set; }

    /// <summary>The channel the message is actually in.</summary>
    public string? ChannelId { get; set; }

    /// <summary>A hash of what was last written successfully.</summary>
    public string? SentFingerprint { get; set; }

    /// <summary>A hash of what was last refused. Not sent again until it changes.</summary>
    public string? FailedFingerprint { get; set; }

    /// <summary>How many draws have been announced in the channel. Stops a second announcement.</summary>
    public int AnnouncedDraws { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? ErrorAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One reaction on the giveaway's Discord post. The table is <c>giveaway_entry</c>.
/// </summary>
/// <remarks>
/// <para>
/// The raw act of entering, not a decision about whether it counts. Whether somebody qualifies is
/// checked when the reaction arrives and again at the draw, because a person can qualify on Monday
/// and not on Friday (giveaways design §4.2) — so this row says only "they reacted, and here is
/// when".
/// </para>
/// <para>
/// Taking the reaction off sets <see cref="WithdrawnAt"/> rather than deleting the row, so that
/// putting it back on is recognised as the same person returning and the history of both is
/// intact. A purge deletes these rows outright (giveaways design §6.3).
/// </para>
/// </remarks>
public class GiveawayEntry
{
    public Guid GiveawayId { get; set; }

    /// <summary>Discord's id for the person who reacted. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    public DateTimeOffset EnteredAt { get; set; }

    /// <summary>Set when they took the reaction off. Cleared if they put it back.</summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    /// <summary>Whether they passed the rules when they reacted. Checked again at the draw.</summary>
    public bool QualifiedOnEntry { get; set; }

    /// <summary>One of <c>GiveawayKeptOut</c> when they did not qualify on entry.</summary>
    public string? KeptOut { get; set; }
}

/// <summary>
/// One draw: a fact, with the parameters it ran under and the seed it ran on. The table is
/// <c>giveaway_draw</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never changed once written, and never re-run in place</strong> (giveaways design §5.3).
/// Drawing again writes a second row with the next <see cref="Number"/>, its own seed, its own
/// snapshot and its own winners, so the first draw and the fact that somebody was not happy with
/// it both stay visible.
/// </para>
/// <para>
/// The rules, exclusions, weighting and cap are copied here rather than read back off the
/// giveaway, because the giveaway can be edited afterwards and a draw that silently reports
/// today's rules for last week's result would be worse than reporting none.
/// </para>
/// </remarks>
public class GiveawayDraw
{
    public Guid Id { get; set; }

    public Guid GiveawayId { get; set; }

    /// <summary>1 for the first draw, 2 for the next. What makes a re-draw visibly a different draw.</summary>
    public int Number { get; set; }

    public DateTimeOffset DrawnAt { get; set; }

    /// <summary>The Modbot account that drew, or null when the draw time came round by itself.</summary>
    public Guid? DrawnByUserId { get; set; }

    /// <summary>The seed, in the open. Published the moment the draw is made.</summary>
    public string Seed { get; set; } = string.Empty;

    /// <summary>The promise that was published before the draw: SHA-256 of <see cref="Seed"/>.</summary>
    public string SeedPromise { get; set; } = string.Empty;

    public int WinnerCount { get; set; }

    /// <summary>The rule tree as it stood, as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string Rules { get; set; } = "{}";

    /// <summary>The exclusions as they stood, as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string Exclusions { get; set; } = "{}";

    public string Weighting { get; set; } = "uniform";

    public long? WeightCap { get; set; }

    /// <summary>Everybody in the snapshot, whether or not they could win.</summary>
    public int EntrantCount { get; set; }

    /// <summary>How many of them were in the hat.</summary>
    public int InDrawCount { get; set; }

    /// <summary>The weights of everybody in the hat, added up.</summary>
    public long TotalWeight { get; set; }

    /// <summary>
    /// True when any rule or weight in this draw was answered from polled presence reports.
    /// </summary>
    /// <remarks>
    /// Presence is sampled by whichever moderator's companion happened to be in the instance, so a
    /// figure counted from it is close rather than exact (M7 §2.3). The draw says so rather than
    /// letting a page print an hours figure that looks measured.
    /// </remarks>
    public bool FromPolledData { get; set; }

    /// <summary>How many entrants were within a whisker of a threshold on an approximate rule.</summary>
    public int CloseCalls { get; set; }
}

/// <summary>
/// One person in a draw's frozen entrant list. The table is <c>giveaway_entrant</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written once, at the draw, and kept</strong> (giveaways design §5.1). This is the list
/// the draw actually ran on; without it the seed proves nothing, because nobody can check a pick
/// from a hat whose contents were not written down.
/// </para>
/// <para>
/// People who could not win are here too, with <see cref="KeptOut"/> saying why. An entrant list
/// that quietly omitted them would hide exactly the decisions §5.2 says must be visible.
/// </para>
/// </remarks>
public class GiveawayEntrant
{
    public Guid DrawId { get; set; }

    /// <summary>Where they sit in the frozen list. The draw walks the list in this order.</summary>
    public int Position { get; set; }

    /// <summary>
    /// How the snapshot names them: <c>vrchat:usr_…</c> or <c>discord:…</c>. What the draw is
    /// reproduced against.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string? VRChatUserId { get; set; }

    public string? DiscordUserId { get; set; }

    /// <summary>The name they went by at the draw. Untrusted text, like any display name.</summary>
    public string? Name { get; set; }

    /// <summary>Their weight, a whole number. Zero for anybody who could not win.</summary>
    public long Weight { get; set; }

    /// <summary>The number the weight was counted from, before rounding and the cap.</summary>
    public decimal Measured { get; set; }

    /// <summary>One of <c>GiveawayKeptOut</c>. Empty means they were in the hat.</summary>
    public string KeptOut { get; set; } = string.Empty;

    /// <summary>The rule they failed, in plain words, when the rules kept them out.</summary>
    public string? Because { get; set; }

    /// <summary>True when their weight or a rule about them came from polled presence reports.</summary>
    public bool FromPolledData { get; set; }

    /// <summary>True when a measurement of theirs sat within a whisker of a threshold.</summary>
    public bool CloseCall { get; set; }

    /// <summary>1 for the first name drawn, and so on. Null for everybody else.</summary>
    public int? WinnerRank { get; set; }

    /// <summary>
    /// Set when the person was erased at their own request. Their weight and place stay so the
    /// draw is still reproducible; their name and ids do not (giveaways design §6.3).
    /// </summary>
    public bool Purged { get; set; }
}
