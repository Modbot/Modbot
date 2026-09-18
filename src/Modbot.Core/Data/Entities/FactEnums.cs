namespace Modbot.Core.Data.Entities;

/// <summary>
/// Which system an identifier belongs to.
/// </summary>
/// <remarks>
/// Spec 5.3. Its own column rather than a namespaced string (<c>dc:123…</c>) so the identifier
/// stays joinable against the user tables without parsing, and so a Discord snowflake can never
/// collide with a VRChat id in the same text column.
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </remarks>
public enum FactPlatform : short
{
    VRChat = 1,
    Discord = 2,

    /// <summary>Modbot itself: the subject or actor of its own audit entries (spec 5.9).</summary>
    Modbot = 3,
}

/// <summary>
/// Where a fact came from, which is also what tells you how much to trust its timestamp.
/// </summary>
/// <remarks>
/// Spec 5.3. An <see cref="AuditLog"/> ban is exact; a <see cref="SyncDiff"/> one is an inference
/// with a window. Same event, different confidence -- and when both arrive, the authoritative one
/// wins.
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </remarks>
public enum FactSource : short
{
    /// <summary>VRChat's own group audit log. Authoritative and exact.</summary>
    AuditLog = 1,

    /// <summary>Inferred by comparing two syncs. Carries an <c>occurred_before</c> window.</summary>
    SyncDiff = 2,

    /// <summary>Reported by a moderator's Windows client (M3). The deduplicated path.</summary>
    Client = 3,

    /// <summary>Observed in Discord (M5).</summary>
    Discord = 4,

    /// <summary>Entered by a person through Modbot.</summary>
    Manual = 5,

    /// <summary>
    /// Modbot's own record of what happened inside Modbot: logins, settings changes, sync
    /// failures. There is no second audit system -- the fact log already is the audit log
    /// (spec 5.9).
    /// </summary>
    Modbot = 6,

    /// <summary>
    /// <strong>Legacy. Only rows written before the import redesign hold this.</strong>
    /// </summary>
    /// <remarks>
    /// It meant "a person uploaded this from somewhere else". That answered the wrong question:
    /// this column says <em>who says so</em>, and every reader -- the audit log's Source chips,
    /// the badge on a row, a Discord route deciding how much to trust a timestamp -- reads it that
    /// way. "Somebody uploaded a file" is an answer to <em>how did this get here</em> instead, and
    /// it hid the real answer: a ban a group carried over from VRChat's own audit log is a ban
    /// VRChat recorded, whoever moved it.
    /// <para>
    /// So an imported record now names the source it really came from (import design §5), and how
    /// it got here lives in the fact's own <c>importId</c> and in <c>import_record</c>. The member
    /// stays because existing rows carry the number 7 -- it is never renumbered and never reused
    /// -- and it stays listed wherever sources are listed, so those rows can still be found. It is
    /// simply never written again.
    /// </para>
    /// </remarks>
    Import = 7,
}

// FactType moved to FactType.cs and became hierarchical strings -- see the remarks there
// for why, and for what the smallint enum was costing.

