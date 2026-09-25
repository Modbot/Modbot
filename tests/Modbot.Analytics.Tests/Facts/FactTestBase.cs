using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// Shared setup for the fact-log tests: one partition range covering the instant every test
/// writes at, and constructors for the writer and the records it takes.
/// </summary>
public abstract class FactTestBase : IAsyncLifetime
{
    /// <summary>
    /// A fixed instant every fact test writes around. Fixed rather than "now" because the log is
    /// partitioned by month, and a suite that silently depends on today's date starts failing on
    /// the first of a month for reasons nobody will connect to this file.
    /// </summary>
    protected static readonly DateTimeOffset Now = new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

    protected FactTestBase(PostgresFixture db) => Db = db;

    protected PostgresFixture Db { get; }

    public async ValueTask InitializeAsync()
    {
        await using var context = Db.NewContext();
        var maintainer = new EventPartitionMaintainer(context, new FakeClock(Now));
        await maintainer.EnsureAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected static string UniqueId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    protected static IFactWriter NewWriter(ModbotContext context, IModbotClock clock)
        => new FactWriter(context, clock);

    /// <summary>A client-reported instance join -- the shape deduplication exists for.</summary>
    /// <param name="device">
    /// Which client reported it, as the companion ingest writes it. Left off where the test is
    /// about the fact rather than about who saw it.
    /// </param>
    protected static FactRecord Presence(
        string subjectId,
        DateTimeOffset occurredAt,
        string instanceId = "instance-1",
        string type = FactType.InstanceJoined,
        Guid? device = null)
        => new()
        {
            Type = type,
            OccurredAt = occurredAt,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subjectId,
            WorldId = "wrld_test",
            InstanceId = instanceId,
            Source = FactSource.Companion,
            Data = device is { } reporter
                ? new JsonObject { [ClientReport.DeviceIdKey] = reporter.ToString() }
                : null,
        };
}
