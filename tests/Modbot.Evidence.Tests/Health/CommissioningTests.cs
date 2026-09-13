using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.FileSystem;
using Modbot.Evidence.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Evidence.Tests.Health;

/// <summary>
/// Design section 8.5: a backend that cannot pass a full round trip cannot be selected, and the
/// error names the step that failed rather than saying "storage error".
/// </summary>
public sealed class CommissioningTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "modbot-commission-tests", Guid.NewGuid().ToString("N"));

    private readonly FaultInjectingStore _store;
    private readonly EvidenceStoreCommissioner _commissioner = new(new FakeClock());

    public CommissioningTests()
    {
        Directory.CreateDirectory(_root);
        _store = new FaultInjectingStore(new FilesystemEvidenceStore(new FilesystemEvidenceOptions { Root = _root }));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AGoodStorePassesAndEndsUpCarryingTheSentinel()
    {
        var id = Guid.NewGuid();
        var result = await _commissioner.CommissionAsync(_store, id, "home server", Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(id, result.StoreId);

        var probe = await _store.ProbeAsync(Ct);
        Assert.Equal(StoreProbeOutcome.Present, probe.Outcome);
        Assert.Equal(id, probe.Sentinel!.StoreId);
    }

    /// <summary>
    /// The operator is making a decision at this moment, so this is where the durability
    /// properties get stated: no backups unless they arranged them, and no undo on a delete.
    /// </summary>
    [Fact]
    public async Task SuccessSaysPlainlyThatTheDataIsTheirsToLookAfter()
    {
        var result = await _commissioner.CommissionAsync(_store, Guid.NewGuid(), null, Ct);

        Assert.Contains("no backups", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be undone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStoreThatCannotBeWrittenToFailsAtTheWriteStep()
    {
        _store.Fault = new IOException("permission denied");

        var result = await _commissioner.CommissionAsync(_store, Guid.NewGuid(), null, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("write", result.FailedStep);
    }

    /// <summary>
    /// The round trip exists to catch a store that accepts a write and then cannot produce the
    /// object — the failure that would otherwise be found at the first real upload.
    /// </summary>
    [Fact]
    public async Task AStoreThatLosesTheObjectFailsRatherThanBeingSelectable()
    {
        var result = await _commissioner.CommissionAsync(_store, Guid.NewGuid(), null, Ct);
        Assert.True(result.Succeeded);

        _store.PretendObjectsAreMissing = true;

        var second = await _commissioner.CommissionAsync(_store, Guid.NewGuid(), null, Ct);

        Assert.False(second.Succeeded);
        Assert.Equal("commit", second.FailedStep);
    }

    /// <summary>Nothing of Modbot's is left in the store except the sentinel.</summary>
    [Fact]
    public async Task TheCanaryIsCleanedUpAfterwards()
    {
        await _commissioner.CommissionAsync(_store, Guid.NewGuid(), null, Ct);

        var left = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .ToList();

        Assert.Equal([EvidenceKeys.SentinelKey], left);
    }
}
