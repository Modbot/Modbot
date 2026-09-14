using Modbot.Core.Configuration;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;

namespace Modbot.Evidence.Tests.Health;

/// <summary>
/// Detect, warn, and let the operator proceed anyway — never refuse on a suspicion.
/// </summary>
/// <remarks>
/// The distinction these pin down is the one that is easy to lose in a refactor: a suspicion asks,
/// a fact decides. Platform detection can only ever produce the first, because Railway, Fly.io and
/// Render all support mountable volumes and Modbot cannot see a volume from inside the container.
/// </remarks>
public class DurabilityAdvisorTests
{
    private static readonly HostPlatform Managed = new("Railway", true, "RAILWAY_ENVIRONMENT");
    private static readonly HostPlatform Owned = new("self-hosted", false, null);

    private static PersistenceProbeResult Probe(PersistenceEvidence evidence)
        => new(evidence, $"probe said {evidence}.");

    private static FilesystemEvidenceOptions Options(bool acknowledged = false) => new()
    {
        Root = "/app/data/evidence",
        Durability = new EvidenceDurabilityAcknowledgement { UseAnyway = acknowledged },
    };

    /// <summary>
    /// The correction this design took on: an unproven disk is a warning with a way past it, not a
    /// disabled backend.
    /// </summary>
    [Fact]
    public void AnUnprovenDiskWarnsAndAsksRatherThanRefusing()
    {
        var assessment = EvidenceDurabilityAdvisor.Assess(
            Options(), Managed, Probe(PersistenceEvidence.None));

        Assert.Equal(DurabilityFinding.Unproven, assessment.Finding);
        Assert.True(assessment.IsSuspicion);
        Assert.True(assessment.RequiresAcknowledgement);
        Assert.False(assessment.CanProceed);
        Assert.Contains("could not confirm", assessment.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The operator may well have mounted a volume Modbot cannot see, so "use anyway" has to
    /// actually work.
    /// </summary>
    [Fact]
    public void AcknowledgingTheWarningLetsTheOperatorProceed()
    {
        var assessment = EvidenceDurabilityAdvisor.Assess(
            Options(acknowledged: true), Managed, Probe(PersistenceEvidence.None));

        Assert.Equal(DurabilityFinding.Unproven, assessment.Finding);
        Assert.True(assessment.Acknowledged);
        Assert.True(assessment.CanProceed);
    }

    /// <summary>
    /// The acknowledgement is recorded so the settings page can show it back. One nobody can see
    /// again is indistinguishable from one that was never given.
    /// </summary>
    [Fact]
    public void TheAcknowledgementRecordsWhoAndWhenAndWhatTheySaw()
    {
        var when = new DateTimeOffset(2026, 3, 4, 21, 14, 0, TimeSpan.Zero);

        var options = new FilesystemEvidenceOptions
        {
            Root = "/app/data/evidence",
            Durability = new EvidenceDurabilityAcknowledgement
            {
                UseAnyway = true,
                AcknowledgedBy = "Gunner24",
                AcknowledgedAt = when,
                WarningShown = EvidenceDurabilityAdvisor.UnprovenWarning,
            },
        };

        var assessment = EvidenceDurabilityAdvisor.Assess(options, Managed, Probe(PersistenceEvidence.None));

        Assert.True(assessment.CanProceed);
        Assert.Equal("Gunner24", options.Durability.AcknowledgedBy);
        Assert.Equal(when, options.Durability.AcknowledgedAt);
        Assert.Equal(assessment.Message, options.Durability.WarningShown);
    }

    /// <summary>
    /// Evidence outranks the guess. A directory that has survived a restart is never warned about,
    /// whatever the platform looks like — so the warning stops by itself.
    /// </summary>
    [Fact]
    public void ADirectoryThatSurvivedARestartIsNeverWarnedAbout()
    {
        var assessment = EvidenceDurabilityAdvisor.Assess(
            Options(), Managed, Probe(PersistenceEvidence.SurvivedRestart));

        Assert.Equal(DurabilityFinding.Durable, assessment.Finding);
        Assert.False(assessment.RequiresAcknowledgement);
        Assert.True(assessment.CanProceed);
    }

    [Fact]
    public void AnOrdinaryHostIsNotWarnedAboutAtAll()
    {
        var assessment = EvidenceDurabilityAdvisor.Assess(
            Options(), Owned, Probe(PersistenceEvidence.None));

        Assert.Equal(DurabilityFinding.Durable, assessment.Finding);
        Assert.True(assessment.CanProceed);
    }

    /// <summary>
    /// The single case with nothing to judge: no acknowledgement can make an unwritable directory
    /// hold a file. This refuses on a fact, not on an inference about the platform.
    /// </summary>
    [Fact]
    public void AnUnwritableDirectoryIsRefusedEvenWhenAcknowledged()
    {
        var assessment = EvidenceDurabilityAdvisor.Assess(
            Options(acknowledged: true), Owned, Probe(PersistenceEvidence.Unwritable));

        Assert.Equal(DurabilityFinding.Unwritable, assessment.Finding);
        Assert.False(assessment.IsSuspicion);
        Assert.False(assessment.RequiresAcknowledgement);
        Assert.False(assessment.CanProceed);
    }

    /// <summary>
    /// A real directory, probed twice: the first boot cannot tell, the second knows. This is what
    /// makes the warning self-clearing rather than something an operator has to dismiss forever.
    /// </summary>
    [Fact]
    public void ASecondBootInTheSameDirectoryTurnsTheSuspicionIntoEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "modbot-durability", Guid.NewGuid().ToString("N"));

        try
        {
            var options = new FilesystemEvidenceOptions { Root = root };

            var first = EvidenceDurabilityAdvisor.Assess(options, Managed, "boot-one");
            Assert.Equal(DurabilityFinding.Unproven, first.Finding);

            var second = EvidenceDurabilityAdvisor.Assess(options, Managed, "boot-two");
            Assert.Equal(DurabilityFinding.Durable, second.Finding);
            Assert.True(second.CanProceed);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
