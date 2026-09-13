using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.FileSystem;
using Modbot.Evidence.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Evidence.Tests.Health;

/// <summary>
/// Design sections 8.3 and 8.4: the store marker's four-valued result, the latch, and the difference
/// between a store that said no and a store that said nothing.
/// </summary>
public sealed class StoreMarkerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "modbot-store-marker-tests", Guid.NewGuid().ToString("N"));

    private readonly FaultInjectingStore _store;
    private readonly FakeClock _clock = new();
    private readonly Guid _storeId = Guid.NewGuid();

    public StoreMarkerTests()
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

    private EvidenceStoreMonitor Monitor(int failuresBeforeAlarm = 3)
        => new(_store, new EvidenceOptions { StoreId = _storeId, TransientFailuresBeforeAlarm = failuresBeforeAlarm }, _clock);

    private Task CommissionAsync() =>
        _store.WriteStoreMarkerAsync(new StoreMarker(_storeId, _clock.UtcNow, "test"), Ct);

    [Fact]
    public async Task AMatchingStoreMarkerIsHealthyAndUploadsAreAllowed()
    {
        await CommissionAsync();
        var health = await Monitor().CheckAsync(Ct);

        Assert.Equal(EvidenceStoreState.Healthy, health.State);
        Assert.True(health.UploadsAllowed);
        Assert.False(health.ShouldAlarm);
    }

    /// <summary>
    /// The trap can be sprung on day one, before any evidence exists, and this is what catches it
    /// then — which is the only time it can be fixed for free.
    /// </summary>
    [Fact]
    public async Task AnAbsentStoreMarkerLatchesEvenWhenTheStoreIsEmpty()
    {
        var monitor = Monitor();
        var health = await monitor.CheckAsync(Ct);

        Assert.Equal(EvidenceStoreState.Unavailable, health.State);
        Assert.False(health.UploadsAllowed);
        Assert.True(health.ShouldAlarm);
        Assert.Single(monitor.Incidents);
    }

    [Fact]
    public async Task AStoreMarkerBelongingToADifferentStoreLatchesAndNamesBothIds()
    {
        var theirs = Guid.NewGuid();
        await _store.WriteStoreMarkerAsync(new StoreMarker(theirs, _clock.UtcNow, "somebody else"), Ct);

        var health = await Monitor().CheckAsync(Ct);

        Assert.Equal(EvidenceStoreState.Unavailable, health.State);
        Assert.Equal(_storeId, health.ExpectedStoreId);
        Assert.Equal(theirs, health.FoundStoreId);
        Assert.Contains(theirs.ToString(), health.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RubbishAtTheStoreMarkerKeyLatchesLikeAnAbsentOne()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, EvidenceKeys.StoreMarkerKey), "nonsense", Ct);

        Assert.Equal(EvidenceStoreState.Unavailable, (await Monitor().CheckAsync(Ct)).State);
    }

    /// <summary>
    /// The case that is never exercised by accident, and the one a false alarm would ruin. A
    /// banner that fires on every bucket blip teaches the operator to dismiss the banner.
    /// </summary>
    [Fact]
    public async Task AStoreThatDidNotAnswerDoesNotLatchAndDoesNotAlarmOnTheFirstFailure()
    {
        await CommissionAsync();

        var monitor = Monitor(failuresBeforeAlarm: 3);
        await monitor.CheckAsync(Ct);

        _store.ProbeOverride = StoreProbe.Unreachable("the bucket", new HttpRequestException("connection reset"));

        var first = await monitor.CheckAsync(Ct);
        Assert.Equal(EvidenceStoreState.Unreachable, first.State);
        Assert.False(first.ShouldAlarm);
        Assert.Empty(monitor.Incidents);

        var second = await monitor.CheckAsync(Ct);
        Assert.False(second.ShouldAlarm);

        var third = await monitor.CheckAsync(Ct);
        Assert.True(third.ShouldAlarm);
        Assert.Equal(3, third.ConsecutiveFailures);
    }

    /// <summary>Uploads are refused while the store's answer is unknown, but nothing is latched.</summary>
    [Fact]
    public async Task AnUnreachableStoreRefusesUploadsWithoutDeclaringLoss()
    {
        _store.ProbeOverride = StoreProbe.Unreachable("the bucket", new HttpRequestException("timeout"));

        var health = await Monitor().CheckAsync(Ct);

        Assert.False(health.UploadsAllowed);
        Assert.NotEqual(EvidenceStoreState.Unavailable, health.State);
    }

    /// <summary>
    /// Silence after a finding is not evidence that the finding was wrong.
    /// </summary>
    [Fact]
    public async Task ALatchedStoreStaysLatchedWhenItLaterStopsAnswering()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(Ct);
        Assert.Equal(EvidenceStoreState.Unavailable, monitor.Current.State);

        _store.ProbeOverride = StoreProbe.Unreachable("the bucket", new HttpRequestException("timeout"));

        Assert.Equal(EvidenceStoreState.Unavailable, (await monitor.CheckAsync(Ct)).State);
    }

    /// <summary>
    /// The latch clears when the volume is mounted correctly on the next deploy — and the incident
    /// stays on the record afterwards. A misconfiguration that fixes itself leaving no trace is how
    /// an operator concludes the warning was spurious.
    /// </summary>
    [Fact]
    public async Task TheLatchClearsOnlyWhenTheStoreMarkerReappearsAndTheIncidentSurvives()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(Ct);
        Assert.Equal(EvidenceStoreState.Unavailable, monitor.Current.State);

        _clock.Advance(TimeSpan.FromHours(2));
        await CommissionAsync();

        var health = await monitor.CheckAsync(Ct);

        Assert.Equal(EvidenceStoreState.Healthy, health.State);
        Assert.Single(monitor.Incidents);
        Assert.NotNull(monitor.Incidents[0].ResolvedAt);
    }

    /// <summary>
    /// One incident per latch, not one per probe. Repeating the alarm every fifteen minutes is the
    /// same mistake as a banner nobody reads.
    /// </summary>
    [Fact]
    public async Task RepeatedProbesOfALatchedStoreRecordOneIncident()
    {
        var monitor = Monitor();

        await monitor.CheckAsync(Ct);
        await monitor.CheckAsync(Ct);
        await monitor.CheckAsync(Ct);

        Assert.Single(monitor.Incidents);
        Assert.False(monitor.Current.ShouldAlarm);
    }

    /// <summary>
    /// Absence only means "wrong store" once there is a record of a right one. Before
    /// commissioning there is no such record, so there is nothing to conclude.
    /// </summary>
    [Fact]
    public async Task AnUncommissionedDeploymentIsNotConfiguredRatherThanBroken()
    {
        var monitor = new EvidenceStoreMonitor(_store, new EvidenceOptions(), _clock);
        var health = await monitor.CheckAsync(Ct);

        Assert.Equal(EvidenceStoreState.NotConfigured, health.State);
        Assert.False(health.UploadsAllowed);
        Assert.Empty(monitor.Incidents);
    }

    /// <summary>
    /// Sweeping while the latch is on would be deleting on the authority of a database whose
    /// relationship to the store is exactly what is in doubt.
    /// </summary>
    [Fact]
    public async Task ASweepIsOnlyAllowedWhileTheStoreIsHealthy()
    {
        var monitor = Monitor();
        await monitor.CheckAsync(Ct);
        Assert.False(monitor.Current.SweepAllowed);

        await CommissionAsync();
        await monitor.CheckAsync(Ct);
        Assert.True(monitor.Current.SweepAllowed);
    }
}
