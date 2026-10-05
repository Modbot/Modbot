using Modbot.Companion.Startup;
using Modbot.Companion.TestRemote;

namespace Modbot.Companion.Tests.TestRemote;

/// <summary>
/// The test remote exists only in a test copy that is also in debug mode, and its pipe is named
/// after the test copy's folder.
/// </summary>
public class TestRemoteSwitchTests
{
    private static readonly string AppData = Path.Combine(Path.GetTempPath(), "modbot-remote-tests", "Roaming");

    private static readonly string TestFolder = Path.Combine(Path.GetTempPath(), "modbot-remote-tests", "TestCopy");

    private static Func<string, string?> Env(string? folder, string? debug)
        => name => name switch
        {
            DataFolder.Variable => folder,
            TestRemoteSwitch.DebugModeVariable => debug,
            _ => null,
        };

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "1")]
    [InlineData("test", null)]
    [InlineData("test", "0")]
    [InlineData("test", "")]
    [InlineData("test", "yes")]
    public void OffUnlessBothAreSet(string? folder, string? debug)
    {
        var pipe = TestRemoteSwitch.PipeName(Env(folder is null ? null : TestFolder, debug), AppData);

        Assert.Null(pipe);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void OnWithATestCopyInDebugMode(string debug)
    {
        var pipe = TestRemoteSwitch.PipeName(Env(TestFolder, debug), AppData);

        Assert.NotNull(pipe);
        Assert.StartsWith(TestRemoteSwitch.PipePrefix, pipe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealCopyNeverHasOneEvenInDebugMode()
    {
        var real = new DataFolder(DataFolder.Real(AppData), IsTestCopy: false);

        Assert.Null(real.RemotePipeName);
        Assert.Null(TestRemoteSwitch.PipeName(real, debugMode: true));
        Assert.Null(TestRemoteSwitch.PipeName(Env(DataFolder.Real(AppData), "1"), AppData));
    }

    [Fact]
    public void ThePipeIsNamedAfterTheFolder()
    {
        var one = TestRemoteSwitch.PipeName(Env(Path.Combine(AppData, "..", "One"), "1"), AppData);
        var oneAgain = TestRemoteSwitch.PipeName(Env(Path.Combine(AppData, "..", "One") + Path.DirectorySeparatorChar, "1"), AppData);
        var two = TestRemoteSwitch.PipeName(Env(Path.Combine(AppData, "..", "Two"), "1"), AppData);

        Assert.Equal(one, oneAgain);
        Assert.NotEqual(one, two);

        // Its own name: not the one-copy lock's, and not the pipe a second copy hands a link through.
        var folder = DataFolder.Choose(Env(Path.Combine(AppData, "..", "One"), "1"), AppData).Folder!;
        Assert.NotEqual(folder.PipeName, one);
        Assert.NotEqual(folder.SingleInstanceName, one);
    }

    [Fact]
    public void TheToolWorksOutTheSameNameFromTheFolder()
    {
        var (pipe, refusal) = TestRemoteSwitch.PipeForFolder(TestFolder, AppData);

        Assert.Null(refusal);
        Assert.Equal(TestRemoteSwitch.PipeName(Env(TestFolder, "1"), AppData), pipe);
    }

    [Fact]
    public void TheToolRefusesTheRealFolder()
    {
        var (pipe, refusal) = TestRemoteSwitch.PipeForFolder(DataFolder.Real(AppData), AppData);

        Assert.Null(pipe);
        Assert.NotNull(refusal);
    }
}
