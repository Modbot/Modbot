namespace Modbot.Core.Configuration;

/// <summary>What Modbot actually knows about whether a directory survives a restart.</summary>
public enum PersistenceEvidence
{
    /// <summary>
    /// No marker from an earlier boot. Either this is the first run, or the directory was wiped —
    /// and from the filesystem alone those two are indistinguishable, which is why this is
    /// "no evidence" rather than "ephemeral".
    /// </summary>
    None,

    /// <summary>
    /// A marker written by a <em>different</em> boot was found. This directory demonstrably
    /// survived a restart.
    /// </summary>
    SurvivedRestart,

    /// <summary>The directory cannot be written to at all, so nothing will persist in it.</summary>
    Unwritable,
}

/// <param name="Evidence">What was found.</param>
/// <param name="Explanation">One sentence for the log and the diagnostics page.</param>
public sealed record PersistenceProbeResult(PersistenceEvidence Evidence, string Explanation)
{
    /// <summary>
    /// The one finding that is not advisory: a directory that cannot be written to will not hold
    /// files whatever anybody decides about it.
    /// </summary>
    /// <remarks>
    /// Everything else this probe reports is information for a human. Modbot does not withhold
    /// log files because a platform <em>might</em> discard them — that judgement belongs to the
    /// operator, who knows whether they mounted a volume. This is the only case with nothing to
    /// judge.
    /// </remarks>
    public bool IsUnwritable => Evidence is PersistenceEvidence.Unwritable;
}

/// <summary>
/// Finds out whether a directory actually persists, by leaving a marker and looking for an older
/// one.
/// </summary>
/// <remarks>
/// <para>
/// Modbot writes two kinds of thing to disk that matter — log files, and (where the filesystem
/// backend is chosen) evidence attached to ban reports. On a platform whose container filesystem
/// is thrown away on redeploy, both vanish silently. Logs vanishing is an annoyance; evidence
/// vanishing is discovered during a dispute, months later, which is the worst possible moment.
/// </para>
/// <para>
/// <strong>Why a marker file rather than trusting the platform.</strong>
/// <see cref="HostPlatform"/> can only supply a default, because Railway, Fly.io and Render all
/// offer mountable volumes — so an operator who mounted one on Railway would be told their disk is
/// ephemeral when it is not. A marker settles it from evidence instead of inference: if a boot
/// finds a marker stamped with a <em>different</em> boot id, that directory survived a restart,
/// whatever the platform is.
/// </para>
/// <para>
/// The consequence is a system that corrects itself. A volume mounted on a platform assumed
/// ephemeral is treated as ephemeral for exactly one boot, and as persistent from the next restart
/// onwards. A volume that is later removed reverts the same way. Nothing has to be configured, and
/// nothing stays wrong.
/// </para>
/// <para>
/// Note what this deliberately does not claim: absence of a marker is <em>not</em> reported as
/// proof of ephemerality. Proving that needs memory outside the directory being tested — the
/// database knows whether Modbot has booted before — and inferring it from the filesystem alone
/// would report every genuine first run as a fault.
/// </para>
/// </remarks>
public static class PersistenceProbe
{
    /// <summary>Dotfile, so it does not clutter a directory an operator browses.</summary>
    public const string MarkerFileName = ".modbot-persistence";

    /// <summary>
    /// Looks for a marker from an earlier boot, then leaves one for the next.
    /// </summary>
    /// <param name="directory">The directory to test. Created if absent.</param>
    /// <param name="bootId">
    /// Unique to this process run. Comparing it against the marker's contents is what separates
    /// "a previous boot wrote this" from "I wrote this thirty milliseconds ago".
    /// </param>
    /// <remarks>
    /// Never throws. This runs before logging is configured, on a path supplied by the
    /// environment, and a deployment must not fail to start because a probe could not write a
    /// dotfile — an unwritable directory is a finding to report, not an exception to propagate.
    /// </remarks>
    public static PersistenceProbeResult Probe(string directory, string bootId)
    {
        var marker = Path.Combine(directory, MarkerFileName);

        string? previous = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(marker)) previous = File.ReadAllText(marker).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PersistenceProbeResult(
                PersistenceEvidence.Unwritable,
                $"'{directory}' cannot be read or created ({ex.GetType().Name}), so nothing "
                + "written there will survive — or arrive at all.");
        }

        try
        {
            File.WriteAllText(marker, bootId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PersistenceProbeResult(
                PersistenceEvidence.Unwritable,
                $"'{directory}' is not writable ({ex.GetType().Name}).");
        }

        // Same id means this process wrote it, which proves nothing about persistence.
        if (!string.IsNullOrEmpty(previous) && previous != bootId)
        {
            return new PersistenceProbeResult(
                PersistenceEvidence.SurvivedRestart,
                $"'{directory}' survived a restart — a marker from an earlier run was found, so "
                + "this directory persists.");
        }

        return new PersistenceProbeResult(
            PersistenceEvidence.None,
            $"'{directory}' holds no marker from an earlier run. Either this is the first start, "
            + "or the directory does not survive restarts; the next restart will distinguish them.");
    }

    /// <summary>
    /// Whether putting files here looks risky enough to warn about — <strong>not</strong> whether
    /// to allow it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modbot does not refuse to write files because a platform <em>might</em> discard them.
    /// Railway, Fly.io and Render all support volumes, so detection can never be more than a
    /// suspicion, and acting on a suspicion means an operator who mounted a volume loses the
    /// artefact they went looking for — silently, and in the situation where they most need it.
    /// The operator knows whether they mounted one; Modbot does not.
    /// </para>
    /// <para>
    /// So the output is a warning and, where there is something to choose, an override the
    /// operator can take. Evidence still settles it when there is any: a directory proven to
    /// survive a restart is never warned about, whatever the platform is assumed to be.
    /// </para>
    /// </remarks>
    public static bool IsRiskyPlacement(HostPlatform platform, PersistenceEvidence evidence)
        => evidence switch
        {
            PersistenceEvidence.SurvivedRestart => false,
            PersistenceEvidence.Unwritable => true,
            _ => platform.AssumeEphemeralFilesystem,
        };
}
