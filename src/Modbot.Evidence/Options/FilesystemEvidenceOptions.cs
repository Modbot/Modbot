namespace Modbot.Evidence.Options;

/// <summary>Where the filesystem backend puts objects (design section 4.2).</summary>
public sealed record FilesystemEvidenceOptions
{
    /// <summary>
    /// The evidence directory. Conventionally <c>/app/data/evidence</c>, a documented mount point
    /// the operator mounts deliberately — Modbot declares no <c>VOLUME</c> (design section 3.1).
    /// </summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// The operator's "use anyway" for a directory Modbot could not confirm survives a restart.
    /// </summary>
    public EvidenceDurabilityAcknowledgement Durability { get; set; } = new();
}

/// <summary>
/// An operator's recorded acknowledgement that they were warned about an unproven disk and chose
/// to proceed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because detection here can only ever be a suspicion.</strong> Railway,
/// Fly.io and Render all support mountable volumes, so "this looks like a platform that discards
/// its filesystem" does not mean "this directory is ephemeral" — it means "this directory is
/// ephemeral unless the operator mounted something Modbot cannot see". Acting on the suspicion
/// withholds the artefact the operator went looking for, in the situation where they most need it.
/// <see cref="Core.Configuration.PersistenceProbe"/>'s remarks make the same argument for log files
/// and reached the same conclusion.
/// </para>
/// <para>
/// So the filesystem backend is never disabled by a guess. It warns, it asks, and it
/// <em>records the answer</em> — which is why this is a stored value and not a boolean passed at a
/// call site. The settings page shows the operator what they agreed to and when, because an
/// acknowledgement nobody can see again is indistinguishable from one that was never given.
/// </para>
/// <para>
/// The one thing no acknowledgement can override is a directory that cannot be written to at all.
/// That is a fact rather than a suspicion, and there is nothing to decide about it.
/// </para>
/// </remarks>
public sealed record EvidenceDurabilityAcknowledgement
{
    /// <summary>Whether the operator chose to use this directory despite the warning.</summary>
    public bool UseAnyway { get; set; }

    /// <summary>Who acknowledged it, for the settings page to show back.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// When. Supplied by the caller from <see cref="Core.Time.IModbotClock"/>; nothing in this
    /// project reads a machine clock.
    /// </summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>
    /// The warning text the operator was shown. Kept verbatim so that a later change to the
    /// wording does not rewrite what they actually agreed to.
    /// </summary>
    public string? WarningShown { get; set; }
}
