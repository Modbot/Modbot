namespace Modbot.Core.Logging.Store;

/// <summary>What the log store has been doing, for the Health page.</summary>
/// <param name="Storing">The sink is connected to the database and writing.</param>
/// <param name="Written">Lines written since this Modbot started.</param>
/// <param name="Dropped">Lines thrown away because the queue was full.</param>
/// <param name="LastWriteAt">When the last batch was written.</param>
/// <param name="LastError">Why the last batch failed, if one did. Null once a batch succeeds.</param>
/// <param name="LastErrorAt">When that was.</param>
/// <param name="Waiting">Lines in the queue right now.</param>
public sealed record LogStoreStatus(
    bool Storing,
    long Written,
    long Dropped,
    DateTimeOffset? LastWriteAt,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    int Waiting);
