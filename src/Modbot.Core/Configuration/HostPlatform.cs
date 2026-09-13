namespace Modbot.Core.Configuration;

/// <summary>
/// The hosting platform Modbot appears to be running on, inferred from the variables that
/// platform injects into its own containers.
/// </summary>
/// <param name="Name">Human-readable, for the startup banner and the diagnostics page.</param>
/// <param name="AssumeEphemeralFilesystem">
/// Whether to assume the container filesystem does not survive a redeploy, <em>in the absence of
/// evidence either way</em>. A default, never a finding — see the remarks.
/// </param>
/// <param name="Evidence">The variable that produced the match, for the operator to check.</param>
/// <remarks>
/// <para>
/// This is deliberately <strong>not</strong> configuration. Modbot reads exactly three environment
/// variables (<see cref="ModbotEnvironment"/>) and that has not changed; these are variables the
/// host sets for its own reasons, which Modbot only observes. Nobody is expected to set one, and
/// setting one by hand only changes a default that evidence overrides on the next restart.
/// </para>
/// <para>
/// <strong>Platform detection cannot decide persistence, and must not pretend to.</strong> Railway,
/// Fly.io and Render all offer mountable volumes, so "this is a PaaS" does not imply "this disk is
/// ephemeral" — it implies "this disk is ephemeral <em>unless the operator mounted something</em>",
/// which is a different and much weaker claim. <see cref="PersistenceProbe"/> settles it from
/// evidence; this type only supplies the answer for a first boot, when there is no evidence yet.
/// </para>
/// </remarks>
public sealed record HostPlatform(string Name, bool AssumeEphemeralFilesystem, string? Evidence)
{
    /// <summary>
    /// Nothing recognised. Assumed persistent, because the ordinary case for an unrecognised host
    /// is somebody's own Docker host or VM, where the disk does survive.
    /// </summary>
    public static readonly HostPlatform SelfHosted = new("self-hosted", false, null);

    /// <summary>
    /// Ordered signatures. First match wins, so more specific platforms precede more general ones
    /// — a Cloud Run container also looks like a container, and Kubernetes is the fallback for
    /// several managed platforms built on top of it.
    /// </summary>
    private static readonly (string Name, string[] Variables, bool Ephemeral)[] Signatures =
    [
        // Volumes are available on all three of these, hence "assume", not "conclude".
        ("Railway", ["RAILWAY_ENVIRONMENT", "RAILWAY_PROJECT_ID", "RAILWAY_SERVICE_ID"], true),
        ("Fly.io", ["FLY_APP_NAME", "FLY_ALLOC_ID", "FLY_MACHINE_ID"], true),
        ("Render", ["RENDER", "RENDER_SERVICE_ID", "RENDER_INSTANCE_ID"], true),

        ("Heroku", ["DYNO", "HEROKU_APP_ID"], true),
        ("Vercel", ["VERCEL", "VERCEL_ENV"], true),
        ("Koyeb", ["KOYEB_APP_NAME", "KOYEB_SERVICE_ID"], true),
        ("Northflank", ["NF_INSTANCE_ID", "NF_PROJECT_ID"], true),
        ("Google Cloud Run", ["K_SERVICE", "K_REVISION"], true),
        ("Azure Container Apps", ["CONTAINER_APP_NAME", "CONTAINER_APP_REVISION"], true),
        ("AWS App Runner", ["AWS_APP_RUNNER_SERVICE_ID"], true),
        ("AWS ECS / Fargate", ["ECS_CONTAINER_METADATA_URI_V4", "ECS_CONTAINER_METADATA_URI"], true),

        // Deliberately last, and deliberately NOT ephemeral. A PersistentVolumeClaim is the
        // ordinary way to run a stateful workload on Kubernetes, so assuming otherwise here would
        // be wrong far more often than it was right -- and several platforms above are Kubernetes
        // underneath, which is why they are matched first.
        ("Kubernetes", ["KUBERNETES_SERVICE_HOST"], false),
    ];

    public static HostPlatform Detect(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        foreach (var (name, variables, ephemeral) in Signatures)
        {
            var hit = variables.FirstOrDefault(v => !string.IsNullOrWhiteSpace(read(v)));
            if (hit is not null) return new HostPlatform(name, ephemeral, hit);
        }

        return SelfHosted;
    }
}
