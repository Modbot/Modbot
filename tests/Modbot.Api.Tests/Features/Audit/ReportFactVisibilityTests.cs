using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// The two member-report facts (<c>modbot.report.open</c> and <c>modbot.report.close</c>) are in the
/// operational log, so nobody sees them by holding See the audit log or See reports alone: not in a
/// person's timeline, not in the audit log, not on the live feed or the live stream (Discord
/// commands design §3.4).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ReportFactVisibilityTests
{
    private const string Reported = "200000000000000002";

    private static readonly string[] ReportTypes = [FactType.MemberReportOpened, FactType.MemberReportClosed];

    /// <summary>What a moderator holds, with See reports and Handle reports as well.</summary>
    private const ModbotPermissions Moderator = ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewProfile
        | ModbotPermissions.ViewReports | ModbotPermissions.HandleReports | ModbotPermissions.ReviewTickets;

    private readonly PostgresFixture _db;

    public ReportFactVisibilityTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheReportFactsAreOperational_SoOnlyTheOperationalLogSeesThem()
    {
        foreach (var type in ReportTypes)
        {
            Assert.Equal(AuditCategory.Operational, AuditVisibility.CategoryOf(type));
            Assert.False(AuditVisibility.CanSeeType(Moderator, type));
            Assert.DoesNotContain(type, AuditVisibility.VisibleTypes(Moderator));
            Assert.True(AuditVisibility.CanSeeType(ModbotPermissions.ViewOperationalLog, type));
            Assert.True(AuditVisibility.CanSeeType(ModbotPermissions.Administrator, type));
        }
    }

    [Fact]
    public void TheLiveFeed_AndTheLiveStream_FollowTheSameGate()
    {
        foreach (var type in ReportTypes)
        {
            // Events API, webhooks and companion-less feeds.
            Assert.False(EventVisibility.CanSee(Moderator, type));
            Assert.True(EventVisibility.CanSee(Moderator | ModbotPermissions.ViewOperationalLog, type));

            // The live stream: no kind of its own, so the audit log's rules alone decide.
            var scope = LiveScope.ForPerson(Moderator);
            Assert.False(scope.CanSee("fact", type));
            Assert.True(LiveScope.ForPerson(ModbotPermissions.ViewOperationalLog).CanSee("fact", type));

            // A companion on a headset is sent presence and nothing else.
            Assert.False(LiveScope.ForDevice(Guid.NewGuid(), "12345").CanSee("fact", type));
        }
    }

    [Fact]
    public async Task APersonsTimelineAndTheAuditLog_LeaveTheReportFactsOut_ForSomebodyWithoutTheOperationalLog()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        foreach (var type in ReportTypes)
        {
            await host.WriteFactAsync(
                new FactRecord
                {
                    Type = type,
                    OccurredAt = host.Clock.UtcNow,
                    SubjectPlatform = FactPlatform.Discord,
                    SubjectId = Reported,
                    Source = FactSource.Modbot,
                    Data = new JsonObject { ["reportId"] = Guid.NewGuid().ToString() },
                },
                Ct);
        }

        var moderator = await host.SignedInAsync(Moderator, Ct);
        var operational = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog | ModbotPermissions.ViewProfile, Ct);

        foreach (var path in new[]
                 {
                     $"/api/audit?person={Reported}&personPlatform=Discord",
                     "/api/audit",
                 })
        {
            var seen = await host.GetJsonAsync<AuditPage>(path, moderator, Ct);
            Assert.DoesNotContain(seen.Entries, e => ReportTypes.Contains(e.Type));
        }

        // Asking for the types by name does not get past the gate either.
        var asked = await host.GetJsonAsync<AuditPage>(
            $"/api/audit?person={Reported}&personPlatform=Discord&type={FactType.MemberReportOpened}", moderator, Ct);
        Assert.Empty(asked.Entries);

        // The operational log is where they are.
        var inTheLog = await host.GetJsonAsync<AuditPage>(
            $"/api/audit?person={Reported}&personPlatform=Discord", operational, Ct);
        Assert.Equal(2, inTheLog.Entries.Count(e => ReportTypes.Contains(e.Type)));
    }
}
