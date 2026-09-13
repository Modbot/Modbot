using Modbot.Core.Configuration;
using Modbot.Evidence.Options;

namespace Modbot.Evidence.Health;

/// <summary>What is actually known about the durability of a chosen directory.</summary>
public enum DurabilityFinding
{
    /// <summary>A marker from an earlier boot was found. This directory survives restarts.</summary>
    Durable,

    /// <summary>
    /// Not proven either way, on a platform whose container filesystem is usually discarded.
    /// <strong>A suspicion, never a finding</strong> — see <see cref="EvidenceDurabilityAdvisor"/>.
    /// </summary>
    Unproven,

    /// <summary>The directory cannot be written to. Not a suspicion; there is nothing to judge.</summary>
    Unwritable,
}

/// <param name="Finding">What is known.</param>
/// <param name="Message">The sentence shown to the operator, verbatim.</param>
/// <param name="RequiresAcknowledgement">Whether proceeding needs an explicit "use anyway".</param>
/// <param name="Acknowledged">Whether the operator has given one.</param>
/// <param name="CanProceed">Whether the filesystem backend may be selected as things stand.</param>
public sealed record DurabilityAssessment(
    DurabilityFinding Finding,
    string Message,
    bool RequiresAcknowledgement,
    bool Acknowledged,
    bool CanProceed)
{
    /// <summary>
    /// Whether this rests on platform inference rather than on evidence. A suspicion never blocks
    /// on its own; it asks.
    /// </summary>
    public bool IsSuspicion => Finding is DurabilityFinding.Unproven;
}

/// <summary>
/// Decides what to tell an operator who has chosen the filesystem backend — and, deliberately,
/// decides nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Detect, warn, and let the operator proceed anyway. Never refuse on a suspicion.</strong>
/// Platform detection can only ever be a suspicion here, because Railway, Fly.io and Render all
/// support mountable volumes — so "this looks like a platform that throws its disk away" is really
/// "this platform throws its disk away unless the operator mounted something Modbot cannot see",
/// which is a much weaker claim and one the operator is better placed to settle than Modbot is.
/// </para>
/// <para>
/// An earlier version of the logging code got this wrong in the other direction: it detected a
/// managed platform and silently switched file logging off, withholding the artefact the operator
/// went looking for on the strength of a guess. The fix there was to warn and let them decide, and
/// the same reasoning applies with more force to evidence, where the cost of being wrong is
/// somebody's case file.
/// </para>
/// <para>
/// <strong>What this is not.</strong> This is not the store marker, and the two must not be
/// confused. The store marker answers "is this the store we put our evidence in?", has memory outside
/// the directory being tested, and therefore gets to treat absence as conclusive and lock on it
/// (design section 8.2.1). This answers "will this directory survive a restart?", has no memory
/// outside the directory, and therefore gets to warn and nothing more.
/// </para>
/// </remarks>
public static class EvidenceDurabilityAdvisor
{
    /// <summary>
    /// The warning, kept as a constant so that what an operator acknowledged can be compared with
    /// what they were shown.
    /// </summary>
    public const string UnprovenWarning =
        "Modbot could not confirm that this directory survives a restart. Evidence stored here may "
        + "be lost the next time the container is recreated, and there would be no error when it "
        + "happens — the reports would still list their attachments. Object storage is strongly "
        + "recommended. If you have mounted a volume here, Modbot cannot see that from inside the "
        + "container: choose \"use anyway\" and this warning will stop by itself once a restart has "
        + "proven the directory persists.";

    /// <summary>Runs the persistence probe and judges the result.</summary>
    /// <param name="options">Carries the directory and the operator's acknowledgement, if any.</param>
    /// <param name="platform">Supplies the default for a first boot, and nothing stronger.</param>
    /// <param name="bootId">Unique to this process run. See <see cref="PersistenceProbe"/>.</param>
    public static DurabilityAssessment Assess(
        FilesystemEvidenceOptions options, HostPlatform platform, string bootId)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Assess(options, platform, PersistenceProbe.Probe(options.Root, bootId));
    }

    /// <summary>Judges a probe result that has already been taken.</summary>
    public static DurabilityAssessment Assess(
        FilesystemEvidenceOptions options, HostPlatform platform, PersistenceProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(probe);

        var acknowledged = options.Durability.UseAnyway;

        if (probe.IsUnwritable)
        {
            // The one case with nothing to decide. No acknowledgement can make an unwritable
            // directory hold a file, so this is the single place that refuses — and it refuses on
            // a fact, not on an inference about the platform.
            return new DurabilityAssessment(
                DurabilityFinding.Unwritable,
                probe.Explanation + " Evidence cannot be stored here at all, whatever the platform is.",
                RequiresAcknowledgement: false,
                Acknowledged: acknowledged,
                CanProceed: false);
        }

        if (!PersistenceProbe.IsRiskyPlacement(platform, probe.Evidence))
        {
            return new DurabilityAssessment(
                DurabilityFinding.Durable,
                probe.Explanation,
                RequiresAcknowledgement: false,
                Acknowledged: acknowledged,
                CanProceed: true);
        }

        // A suspicion. It asks; it does not decide.
        return new DurabilityAssessment(
            DurabilityFinding.Unproven,
            UnprovenWarning,
            RequiresAcknowledgement: true,
            Acknowledged: acknowledged,
            CanProceed: acknowledged);
    }
}
