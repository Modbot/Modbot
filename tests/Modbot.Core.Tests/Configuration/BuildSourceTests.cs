using System.Reflection;
using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Configuration;

/// <summary>
/// The commit and branch on the Deployment card: what the build wrote down first, then what the
/// host says at runtime, then nothing.
/// </summary>
public class BuildSourceTests
{
    private const string BuiltCommit = "0123456789abcdef0123456789abcdef01234567";

    private static Func<string, string?> Map(params (string Key, string Value)[] set)
    {
        var map = set.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void WhatTheBuildWroteDownWinsOverTheEnvironment()
    {
        var source = BuildSource.Detect(
            Map((BuildSource.CommitKey, BuiltCommit), (BuildSource.BranchKey, "master")),
            Map(("RAILWAY_GIT_COMMIT_SHA", "railway-commit"), ("RAILWAY_GIT_BRANCH", "other"),
                ("GITHUB_SHA", "github-commit"), ("GITHUB_HEAD_REF", "feature")));

        Assert.Equal(BuiltCommit, source.Commit);
        Assert.Equal("master", source.Branch);
    }

    [Fact]
    public void RailwaysVariablesAreUsedWhenTheBuildRecordedNothing()
    {
        var source = BuildSource.Detect(
            Map(),
            Map(("RAILWAY_GIT_COMMIT_SHA", "railway-commit"), ("RAILWAY_GIT_BRANCH", "master")));

        Assert.Equal("railway-commit", source.Commit);
        Assert.Equal("master", source.Branch);
    }

    [Fact]
    public void RailwayIsPreferredOverGitHub()
    {
        var source = BuildSource.Detect(
            Map(),
            Map(("RAILWAY_GIT_COMMIT_SHA", "railway-commit"), ("RAILWAY_GIT_BRANCH", "master"),
                ("GITHUB_SHA", "github-commit"), ("GITHUB_REF_TYPE", "branch"), ("GITHUB_REF_NAME", "other")));

        Assert.Equal("railway-commit", source.Commit);
        Assert.Equal("master", source.Branch);
    }

    [Fact]
    public void GitHubsBranchIsUsedOnABranchPush()
    {
        var source = BuildSource.Detect(
            Map(),
            Map(("GITHUB_SHA", "github-commit"), ("GITHUB_REF_TYPE", "branch"), ("GITHUB_REF_NAME", "master")));

        Assert.Equal("github-commit", source.Commit);
        Assert.Equal("master", source.Branch);
    }

    /// <summary>On a pull request GITHUB_REF_NAME is "123/merge", which is not a branch anybody made.</summary>
    [Fact]
    public void APullRequestNamesItsHeadBranch()
    {
        var source = BuildSource.Detect(
            Map(),
            Map(("GITHUB_HEAD_REF", "feature"), ("GITHUB_REF_TYPE", "branch"), ("GITHUB_REF_NAME", "123/merge")));

        Assert.Equal("feature", source.Branch);
    }

    [Fact]
    public void ATagIsNotABranch()
    {
        var source = BuildSource.Detect(
            Map(),
            Map(("GITHUB_SHA", "github-commit"), ("GITHUB_REF_TYPE", "tag"), ("GITHUB_REF_NAME", "client-v2026.9.1")));

        Assert.Equal("github-commit", source.Commit);
        Assert.Null(source.Branch);
    }

    [Fact]
    public void EachValueFallsBackOnItsOwn()
    {
        var source = BuildSource.Detect(
            Map((BuildSource.CommitKey, BuiltCommit)),
            Map(("RAILWAY_GIT_COMMIT_SHA", "railway-commit"), ("RAILWAY_GIT_BRANCH", "master")));

        Assert.Equal(BuiltCommit, source.Commit);
        Assert.Equal("master", source.Branch);
    }

    [Fact]
    public void BlankValuesCountAsMissingAndOthersAreTrimmed()
    {
        var source = BuildSource.Detect(
            Map((BuildSource.CommitKey, "  "), (BuildSource.BranchKey, "")),
            Map(("RAILWAY_GIT_COMMIT_SHA", " railway-commit\n"), ("RAILWAY_GIT_BRANCH", " master ")));

        Assert.Equal("railway-commit", source.Commit);
        Assert.Equal("master", source.Branch);
    }

    [Fact]
    public void NothingKnownIsNull()
    {
        var source = BuildSource.Detect(Map(), Map());

        Assert.Equal(BuildSource.Unknown, source);
    }

    [Fact]
    public void AnAssemblyWithoutTheMetadataReadsAsNothing()
    {
        var read = BuildSource.ReadFrom(typeof(BuildSource).Assembly);

        Assert.Null(read(BuildSource.CommitKey));
        Assert.Null(read(BuildSource.BranchKey));
    }

    [Fact]
    public void AnAssemblysMetadataIsReadByKey()
    {
        // The test assembly carries metadata the SDK stamps on test projects; any one entry proves
        // the lookup reads the attributes rather than returning nothing.
        var entry = typeof(BuildSourceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault();
        Assert.SkipWhen(entry is null, "This test assembly carries no assembly metadata to read back.");

        Assert.Equal(entry.Value, BuildSource.ReadFrom(typeof(BuildSourceTests).Assembly)(entry.Key));
    }
}
