using Modbot.VRChat.Pacing;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A pacing source a test moves by hand, standing in for the settings row.
/// </summary>
/// <remarks>
/// The provider's own reading and caching are tested against real PostgreSQL, because the thing
/// worth proving there is that the column round-trips. What every other test needs is the ability
/// to say "the operator has just lowered this rate" and see what the limiter or the producer does
/// next, which is this.
/// </remarks>
public sealed class FakeSyncPacing : ISyncPacingSource
{
    private SyncPacing _snapshot = SyncPacing.Defaults;
    private long _version;

    public SyncPacing Snapshot => _snapshot;

    public long Version => _version;

    /// <summary>How many times a consumer asked. A limiter that asked per call would show here.</summary>
    public int Reads { get; private set; }

    public ValueTask<SyncPacing> CurrentAsync(CancellationToken ct = default)
    {
        Reads++;
        return ValueTask.FromResult(_snapshot);
    }

    public void Publish(string? json) => Set(SyncPacingJson.Read(json));

    /// <summary>The operator moved a slider.</summary>
    public void Set(SyncPacingDocument document)
    {
        _snapshot = SyncPacing.Resolve(document);
        _version++;
    }
}
