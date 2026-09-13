using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// VRChat types <c>eventType</c> as a bare string with one example, so Modbot's mapping table is
/// a record of observations rather than a contract. This check turns it into something verifiable
/// against a live group.
/// </summary>
public class AuditLogVocabularyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 4, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The failure that motivated the whole check: a mapping Modbot depends on that VRChat does
    /// not declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is invisible to the unmapped-event reporting, and that is the point. Unmapped events
    /// are reported when an event <em>arrives</em> with a type Modbot does not know. A
    /// <em>misspelled</em> mapping never produces one: Modbot sits waiting for
    /// <c>group.member.user.ban</c> while VRChat emits something else entirely, no unknown type is
    /// ever seen, and the fact log looks perfectly healthy while containing no bans at all.
    /// </para>
    /// <para>
    /// Only asking VRChat what it actually declares can catch it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMappingVRChatDoesNotDeclareIsReportedAsAProblem()
    {
        // Everything Modbot maps as real, except bans.
        var declared = GroupAuditLogEvents.Primary
            .Where(t => t != GroupAuditLogEvents.MemberBan)
            .ToArray();

        var report = AuditLogVocabulary.Compare(declared, Now);

        Assert.True(report.HasProblem);
        Assert.Contains(GroupAuditLogEvents.MemberBan, report.MissingPrimary);
    }

    /// <summary>
    /// An alias VRChat does not declare is the expected case and must not read as a fault.
    /// </summary>
    /// <remarks>
    /// The aliases are deliberate insurance against a spelling VRChat has used before or might use
    /// again. If every one of them raised a problem, the check would cry wolf on every healthy
    /// deployment and be ignored within a week — which is how a real warning gets missed.
    /// </remarks>
    [Fact]
    public void UnusedAliasesAreNotAProblem()
    {
        var report = AuditLogVocabulary.Compare(GroupAuditLogEvents.Primary, Now);

        Assert.False(report.HasProblem);
        Assert.Empty(report.MissingPrimary);
        Assert.NotEmpty(report.UnusedAliases);
    }

    /// <summary>Types VRChat declares that Modbot does not record are listed, not hidden.</summary>
    /// <remarks>
    /// A known gap rather than a fault — instance kicks and warns, group posts and requests are
    /// all real events Modbot has chosen not to record. Listing them is how that choice stays a
    /// choice instead of drifting into an oversight nobody remembers making.
    /// </remarks>
    [Fact]
    public void TypesVRChatDeclaresButModbotIgnoresAreListed()
    {
        string[] declared =
        [
            .. GroupAuditLogEvents.Primary,
            "group.instance.kick",
            "group.post.create",
        ];

        var report = AuditLogVocabulary.Compare(declared, Now);

        Assert.False(report.HasProblem);
        Assert.Equal(["group.instance.kick", "group.post.create"], report.Unmapped);
    }

    /// <summary>
    /// VRChat's casing is not something to depend on, so neither side of the comparison assumes
    /// it.
    /// </summary>
    [Fact]
    public void ComparisonIgnoresCaseAndSurroundingWhitespace()
    {
        var declared = GroupAuditLogEvents.Primary
            .Select(t => $"  {t.ToUpperInvariant()}  ")
            .ToArray();

        var report = AuditLogVocabulary.Compare(declared, Now);

        Assert.False(report.HasProblem);
        Assert.Empty(report.Unmapped);
    }

    /// <summary>
    /// An empty or nonsense answer must not be read as "every mapping is wrong".
    /// </summary>
    /// <remarks>
    /// It would be, literally — nothing is declared, so every primary is missing — but the useful
    /// reading is that the check learned nothing. The caller discards a null result and this test
    /// pins the shape that makes that distinguishable: a real answer lists what it found.
    /// </remarks>
    [Fact]
    public void AnEmptyAnswerReportsEverythingMissingRatherThanSilentlyPassing()
    {
        var report = AuditLogVocabulary.Compare([], Now);

        Assert.Empty(report.Declared);
        Assert.True(report.HasProblem);
        Assert.Equal(GroupAuditLogEvents.Primary.Count, report.MissingPrimary.Count);
    }

    [Fact]
    public void BlankEntriesAreIgnored()
    {
        string[] declared = [.. GroupAuditLogEvents.Primary, "", "   ", null!];

        var report = AuditLogVocabulary.Compare(declared.Where(x => x is not null), Now);

        Assert.Empty(report.Unmapped);
        Assert.False(report.HasProblem);
    }

    /// <summary>
    /// The check runs on its own endpoint class, so a 429 while verifying cannot cold-stop the
    /// audit log it was verifying on behalf of.
    /// </summary>
    /// <remarks>
    /// Spec 4.3.4.1. A diagnostic that cannot run is an inconvenience; a moderation history that
    /// stops arriving because a diagnostic was refused would be a defect, and sharing a class is
    /// all it would take.
    /// </remarks>
    [Fact]
    public void TheCheckIsIsolatedFromTheAuditLogItChecks()
    {
        var types = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsAuditLogTypes];
        var auditLog = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsAuditLog];

        Assert.NotEqual(auditLog.Lane, types.Lane);
        Assert.NotEqual(VRChatEndpointClass.GroupsAuditLog, VRChatEndpointClass.GroupsAuditLogTypes);

        // Unmeasured, so no faster than the most conservative class it could resemble.
        Assert.True(
            types.HardMaxPerSecond
            <= VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRead].HardMaxPerSecond);
    }
}
