using System.Reflection;

namespace Modbot.Core.Configuration;

/// <summary>
/// The git commit and branch the running build was made from, so a bug report can name the exact
/// code rather than only the calendar version.
/// </summary>
/// <param name="Commit">The full commit id, or null when nothing says.</param>
/// <param name="Branch">The branch that commit was built from, or null when nothing says.</param>
/// <remarks>
/// <para>
/// <strong>What the build wrote down wins over what the process can see.</strong> Modbot.Host.csproj
/// stamps both values into the assembly as <see cref="AssemblyMetadataAttribute"/> entries, from
/// the build properties, then Railway's and GitHub Actions' build variables, then git itself. That
/// is the only answer tied to the code actually running: a runtime variable describes the
/// deployment, which is usually the same build but is not guaranteed to be.
/// </para>
/// <para>
/// The runtime variables are the fallback for a build that recorded nothing -- a Docker build on
/// Railway whose build arguments did not arrive, say, where Railway still sets the same variables
/// on the running container. Each value falls back on its own.
/// </para>
/// <para>
/// Like <see cref="HostPlatform"/>, these are variables the host sets for its own reasons, which
/// Modbot only observes. None of them is configuration.
/// </para>
/// </remarks>
public sealed record BuildSource(string? Commit, string? Branch)
{
    /// <summary>The assembly metadata key the build writes the commit under.</summary>
    public const string CommitKey = "ModbotCommit";

    /// <summary>The assembly metadata key the build writes the branch under.</summary>
    public const string BranchKey = "ModbotBranch";

    public static readonly BuildSource Unknown = new(null, null);

    /// <param name="built">Reads what the build stamped in, by key. See <see cref="ReadFrom"/>.</param>
    /// <param name="read">Reads an environment variable. The real process environment when null.</param>
    public static BuildSource Detect(Func<string, string?> built, Func<string, string?>? read = null)
    {
        ArgumentNullException.ThrowIfNull(built);
        read ??= Environment.GetEnvironmentVariable;

        var commit = Clean(built(CommitKey))
            ?? Clean(read("RAILWAY_GIT_COMMIT_SHA"))
            ?? Clean(read("GITHUB_SHA"));

        var branch = Clean(built(BranchKey))
            ?? Clean(read("RAILWAY_GIT_BRANCH"))
            ?? GitHubBranch(read);

        return new BuildSource(commit, branch);
    }

    /// <summary>Reads the <see cref="AssemblyMetadataAttribute"/> entries an assembly was built with.</summary>
    public static Func<string, string?> ReadFrom(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            values[entry.Key] = entry.Value;

        return key => values.GetValueOrDefault(key);
    }

    /// <summary>
    /// On a pull request GITHUB_REF_NAME is "123/merge", so the head branch is the one to name. On a
    /// tag push it is the tag, which is not a branch at all, so it only counts when the ref is one.
    /// </summary>
    private static string? GitHubBranch(Func<string, string?> read)
    {
        if (Clean(read("GITHUB_HEAD_REF")) is { } head) return head;

        return string.Equals(read("GITHUB_REF_TYPE"), "branch", StringComparison.Ordinal)
            ? Clean(read("GITHUB_REF_NAME"))
            : null;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
