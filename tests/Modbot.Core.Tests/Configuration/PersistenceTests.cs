using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Configuration;

/// <summary>
/// Modbot writes two things to disk that matter — log files, and evidence attached to ban reports.
/// On a platform that discards the container filesystem, both vanish. Logs vanishing is an
/// annoyance; evidence vanishing is discovered during a dispute months later.
/// </summary>
public class HostPlatformTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] set)
    {
        var map = set.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Theory]
    [InlineData("RAILWAY_ENVIRONMENT", "Railway")]
    [InlineData("RAILWAY_SERVICE_ID", "Railway")]
    [InlineData("FLY_APP_NAME", "Fly.io")]
    [InlineData("FLY_MACHINE_ID", "Fly.io")]
    [InlineData("RENDER_SERVICE_ID", "Render")]
    [InlineData("DYNO", "Heroku")]
    [InlineData("VERCEL", "Vercel")]
    [InlineData("KOYEB_APP_NAME", "Koyeb")]
    [InlineData("NF_INSTANCE_ID", "Northflank")]
    [InlineData("K_REVISION", "Google Cloud Run")]
    [InlineData("CONTAINER_APP_NAME", "Azure Container Apps")]
    [InlineData("ECS_CONTAINER_METADATA_URI_V4", "AWS ECS / Fargate")]
    public void ManagedPlatformsAreRecognisedAndAssumedEphemeral(string variable, string expected)
    {
        var platform = HostPlatform.Detect(Env((variable, "set")));

        Assert.Equal(expected, platform.Name);
        Assert.True(platform.AssumeEphemeralFilesystem);
        Assert.Equal(variable, platform.Evidence);
    }

    /// <summary>
    /// An unrecognised host is somebody's own Docker host or VM, where the disk does survive.
    /// </summary>
    /// <remarks>
    /// Defaulting the other way would silently disable file logging for every self-hoster running
    /// plain <c>docker run</c> with a bind mount — the exact audience the file logs exist for.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedHostIsAssumedPersistent()
    {
        var platform = HostPlatform.Detect(Env());

        Assert.Equal("self-hosted", platform.Name);
        Assert.False(platform.AssumeEphemeralFilesystem);
        Assert.Null(platform.Evidence);
    }

    /// <summary>
    /// Kubernetes is recognised but <strong>not</strong> assumed ephemeral.
    /// </summary>
    /// <remarks>
    /// A PersistentVolumeClaim is the ordinary way to run a stateful workload there, so assuming
    /// otherwise would be wrong far more often than right. This is also why Kubernetes is matched
    /// last: several of the managed platforms above run on it, and they have their own answer.
    /// </remarks>
    [Fact]
    public void KubernetesIsRecognisedButNotAssumedEphemeral()
    {
        var platform = HostPlatform.Detect(Env(("KUBERNETES_SERVICE_HOST", "10.0.0.1")));

        Assert.Equal("Kubernetes", platform.Name);
        Assert.False(platform.AssumeEphemeralFilesystem);
    }

    /// <summary>
    /// A managed platform running on Kubernetes reports as itself, not as Kubernetes.
    /// </summary>
    /// <remarks>
    /// Both sets of variables are present in that case, and the more specific one is the one with
    /// the useful answer — Kubernetes would say "assume persistent" for a Railway container whose
    /// disk is thrown away on every redeploy.
    /// </remarks>
    [Fact]
    public void TheMoreSpecificPlatformWins()
    {
        var platform = HostPlatform.Detect(
            Env(("KUBERNETES_SERVICE_HOST", "10.0.0.1"), ("RAILWAY_PROJECT_ID", "abc")));

        Assert.Equal("Railway", platform.Name);
        Assert.True(platform.AssumeEphemeralFilesystem);
    }
}

public class PersistenceProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "modbot-probe-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A first boot cannot tell a fresh directory from a wiped one, and must not pretend to.
    /// </summary>
    /// <remarks>
    /// Reporting "ephemeral" here would flag every genuine first start as a fault. Proving
    /// ephemerality needs memory outside the directory under test.
    /// </remarks>
    [Fact]
    public void AFirstBootFindsNoEvidence()
    {
        var result = PersistenceProbe.Probe(_dir, "boot-one");

        Assert.Equal(PersistenceEvidence.None, result.Evidence);
        Assert.True(File.Exists(Path.Combine(_dir, PersistenceProbe.MarkerFileName)));
    }

    /// <summary>
    /// A marker from a <em>different</em> boot proves the directory survived a restart.
    /// </summary>
    [Fact]
    public void AMarkerFromAnEarlierBootProvesPersistence()
    {
        PersistenceProbe.Probe(_dir, "boot-one");
        var second = PersistenceProbe.Probe(_dir, "boot-two");

        Assert.Equal(PersistenceEvidence.SurvivedRestart, second.Evidence);
    }

    /// <summary>
    /// Reading back a marker this same process wrote proves nothing.
    /// </summary>
    /// <remarks>
    /// Without the boot-id comparison the probe would report persistence on its own second call
    /// within a single run, and every deployment would believe its disk was durable.
    /// </remarks>
    [Fact]
    public void TheSameBootReadingItsOwnMarkerIsNotEvidence()
    {
        PersistenceProbe.Probe(_dir, "boot-one");
        var again = PersistenceProbe.Probe(_dir, "boot-one");

        Assert.Equal(PersistenceEvidence.None, again.Evidence);
    }

    /// <summary>Evidence beats the platform's guess, in both directions.</summary>
    /// <remarks>
    /// This is the property that makes the whole design self-correcting. An operator who mounts a
    /// volume on Railway is told "ephemeral" for exactly one boot and "persistent" from the next
    /// restart onward, with nothing to configure — and an operator who removes that volume
    /// reverts the same way.
    /// </remarks>
    [Fact]
    public void ProvenPersistenceOverridesAnEphemeralPlatform()
    {
        var railway = HostPlatform.Detect(k => k == "RAILWAY_ENVIRONMENT" ? "production" : null);

        Assert.False(PersistenceProbe.ShouldPersist(railway, PersistenceEvidence.None));
        Assert.True(PersistenceProbe.ShouldPersist(railway, PersistenceEvidence.SurvivedRestart));
    }

    /// <summary>An unwritable directory is never used, whatever the platform is.</summary>
    [Fact]
    public void AnUnwritableDirectoryIsNeverUsed()
    {
        Assert.False(PersistenceProbe.ShouldPersist(HostPlatform.SelfHosted, PersistenceEvidence.Unwritable));
    }

    /// <summary>
    /// The probe runs before logging exists, on a path supplied by the environment. It reports a
    /// problem; it never throws one.
    /// </summary>
    [Fact]
    public void AnImpossiblePathIsReportedRatherThanThrown()
    {
        // A path under a file rather than a directory: creation cannot succeed.
        var file = Path.Combine(_dir, "not-a-directory");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "x");

        var result = PersistenceProbe.Probe(Path.Combine(file, "logs"), "boot-one");

        Assert.Equal(PersistenceEvidence.Unwritable, result.Evidence);
        Assert.Contains("cannot be read or created", result.Explanation, StringComparison.Ordinal);
    }
}
