namespace Modbot.Evidence.Storage;

/// <summary>
/// What a store can actually do, declared rather than discovered (design section 13.2).
/// </summary>
/// <remarks>
/// <para>
/// Presigned URLs are the one thing the three backends genuinely disagree about. A filesystem store
/// that threw <c>NotSupportedException</c> from a presign call would push that disagreement into a
/// runtime failure on a path only some deployments exercise — which means it is found in
/// production, by an operator, on the backend the developer never ran.
/// </para>
/// <para>
/// M3 section 7.3 set the precedent: file-id support in an avatar provider is "a capability flag,
/// not a detail", declared in a table and shown in the settings UI so an operator cannot select a
/// provider that silently never works. Same treatment, same reason.
/// </para>
/// <para>
/// The rule callers rely on is a biconditional, and it is what makes this testable rather than
/// merely documented: <see cref="IEvidenceStore.TryCreatePresignedReadAsync"/> returns non-null
/// <strong>if and only if</strong> <see cref="PresignedRead"/> is present. Callers branch on the
/// flag. Nothing throws.
/// </para>
/// </remarks>
[Flags]
public enum EvidenceStoreCapabilities
{
    None = 0,

    /// <summary>The store can hand a browser a URL that reads an object without Modbot.</summary>
    PresignedRead = 1 << 0,

    /// <summary>The store can hand a browser a URL that writes the staging object.</summary>
    PresignedWrite = 1 << 1,

    /// <summary>Reads can start at an offset — which is what makes video seeking work.</summary>
    RangeRead = 1 << 2,

    /// <summary>
    /// Staging can be promoted to its final key without the bytes crossing the application.
    /// </summary>
    ServerSideCopy = 1 << 3,
}
