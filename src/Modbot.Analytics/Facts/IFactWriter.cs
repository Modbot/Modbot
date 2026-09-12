using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Facts;

/// <summary>
/// The only way anything writes to the fact log.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.2. Sync jobs, the audit-log reader, the Windows client's ingest endpoint and Modbot's
/// own audit entries all come through here, which is what makes deduplication (5.7) and the
/// server-stamped <c>observed_at</c> (4.4) unconditional rather than something each caller has to
/// remember.
/// </para>
/// <para>
/// There is no update and no delete. Facts are immutable; a correction is a new fact.
/// </para>
/// </remarks>
public interface IFactWriter
{
    /// <summary>
    /// Records one fact, or recognises it as a duplicate of one already recorded.
    /// </summary>
    Task<FactWriteResult> WriteAsync(FactRecord fact, CancellationToken ct = default);

    /// <summary>
    /// Records a batch, returning one result per input in the order given.
    /// </summary>
    Task<IReadOnlyList<FactWriteResult>> WriteManyAsync(
        IEnumerable<FactRecord> facts,
        CancellationToken ct = default);
}

/// <summary>Which facts go through the deduplication path.</summary>
/// <remarks>
/// <para>
/// Spec 5.7 sizes deduplication for exactly one situation: four to six moderator clients in the
/// same instance independently reporting the same join. Nothing else arrives sixfold.
/// </para>
/// <para>
/// It is <strong>not</strong> applied to every source, and that restraint matters. Modbot's own
/// audit entries share this table (spec 5.9); two settings changes a second apart are two things
/// that happened, and a windowed merge would silently delete audit history -- the exact opposite
/// of what an audit log is for.
/// </para>
/// </remarks>
public static class FactDeduplication
{
    public static bool AppliesTo(FactSource source) => source is FactSource.Client;
}
