namespace Modbot.Evidence.Options;

/// <summary>Settings for the in-database backend (design section 4.3).</summary>
public sealed record DatabaseEvidenceOptions
{
    /// <summary>
    /// Connection string for the evidence tables. Normally the same database as everything else.
    /// </summary>
    /// <remarks>
    /// Taken here rather than reached for through <c>ModbotContext</c> because evidence is streamed
    /// in chunks on its own connection — a tracked <c>DbContext</c> is the wrong instrument for
    /// pushing a hundred megabytes through, and holding a scoped context open for the length of a
    /// video read would tie up a request's connection for minutes.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Bytes per row. About 1 MiB, which is the number design section 4.3 settled on.
    /// </summary>
    /// <remarks>
    /// The 1 GB ceiling on a single PostgreSQL value is the hard reason to chunk, but it is not the
    /// interesting one: a row large enough to be legal is still large enough to be pathological,
    /// and range reads — which is how video seeking works at all (design section 10.5) — need the
    /// value split into pieces somebody can fetch individually.
    /// </remarks>
    public int ChunkBytes { get; set; } = 1024 * 1024;
}
