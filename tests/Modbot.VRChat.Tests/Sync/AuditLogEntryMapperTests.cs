using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The audit log is the authoritative moderation source (spec 5.9), so what it means is decided
/// here and nowhere else.
/// </summary>
public class AuditLogEntryMapperTests
{
    private static readonly DateTime When = new(2026, 6, 15, 14, 32, 7, DateTimeKind.Utc);

    [Theory]
    [InlineData("group.member.join", FactType.MemberJoined)]
    [InlineData("group.member.leave", FactType.MemberLeft)]
    [InlineData("group.member.remove", FactType.MemberKicked)]
    [InlineData("group.member.user.ban", FactType.MemberBanned)]
    [InlineData("group.member.user.unban", FactType.MemberUnbanned)]
    [InlineData("group.member.role.assign", FactType.RoleGranted)]
    [InlineData("group.member.role.unassign", FactType.RoleRevoked)]
    [InlineData("group.invite.create", FactType.InviteCreated)]
    public void EachKnownEventTypeBecomesItsFactType(string eventType, FactType expected)
    {
        var mapping = AuditLogEntryMapper.Map(Entry(eventType));

        Assert.True(mapping.Mapped);
        Assert.Equal(expected, mapping.Fact!.Type);
    }

    /// <summary>
    /// Spec 5.8's accountability is built entirely on this. A sync diff can see that someone is
    /// gone and can never see who removed them; this is the only source that can, and a fact that
    /// dropped the actor would make the audit log no better than the inference.
    /// </summary>
    [Fact]
    public void TheActorIsCarriedOntoTheFactSeparatelyFromTheSubject()
    {
        var mapping = AuditLogEntryMapper.Map(Entry("group.member.user.ban"));
        var fact = mapping.Fact!;

        Assert.Equal("usr_target", fact.SubjectId);
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal("usr_actor", fact.ActorId);
        Assert.Equal(FactPlatform.VRChat, fact.ActorPlatform);
    }

    [Fact]
    public void AnEntryWithNoActorRecordsNoActorRatherThanAnEmptyOne()
    {
        var entry = Entry("group.member.join");
        entry.ActorId = null!;

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Null(fact.ActorId);
        Assert.Null(fact.ActorPlatform);
    }

    /// <summary>
    /// Spec 5.3: the audit log states when a thing happened, so there is no window. Adding one
    /// would tell every downstream chart to treat an exact ban as an estimate.
    /// </summary>
    [Fact]
    public void TheTimeIsExactAndTheSourceSaysSo()
    {
        var fact = AuditLogEntryMapper.Map(Entry("group.member.user.ban")).Fact!;

        Assert.Equal(new DateTimeOffset(When), fact.OccurredAt);
        Assert.Null(fact.OccurredBefore);
        Assert.Equal(FactSource.AuditLog, fact.Source);
    }

    /// <summary>
    /// The SDK deserialises through Newtonsoft, whose <c>DateTimeKind</c> depends on how the
    /// string was written. Reading an unspecified kind as local time would make every fact's
    /// timestamp depend on the host's timezone -- plausible-looking, wrong, and invisible, which
    /// is the class of bug spec 4.4 exists to rule out.
    /// </summary>
    [Fact]
    public void AnUnspecifiedTimestampIsReadAsUtcRatherThanAsLocalTime()
    {
        var entry = Entry("group.member.join");
        entry.CreatedAt = new DateTime(2026, 6, 15, 14, 32, 7, DateTimeKind.Unspecified);

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(TimeSpan.Zero, fact.OccurredAt.Offset);
        Assert.Equal(new DateTime(2026, 6, 15, 14, 32, 7, DateTimeKind.Utc), fact.OccurredAt.UtcDateTime);
    }

    /// <summary>
    /// The idempotency key. A poll deliberately re-reads a window (spec 4.2.4's cursor semantics),
    /// so without VRChat's own entry id on the fact there is no way to recognise work already
    /// done, and every overlap would duplicate the log.
    /// </summary>
    [Fact]
    public void TheEntryIdIsCarriedIntoThePayload()
    {
        var mapping = AuditLogEntryMapper.Map(Entry("group.member.user.ban"));

        Assert.Equal("gaud_1", mapping.EntryId);
        Assert.Equal("gaud_1", mapping.Fact!.Data!["auditEntryId"]!.GetValue<string>());
        Assert.Equal("group.member.user.ban", mapping.Fact.Data["eventType"]!.GetValue<string>());
        Assert.Equal("grp_test", mapping.Fact.Data["groupId"]!.GetValue<string>());
    }

    /// <summary>
    /// VRChat documents this field only as "dependent on the event type", so it is stored and not
    /// interpreted. A role grant's role id lives in here, and guessing at its shape is how it ends
    /// up in the column meant for a user id.
    /// </summary>
    [Fact]
    public void VRChatsOwnPayloadIsCarriedThroughVerbatim()
    {
        var entry = Entry("group.member.role.assign");
        entry.Data = """{"roleId":"grol_9","roleName":"Moderator"}""";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("grol_9", fact.Data!["auditData"]!["roleId"]!.GetValue<string>());
        Assert.Equal("Moderator", fact.Data["auditData"]!["roleName"]!.GetValue<string>());
    }

    [Fact]
    public void APayloadThatIsNotJsonIsKeptAsTextRatherThanDiscarded()
    {
        var entry = Entry("group.member.join");
        entry.Data = "not json at all";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("not json at all", fact.Data!["auditData"]!.GetValue<string>());
    }

    /// <summary>
    /// The failure this producer must never have is a fact log that looks complete and is not.
    /// VRChat types <c>eventType</c> as a free-form string, so an unrecognised value is either a
    /// feature Modbot has not catalogued or a name spelled wrong in
    /// <see cref="GroupAuditLogEvents"/> -- and both have to surface.
    /// </summary>
    [Fact]
    public void AnUnrecognisedEventTypeIsReportedRatherThanMappedToSomethingPlausible()
    {
        var mapping = AuditLogEntryMapper.Map(Entry("group.post.create"));

        Assert.False(mapping.Mapped);
        Assert.Equal(AuditLogRejection.UnknownEventType, mapping.Rejection);
        Assert.Equal("group.post.create", mapping.EventType);

        // Still identified, so the diagnostics can point an operator at the real entry in VRChat.
        Assert.Equal("gaud_1", mapping.EntryId);
    }

    /// <summary>
    /// Instance moderation is the gap this documents: VRChat records ejecting somebody from an
    /// instance separately from removing them from the group, and Modbot has a fact type for the
    /// second only. Folding the first into <see cref="FactType.MemberKicked"/> would blend two
    /// different actions into one count, so it is reported instead.
    /// </summary>
    [Theory]
    [InlineData("group.instance.kick")]
    [InlineData("group.instance.warn")]
    public void InstanceLevelModerationHasNoFactTypeAndSaysSo(string eventType)
        => Assert.Equal(AuditLogRejection.UnknownEventType, AuditLogEntryMapper.Map(Entry(eventType)).Rejection);

    [Fact]
    public void AnEntryWithNoTargetCannotBecomeAFact()
    {
        var entry = Entry("group.member.user.ban");
        entry.TargetId = null!;

        Assert.Equal(AuditLogRejection.MissingTarget, AuditLogEntryMapper.Map(entry).Rejection);
    }

    /// <summary>
    /// The fact log is partitioned by <c>occurred_at</c>, so a fact with no time has nowhere to
    /// go. Better to report the entry than to invent "now" for something that happened earlier.
    /// </summary>
    [Fact]
    public void AnEntryWithNoTimestampCannotBecomeAFact()
    {
        var entry = Entry("group.member.user.ban");
        entry.CreatedAt = default;

        Assert.Equal(AuditLogRejection.MissingTimestamp, AuditLogEntryMapper.Map(entry).Rejection);
    }

    /// <summary>
    /// Every declared event type maps. A constant added to <see cref="GroupAuditLogEvents"/> and
    /// forgotten in its table would otherwise be reported as unknown forever, which is the one
    /// failure the loud path cannot distinguish from a genuine discovery.
    /// </summary>
    [Fact]
    public void EveryDeclaredEventTypeHasAFactType()
        => Assert.All(
            GroupAuditLogEvents.Known,
            type => Assert.True(GroupAuditLogEvents.TryMap(type, out _), type));

    private static GroupAuditLogEntry Entry(string eventType) => new()
    {
        Id = "gaud_1",
        GroupId = "grp_test",
        EventType = eventType,
        ActorId = "usr_actor",
        ActorDisplayName = "Moderator",
        TargetId = "usr_target",
        CreatedAt = When,
        Description = "something happened",
    };
}
