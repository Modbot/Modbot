using Modbot.Companion.CloudBackup;
using Modbot.Companion.Startup;

namespace Modbot.Companion.Tests.Startup;

/// <summary>
/// A test copy makes no request of its own: Modbot Cloud is off whatever the settings and the
/// environment say, and nothing is downloaded. The real copy is unchanged.
/// </summary>
public class TestCopySendsNothingTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "modbot-test-copy-tests", "TestCopy");

    private static readonly DataFolder TestCopy = new(Folder, IsTestCopy: true);

    private static readonly DataFolder Real = new(Path.Combine(Path.GetTempPath(), "modbot-test-copy-tests", "Modbot"), IsTestCopy: false);

    [Fact]
    public void ATestCopysCloudSettingsResolveToDisabled()
    {
        // On in the file, on in the environment, pointed somewhere: still off in a test copy.
        var read = CloudSettings.Resolve(
            "https://cloud.example.org",
            fileDisabled: false,
            name => name == CloudSettings.DisabledVariable ? "0" : null);

        Assert.False(read.Disabled);
        Assert.True(TestCopy.CloudFor(read).Disabled);
        Assert.True(TestCopy.CloudFor(CloudSettings.Default).Disabled);
        Assert.False(TestCopy.MayUseCloud);
    }

    [Fact]
    public void TheRealCopyKeepsWhatItRead()
    {
        Assert.Same(CloudSettings.Default, Real.CloudFor(CloudSettings.Default));
        Assert.True(Real.MayUseCloud);
        Assert.True(Real.MayDownload);
    }

    [Fact]
    public void ATestCopyDownloadsNothing()
    {
        Assert.False(TestCopy.MayDownload);
    }
}
