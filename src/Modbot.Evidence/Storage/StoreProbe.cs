namespace Modbot.Evidence.Storage;

/// <summary>What reading the sentinel found. Never an exception — absence is a result.</summary>
/// <remarks>
/// The separation between <see cref="Absent"/> and <see cref="Unreachable"/> is the load-bearing
/// one. "The store said no" and "the store said nothing" look similar in a log and mean opposite
/// things: the first is evidence of loss, the second is a bucket blip. Conflating them either
/// raises a full-width "your evidence is gone" banner every time a network hiccups — which teaches
/// operators to dismiss the one banner that matters — or stays quiet through an unmounted volume.
/// </remarks>
public enum StoreProbeOutcome
{
    /// <summary>A sentinel was read and parsed. Whether it is <em>ours</em> is not this layer's call.</summary>
    Present,

    /// <summary>
    /// The store answered and there is no sentinel: an unmounted volume, a fresh bucket, a
    /// mistyped prefix.
    /// </summary>
    Absent,

    /// <summary>Something is at the key and it is not a sentinel.</summary>
    Malformed,

    /// <summary>The store did not answer. Says nothing at all about what is in it.</summary>
    Unreachable,
}

/// <param name="Outcome">What was found.</param>
/// <param name="Sentinel">The parsed sentinel, when <see cref="StoreProbeOutcome.Present"/>.</param>
/// <param name="Explanation">One sentence, for the log and the diagnostics page.</param>
/// <param name="Failure">The transport error, when <see cref="StoreProbeOutcome.Unreachable"/>.</param>
public sealed record StoreProbe(
    StoreProbeOutcome Outcome,
    StoreSentinel? Sentinel,
    string Explanation,
    Exception? Failure = null)
{
    public static StoreProbe Present(StoreSentinel sentinel, string where)
        => new(StoreProbeOutcome.Present, sentinel,
            $"'{where}' holds a store sentinel for {sentinel.StoreId}.");

    public static StoreProbe Absent(string where)
        => new(StoreProbeOutcome.Absent, null,
            $"'{where}' holds no store sentinel, so it is not the store Modbot was configured with.");

    public static StoreProbe Malformed(string where)
        => new(StoreProbeOutcome.Malformed, null,
            $"'{where}' holds something at the sentinel key that is not a Modbot store sentinel.");

    public static StoreProbe Unreachable(string where, Exception failure)
        => new(StoreProbeOutcome.Unreachable, null,
            $"'{where}' did not answer ({failure.GetType().Name}: {failure.Message}).", failure);
}
