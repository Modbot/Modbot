using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Pacing;

/// <summary>
/// Spec 4.2.1: the rates are configurable downward only, and the cap is enforced on write.
/// </summary>
/// <remarks>
/// The clamp is the whole safety property of the settings screen. Spec 4.3's failure mode is
/// asymmetric — a rate that is too low costs staler data, a rate that is too high costs an opaque
/// multi-minute penalty that retrying extends — so every one of these is a test that Modbot
/// refuses to be made faster, not that it can be made slower.
/// </remarks>
public class SyncPacingTests
{
    /// <summary>
    /// A deployment that has configured nothing runs at exactly spec 4.2's table.
    /// </summary>
    /// <remarks>
    /// This is the test that makes the whole feature safe to ship: the column is null on every
    /// existing deployment and on every new one, and a resolver that got the "nothing configured"
    /// case wrong would change the pacing of every Modbot in the world without anyone asking it
    /// to.
    /// </remarks>
    [Theory]
    [InlineData(VRChatEndpointClass.GroupsMembers, 0.5)]
    [InlineData(VRChatEndpointClass.GroupsBans, 0.5)]
    [InlineData(VRChatEndpointClass.GroupsAuditLog, 0.125)]
    // 1 per 10s, not the 1 per 8s originally guessed: measured by the maintainer on 2026-09-13
    // (research: vrchat-instance-findings.md).
    [InlineData(VRChatEndpointClass.GroupsInstances, 0.1)]
    [InlineData(VRChatEndpointClass.GroupsRead, 0.2)]
    // 3.5, not the 1.0 spec 4.2.5 first wrote: raised by the maintainer on 2026-09-13 (user
    // profile sync design §5).
    [InlineData(VRChatEndpointClass.UsersRead, 3.5)]
    [InlineData(VRChatEndpointClass.Global, 2.0)]
    public void WithNothingConfigured_TheRatesAreSpecFourTwos(string endpointClass, double expected)
    {
        Assert.Equal(expected, SyncPacing.Defaults.EffectiveRatePerSecond(endpointClass), 6);
        Assert.False(SyncPacing.Defaults.IsConfigured(endpointClass));
    }

    /// <summary>The sum spec 4.2 quotes, recomputed from the defaults rather than restated.</summary>
    [Fact]
    public void TheScheduledClassesStillSumToWhatSpecFourTwoSays()
    {
        var total = VRChatRateLimits.Scheduled.Sum(SyncPacing.Defaults.EffectiveRatePerSecond);

        // 1.425, not spec 4.2's original 1.450: groups.instances was measured at 1 per 10s rather
        // than the 1 per 8s the spec guessed, which takes 0.025 off the total.
        Assert.Equal(1.425, total, 6);
        Assert.True(total < SyncPacing.Defaults.EffectiveRatePerSecond(VRChatEndpointClass.Global));
    }

    [Fact]
    public void WithNothingConfigured_ThePollRateIsTheProducers()
    {
        Assert.Equal(AuditLogSyncOptions.PacingFloor, SyncPacing.Defaults.AuditLog.MinInterval);
        Assert.Equal(new AuditLogSyncOptions().Clamped(), SyncPacing.Defaults.AuditLog);
        Assert.Equal(new GroupInfoSyncOptions().Clamped(), SyncPacing.Defaults.GroupInfo);
    }

    [Fact]
    public void ARateCanBeLowered()
    {
        var pacing = Resolve(new() { ClassCeilingsPerSecond = Ceilings((VRChatEndpointClass.GroupsMembers, 0.2)) });

        // The stored number is the estimate of VRChat's limit; the rate is the configured share
        // of it (spec 4.3.1), so 0.2 at the default 60% is 0.12 req/s.
        Assert.Equal(0.12, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
        Assert.True(pacing.IsConfigured(VRChatEndpointClass.GroupsMembers));

        // And nothing else moved.
        Assert.Equal(0.5, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsBans), 6);
    }

    /// <summary>
    /// The cap is on the write, not on the screen (spec 4.2.1) — and the write says so.
    /// </summary>
    [Fact]
    public void ARateAboveSpecFourTwosCapIsStoredAtTheCapAndReported()
    {
        var document = new SyncPacingDocument
        {
            ClassCeilingsPerSecond = Ceilings((VRChatEndpointClass.GroupsMembers, 50.0)),
        };

        var clamped = SyncPacingJson.Clamp(document, out var adjustments);
        var pacing = SyncPacing.Resolve(clamped);

        var cap = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsMembers].HardMaxPerSecond;
        Assert.Equal(cap, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);

        var reported = Assert.Single(adjustments);
        Assert.Equal($"classCeilingsPerSecond.{VRChatEndpointClass.GroupsMembers}", reported.Field);
        Assert.Equal(50.0, reported.Requested);
        Assert.True(reported.Stored < reported.Requested);
        Assert.Contains("may only lower", reported.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every class, not just the one somebody thought to test.
    /// </summary>
    /// <remarks>
    /// Including the newer unmeasured classes (spec 4.3.4.1, 4.3.4.2), which are the ones a
    /// clamp written against spec 4.2's table would quietly miss — they are not in that table.
    /// </remarks>
    [Fact]
    public void NoClassCanBeConfiguredFasterThanItsCap()
    {
        foreach (var (name, limits) in VRChatRateLimits.Defaults)
        {
            var pacing = Resolve(new() { ClassCeilingsPerSecond = Ceilings((name, 1_000.0)) });

            Assert.True(
                pacing.EffectiveRatePerSecond(name) <= limits.HardMaxPerSecond + 1e-9,
                $"{name} was configurable above spec 4.2's cap of {limits.HardMaxPerSecond} req/s.");
        }
    }

    /// <summary>
    /// A raised budget fraction re-clamps the ceilings it would otherwise have lifted.
    /// </summary>
    /// <remarks>
    /// The two knobs multiply, so a ceiling that was harmless at 60% of the estimate is not
    /// harmless at 100% of it. Clamping the whole document against the fraction it is stored with
    /// is what keeps "the stored number is the number that runs" true after either one moves.
    /// </remarks>
    [Fact]
    public void RaisingTheFractionDoesNotLiftARateAboveItsCap()
    {
        var document = new SyncPacingDocument
        {
            BudgetFraction = 1.0,
            ClassCeilingsPerSecond = Ceilings(
                (VRChatEndpointClass.GroupsMembers,
                 VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsMembers].DefaultCeilingPerSecond)),
        };

        var pacing = SyncPacing.Resolve(SyncPacingJson.Clamp(document, out _));

        Assert.Equal(0.5, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
    }

    [Fact]
    public void LoweringTheFractionLowersEveryClass()
    {
        var pacing = Resolve(new() { BudgetFraction = 0.3 });

        Assert.Equal(0.25, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
        Assert.Equal(0.0625, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsAuditLog), 6);
    }

    /// <summary>
    /// Zero is not "gentler", it is a bucket that never issues again.
    /// </summary>
    /// <remarks>
    /// <c>TokenBucket.TimeUntilToken</c> returns <c>TimeSpan.MaxValue</c> at a rate of zero, so a
    /// stored zero would stop the sync with no cold stop, no incident and nothing on the health
    /// screen to explain it. The API rejects it outright; this is the second line, for a value
    /// that arrived some other way.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ARateOfZeroOrLessFallsBackToTheDefault(double value)
    {
        var document = new SyncPacingDocument
        {
            ClassCeilingsPerSecond = Ceilings((VRChatEndpointClass.GroupsMembers, value)),
        };

        var clamped = SyncPacingJson.Clamp(document, out var adjustments);
        var pacing = SyncPacing.Resolve(clamped);

        Assert.Equal(0.5, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
        Assert.False(pacing.IsConfigured(VRChatEndpointClass.GroupsMembers));
        Assert.Single(adjustments);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void ABudgetFractionOfZeroOrLessFallsBackToSpecFourThreeOnes(double value)
    {
        var pacing = Resolve(new() { BudgetFraction = value });

        Assert.Equal(RateLimitOptions.DefaultFraction, pacing.BudgetFraction);
    }

    /// <summary>Spec 4.3.4: a class with no budget is a question to ask, not a rate to invent.</summary>
    [Fact]
    public void ARateForAClassModbotDoesNotBudgetIsDiscarded()
    {
        var document = new SyncPacingDocument
        {
            ClassCeilingsPerSecond = Ceilings(("groups.somethingNobodyAskedAbout", 0.1)),
        };

        var clamped = SyncPacingJson.Clamp(document, out var adjustments);

        Assert.Null(clamped.ClassCeilingsPerSecond);
        Assert.Contains("not an endpoint class", Assert.Single(adjustments).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntervalBelowThePacingFloorIsRaisedToIt()
    {
        var clamped = SyncPacingJson.Clamp(
            new SyncPacingDocument
            {
                AuditLogMinIntervalSeconds = 0.25,
                GroupInfoIntervalSeconds = 1,
            },
            out var adjustments);

        var pacing = SyncPacing.Resolve(clamped);

        Assert.Equal(AuditLogSyncOptions.PacingFloor, pacing.AuditLog.MinInterval);
        Assert.Equal(GroupInfoSyncOptions.PacingFloor, pacing.GroupInfo.Interval);
        Assert.Equal(2, adjustments.Count);
    }

    [Fact]
    public void AnIntervalCanAlwaysBeRaised()
    {
        var pacing = Resolve(new()
        {
            AuditLogMinIntervalSeconds = 60,
            AuditLogMaxIntervalSeconds = 3600,
            GroupInfoIntervalSeconds = 86_400,
        });

        Assert.Equal(TimeSpan.FromMinutes(1), pacing.AuditLog.MinInterval);
        Assert.Equal(TimeSpan.FromHours(1), pacing.AuditLog.MaxInterval);
        Assert.Equal(TimeSpan.FromDays(1), pacing.GroupInfo.Interval);
    }

    [Fact]
    public void TheCatchUpCanBeTurnedOff()
    {
        Assert.True(SyncPacing.Defaults.AuditLog.CatchUp);
        Assert.False(Resolve(new() { AuditLogCatchUp = false }).AuditLog.CatchUp);
    }

    /// <summary>
    /// A host that composed Modbot with its own poll rate keeps it where the operator has not
    /// overruled it.
    /// </summary>
    [Fact]
    public void AnUnconfiguredFieldFallsBackToTheHostsBaselineRatherThanTheCompiledDefault()
    {
        var baseline = new SyncPacingBaseline(
            new AuditLogSyncOptions { MaxInterval = TimeSpan.FromHours(2) }.Clamped(),
            new GroupInfoSyncOptions { Interval = TimeSpan.FromHours(6) }.Clamped());

        var pacing = SyncPacing.Resolve(
            new SyncPacingDocument { GroupInfoIntervalSeconds = 600 }, classes: null, baseline);

        Assert.Equal(TimeSpan.FromHours(2), pacing.AuditLog.MaxInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), pacing.GroupInfo.Interval);
    }

    // ── The column itself ───────────────────────────────────────────────────────────────────

    [Fact]
    public void NothingConfiguredIsStoredAsNullRatherThanAnEmptyObject()
    {
        Assert.Null(SyncPacingJson.Write(SyncPacingDocument.Empty));
        Assert.Null(SyncPacingJson.Write(null));
    }

    /// <summary>
    /// Only what was changed is written, so an untouched slider keeps tracking the spec.
    /// </summary>
    [Fact]
    public void OnlyConfiguredFieldsAreStored()
    {
        var json = SyncPacingJson.Write(new SyncPacingDocument { AuditLogCatchUp = false });

        Assert.NotNull(json);
        // The stored key is pinned to its original spelling (see SyncPacingDocument), because
        // this JSON lives in the settings table and existing rows must still be read.
        Assert.Contains("auditLogCatchUp", json, StringComparison.Ordinal);
        Assert.DoesNotContain("groupInfoIntervalSeconds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("budgetFraction", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentRoundTripsThroughTheColumn()
    {
        var document = new SyncPacingDocument
        {
            BudgetFraction = 0.4,
            ClassCeilingsPerSecond = Ceilings((VRChatEndpointClass.GroupsBans, 0.3)),
            AuditLogMaxIntervalSeconds = 900,
            AuditLogPageSize = 40,
            AuditLogCatchUp = false,
            GroupInfoIntervalSeconds = 600,
        };

        var json = SyncPacingJson.Write(document);
        var read = SyncPacingJson.Read(json);

        // Compared through the serialiser rather than with record equality: the ceilings are an
        // IReadOnlyDictionary, and a record compares that by reference.
        Assert.Equal(json, SyncPacingJson.Write(read));
        Assert.Equal(0.4, read.BudgetFraction);
        Assert.Equal(0.3, read.ClassCeilingsPerSecond![VRChatEndpointClass.GroupsBans]);
        Assert.Equal(40, read.AuditLogPageSize);
        Assert.False(read.AuditLogCatchUp);
    }

    /// <summary>
    /// An unreadable column degrades to spec 4.2's defaults rather than stopping the producers.
    /// </summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void AnUnreadableColumnResolvesToTheDefaults(string json)
    {
        Assert.True(SyncPacingJson.Read(json).IsEmpty);
    }

    [Fact]
    public void AFieldThisBuildDoesNotKnowIsIgnoredRatherThanFatal()
    {
        var document = SyncPacingJson.Read(
            """{"budgetFraction":0.5,"somethingFromALaterRelease":{"nested":true}}""");

        Assert.Equal(0.5, document.BudgetFraction);
    }

    [Fact]
    public void AChangeOverlaysRatherThanReplaces()
    {
        var stored = new SyncPacingDocument
        {
            BudgetFraction = 0.4,
            ClassCeilingsPerSecond = Ceilings((VRChatEndpointClass.GroupsBans, 0.3)),
            AuditLogCatchUp = false,
        };

        var merged = stored.MergedWith(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = Ceilings((VRChatEndpointClass.GroupsMembers, 0.2)),
        });

        Assert.Equal(0.4, merged.BudgetFraction);
        Assert.False(merged.AuditLogCatchUp);
        Assert.Equal(2, merged.ClassCeilingsPerSecond!.Count);
        Assert.Equal(0.3, merged.ClassCeilingsPerSecond[VRChatEndpointClass.GroupsBans]);
    }

    private static SyncPacing Resolve(SyncPacingDocument document) =>
        SyncPacing.Resolve(SyncPacingJson.Clamp(document, out _));

    private static Dictionary<string, double> Ceilings(params (string Name, double Value)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
}
