using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Configuration;

/// <summary>
/// What the runtime has been told to leave behind if the process dies outright. Modbot only reads
/// these; they belong to .NET, and nothing here sets one.
/// </summary>
public class CrashDumpTests
{
    private static Func<string, string?> Variables(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void WithNothingSetDumpsAreOff()
    {
        var dumps = CrashDumps.Read(Variables());

        Assert.False(dumps.On);
        Assert.Null(dumps.Path);
        Assert.Null(dumps.Folder);
        Assert.Contains(CrashDumps.OnVariable, dumps.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    public void TheWaysOfSayingYes(string value)
        => Assert.True(CrashDumps.Read(Variables((CrashDumps.OnVariable, value))).On);

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("yes")]
    public void EverythingElseIsNo(string value)
        => Assert.False(CrashDumps.Read(Variables((CrashDumps.OnVariable, value))).On);

    /// <summary>
    /// The runtime writes the file and does not make the folder above it, so Modbot has to know
    /// which folder that is in order to make it before the crash rather than after.
    /// </summary>
    [Fact]
    public void TheFolderIsTheOneTheDumpWouldBeWrittenInto()
    {
        var dumps = CrashDumps.Read(Variables(
            (CrashDumps.OnVariable, "1"),
            (CrashDumps.PathVariable, Path.Combine(Path.GetTempPath(), "modbot-dumps", "crash.%p")),
            (CrashDumps.KindVariable, "2")));

        Assert.True(dumps.On);
        Assert.Equal("2", dumps.Kind);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "modbot-dumps").TrimEnd(Path.DirectorySeparatorChar),
            dumps.Folder!.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>An operator reading the startup line should see where a dump would land.</summary>
    [Fact]
    public void TheExplanationNamesWhereADumpWouldGo()
    {
        var dumps = CrashDumps.Read(Variables(
            (CrashDumps.OnVariable, "1"),
            (CrashDumps.PathVariable, "/app/data/dumps/crash.%p")));

        Assert.Contains("/app/data/dumps/crash.%p", dumps.Explanation, StringComparison.Ordinal);
    }
}
