using System.Reflection;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;
using Newtonsoft.Json;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The audit log is the authoritative moderation source (spec 5.9), so what it means is decided
/// here and nowhere else.
/// </summary>
/// <remarks>
/// Half of these run against real entries: <c>Fixtures/audit-log-entries-2026-09-13.jsonl</c>
/// is a pull from a live group with display names replaced and ids kept, deserialised through
/// Newtonsoft the way the SDK does it. VRChat types <c>eventType</c> as a bare string and
/// documents <c>data</c> only as "dependent on the event type", so captured samples are the only
/// evidence there is for either.
/// </remarks>
public class AuditLogEntryMapperTests
{
    private static readonly DateTime When = new(2026, 6, 15, 14, 32, 7, DateTimeKind.Utc);

    [Theory]
    [InlineData("group.member.join", FactType.MemberJoined)]
    [InlineData("group.member.leave", FactType.MemberLeft)]
    [InlineData("group.member.remove", FactType.MemberKicked)]
    [InlineData("group.user.ban", FactType.MemberBanned)]
    [InlineData("group.member.user.ban", FactType.MemberBanned)]
    [InlineData("group.user.unban", FactType.MemberUnbanned)]
    [InlineData("group.member.user.unban", FactType.MemberUnbanned)]
    [InlineData("group.member.role.assign", FactType.RoleGranted)]
    [InlineData("group.member.role.unassign", FactType.RoleRevoked)]
    [InlineData("group.role.update", FactType.RoleUpdated)]
    [InlineData("group.invite.create", FactType.InviteCreated)]
    [InlineData("group.request.create", FactType.JoinRequestCreated)]
    [InlineData("group.request.reject", FactType.JoinRequestRejected)]
    [InlineData("group.request.block", FactType.JoinRequestBlocked)]
    [InlineData("group.post.create", FactType.GroupPostCreated)]
    [InlineData("group.post.delete", FactType.GroupPostDeleted)]
    [InlineData("group.instance.create", FactType.GroupInstanceCreated)]
    [InlineData("group.instance.close", FactType.GroupInstanceClosed)]
    [InlineData("group.instance.update", FactType.GroupInstanceUpdated)]
    [InlineData("group.instance.announcement", FactType.GroupInstanceAnnouncement)]
    [InlineData("group.instance.kick", FactType.GroupInstanceKick)]
    [InlineData("group.instance.warn", FactType.GroupInstanceWarn)]
    [InlineData("group.calendarEvent.create", FactType.CalendarEventCreated)]
    [InlineData("group.calendarEvent.delete", FactType.CalendarEventDeleted)]
    [InlineData("group.calendarEvent.series.update", FactType.CalendarEventSeriesUpdated)]
    [InlineData("group.calendarEvent.series.delete", FactType.CalendarEventSeriesDeleted)]
    [InlineData("group.update", FactType.GroupInfoChanged)]
    public void EachKnownEventTypeBecomesItsFactType(string eventType, string expected)
    {
        var mapping = AuditLogEntryMapper.Map(Entry(eventType));

        Assert.True(mapping.Mapped);
        Assert.Equal(AuditLogRejection.None, mapping.Rejection);
        Assert.Equal(expected, mapping.Fact!.Type);
        Assert.Null(mapping.Fact.TypeRaw);
    }

    /// <summary>
    /// Spec 5.8's accountability is built entirely on this. A sync diff can see that someone is
    /// gone and can never see who removed them; this is the only source that can, and a fact that
    /// dropped the actor would make the audit log no better than the inference.
    /// </summary>
    [Fact]
    public void TheActorIsCarriedOntoTheFactSeparatelyFromTheSubject()
    {
        var mapping = AuditLogEntryMapper.Map(Entry("group.user.ban"));
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

        // Still in the payload, as a null: "VRChat sent nothing" is itself worth keeping.
        Assert.True(fact.Data!.ContainsKey("actorId"));
        Assert.Null(fact.Data["actorId"]);
    }

    /// <summary>
    /// Spec 5.3: the audit log states when a thing happened, so there is no window. Adding one
    /// would tell every downstream chart to treat an exact ban as an estimate.
    /// </summary>
    [Fact]
    public void TheTimeIsExactAndTheSourceSaysSo()
    {
        var fact = AuditLogEntryMapper.Map(Entry("group.user.ban")).Fact!;

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
        var mapping = AuditLogEntryMapper.Map(Entry("group.user.ban"));

        Assert.Equal("gaud_1", mapping.EntryId);
        Assert.Equal("gaud_1", mapping.Fact!.Data!["auditEntryId"]!.GetValue<string>());
        Assert.Equal("group.user.ban", mapping.Fact.Data["eventType"]!.GetValue<string>());
        Assert.Equal("grp_test", mapping.Fact.Data["groupId"]!.GetValue<string>());
    }

    /// <summary>
    /// Every field of every entry, always. The columns hold the actor, subject and time too; the
    /// payload is the record of what VRChat said, and it has to survive whatever a later change
    /// decides the columns mean.
    /// </summary>
    [Theory]
    [MemberData(nameof(KnownEventTypes))]
    public void TheWholeEntryIsKeptForEveryEventType(string eventType)
    {
        var entry = Entry(eventType);
        entry.Data = """{"anything":"at all"}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal("gaud_1", data["auditEntryId"]!.GetValue<string>());
        Assert.Equal(eventType, data["eventType"]!.GetValue<string>());
        Assert.Equal("grp_test", data["groupId"]!.GetValue<string>());
        Assert.Equal("usr_actor", data["actorId"]!.GetValue<string>());
        Assert.Equal("Moderator", data["actorDisplayName"]!.GetValue<string>());
        Assert.Equal("usr_target", data["targetId"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(When), DateTimeOffset.Parse(data["createdAt"]!.GetValue<string>()));
        Assert.Equal("something happened", data["description"]!.GetValue<string>());
        Assert.Equal("at all", data["auditData"]!["anything"]!.GetValue<string>());
    }

    public static TheoryData<string> KnownEventTypes()
    {
        var types = new TheoryData<string>();
        foreach (var type in GroupAuditLogEvents.Known.Order(StringComparer.Ordinal))
            types.Add(type);
        return types;
    }

    /// <summary>
    /// No key on the SDK's entry is absent from the payload. Reflected rather than listed, so a
    /// field VRChat adds and the SDK picks up fails here instead of quietly not being recorded.
    /// </summary>
    [Fact]
    public void NoFieldOnTheSdkEntryIsMissingFromThePayload()
    {
        var entry = Entry("group.user.ban");
        entry.Data = """{"k":"v"}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        var properties = typeof(GroupAuditLogEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => p.Name)
            .ToList();

        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            var key = PayloadKeyFor(property);

            Assert.True(
                data.ContainsKey(key),
                $"GroupAuditLogEntry.{property} has no key '{key}' in the stored payload.");
            Assert.NotNull(data[key]);
        }
    }

    private static string PayloadKeyFor(string sdkProperty) => sdkProperty switch
    {
        "Id" => "auditEntryId",
        "Data" => "auditData",
        _ => char.ToLowerInvariant(sdkProperty[0]) + sdkProperty[1..],
    };

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

    [Fact]
    public void AMissingPayloadIsKeptAsANullRatherThanOmitted()
    {
        var entry = Entry("group.member.join");
        entry.Data = null!;

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.True(data.ContainsKey("auditData"));
        Assert.Null(data["auditData"]);
    }

    // ── Lifting: only what has been seen ───────────────────────────────────────────────────

    /// <summary>
    /// A role event's <c>data</c> is <c>{roleId, roleName}</c> -- seen live. Lifted beside the
    /// verbatim copy, never instead of it.
    /// </summary>
    [Theory]
    [InlineData("group.member.role.assign")]
    [InlineData("group.member.role.unassign")]
    [InlineData("group.role.update")]
    public void RoleEventsLiftTheRoleBesideTheVerbatimCopy(string eventType)
    {
        var entry = Entry(eventType);
        entry.Data = """{"roleId":"grol_9","roleName":"Moderator"}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal("grol_9", data["roleId"]!.GetValue<string>());
        Assert.Equal("Moderator", data["roleName"]!.GetValue<string>());
        Assert.Equal("grol_9", data["auditData"]!["roleId"]!.GetValue<string>());
    }

    /// <summary>
    /// A group update's <c>data</c> is <c>{field: {old, new}}</c> per VRChat's own example. Each
    /// pair goes under <c>changed</c> -- the key the group-info producer already uses -- and
    /// anything that is not a pair does not.
    /// </summary>
    [Fact]
    public void AGroupUpdateLiftsEachOldNewPairUnderChanged()
    {
        var entry = Entry("group.update");
        entry.Data = """{"name":{"old":"Old name","new":"New name"},"joinState":{"old":"open","new":"request"},"note":"not a pair"}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;
        var changed = Assert.IsType<JsonObject>(data["changed"]);

        Assert.Equal("Old name", changed["name"]!["old"]!.GetValue<string>());
        Assert.Equal("New name", changed["name"]!["new"]!.GetValue<string>());
        Assert.Equal("request", changed["joinState"]!["new"]!.GetValue<string>());
        Assert.False(changed.ContainsKey("note"));

        // The copy is untouched.
        Assert.Equal("not a pair", data["auditData"]!["note"]!.GetValue<string>());
        Assert.Equal("Old name", data["auditData"]!["name"]!["old"]!.GetValue<string>());
    }

    [Fact]
    public void ARoleUpdateLiftsBothTheRoleAndItsChanges()
    {
        var entry = Entry("group.role.update");
        entry.Data = """{"roleId":"grol_1","roleName":"Mod","permissions":{"old":["a"],"new":["a","b"]}}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal("grol_1", data["roleId"]!.GetValue<string>());
        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Equal(2, changed["permissions"]!["new"]!.AsArray().Count);
        Assert.False(changed.ContainsKey("roleId"));
    }

    /// <summary>
    /// Verbatim, nothing invented. These types' payloads have not been observed, and a field
    /// lifted under a guessed name is one two producers and a query then depend on. Even data
    /// that looks like a role or a diff stays where VRChat put it until a real sample says so.
    /// </summary>
    [Theory]
    [InlineData("group.member.join")]
    [InlineData("group.member.leave")]
    [InlineData("group.member.remove")]
    [InlineData("group.user.ban")]
    [InlineData("group.user.unban")]
    [InlineData("group.invite.create")]
    [InlineData("group.request.create")]
    [InlineData("group.request.reject")]
    [InlineData("group.request.block")]
    [InlineData("group.post.create")]
    [InlineData("group.post.delete")]
    [InlineData("group.instance.create")]
    [InlineData("group.instance.close")]
    [InlineData("group.instance.update")]
    [InlineData("group.instance.announcement")]
    [InlineData("group.instance.kick")]
    [InlineData("group.instance.warn")]
    [InlineData("group.calendarEvent.create")]
    [InlineData("group.calendarEvent.delete")]
    [InlineData("group.calendarEvent.series.update")]
    [InlineData("group.calendarEvent.series.delete")]
    public void NothingIsLiftedForTypesWhosePayloadHasNotBeenSeen(string eventType)
    {
        var entry = Entry(eventType);
        entry.Data = """{"roleId":"grol_9","name":{"old":"a","new":"b"}}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        string[] expectedKeys =
        [
            "auditEntryId", "eventType", "groupId", "actorId", "actorDisplayName",
            "targetId", "createdAt", "description", "auditData",
        ];

        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), data.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("grol_9", data["auditData"]!["roleId"]!.GetValue<string>());
    }

    // ── Targets and unknowns ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Instance moderation gets its own types. VRChat records ejecting somebody from an instance
    /// separately from removing them from the group, and folding the first into
    /// <see cref="FactType.MemberKicked"/> would blend two different actions into one count.
    /// </summary>
    [Fact]
    public void AnInstanceKickIsNotAGroupKick()
    {
        var fact = AuditLogEntryMapper.Map(Entry("group.instance.kick")).Fact!;

        Assert.Equal(FactType.GroupInstanceKick, fact.Type);
        Assert.NotEqual(FactType.MemberKicked, fact.Type);
    }

    /// <summary>
    /// For instance events <c>targetId</c> is probably a location string. Probably: it is never
    /// parsed to find out (spec 3.1.1), and it reaches the subject column byte for byte.
    /// </summary>
    [Fact]
    public void ALocationShapedTargetIsCarriedUntouched()
    {
        const string location = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_x)~groupAccessType(plus)~region(use)";

        var entry = Entry("group.instance.kick");
        entry.TargetId = location;

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(location, fact.SubjectId);
        Assert.Equal(location, fact.Data!["targetId"]!.GetValue<string>());
    }

    /// <summary>
    /// The failure this producer must never have is a fact log that looks complete and is not.
    /// VRChat types <c>eventType</c> as a free-form string, so an unrecognised value is a feature
    /// Modbot has not catalogued -- and it has to surface.
    /// </summary>
    [Fact]
    public void AnUnrecognisedEventTypeIsRecordedAsUnrecognisedAndReported()
    {
        var mapping = AuditLogEntryMapper.Map(Entry("group.something.new"));

        // Recorded -- under Unrecognised, with VRChat's own name kept -- rather than dropped.
        // The old behaviour was to return no fact at all, and VRChat's audit log ages out, so
        // every entry handled that way was gone for good.
        Assert.True(mapping.Mapped);
        Assert.Equal(FactType.Unrecognised, mapping.Fact!.Type);
        Assert.Equal("group.something.new", mapping.Fact.TypeRaw);

        // ...and still reported, so somebody adds the mapping.
        Assert.Equal(AuditLogRejection.UnknownEventType, mapping.Rejection);
        Assert.Equal("group.something.new", mapping.EventType);

        // Still identified, so the diagnostics can point an operator at the real entry in VRChat.
        Assert.Equal("gaud_1", mapping.EntryId);
    }

    [Fact]
    public void AnEntryWithNoTargetCannotBecomeAFact()
    {
        var entry = Entry("group.user.ban");
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
        var entry = Entry("group.user.ban");
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

    // ── Real entries ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryLiveSampleMapsToANamedFactType()
    {
        var samples = LiveSamples();
        Assert.NotEmpty(samples);

        Assert.All(samples, entry =>
        {
            var mapping = AuditLogEntryMapper.Map(entry);

            Assert.True(mapping.Mapped, entry.EventType);
            Assert.Equal(AuditLogRejection.None, mapping.Rejection);
            Assert.NotEqual(FactType.Unrecognised, mapping.Fact!.Type);
            Assert.Null(mapping.Fact.TypeRaw);
        });
    }

    /// <summary>
    /// The one production recorded as <c>modbot.unrecognised</c>: somebody asking to join. It is
    /// a real event with a real subject, and it now has a name.
    /// </summary>
    [Fact]
    public void TheJoinRequestProductionCouldNotNameNowMaps()
    {
        var entry = LiveSample("gaud_812b3066-7a22-4f8c-b39a-ce9178c80c63");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("group.request.create", entry.EventType);
        Assert.Equal(FactType.JoinRequestCreated, fact.Type);
        Assert.Equal("usr_51991099-51c4-4b82-96ea-da818800aa88", fact.SubjectId);
        Assert.Equal(fact.SubjectId, fact.ActorId);
    }

    /// <summary>
    /// The spelling VRChat really uses for a ban, from a real ban. The description is VRChat's
    /// own template and carries no reason, which is why none is parsed.
    /// </summary>
    [Fact]
    public void TheLiveBanSpellingIsABan()
    {
        var entry = LiveSample("gaud_eb69ac86-715a-433b-be60-1761f0ec2013");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("group.user.ban", entry.EventType);
        Assert.Equal(FactType.MemberBanned, fact.Type);
        Assert.Equal("usr_c9094d86-1846-43eb-b79d-7e3dc318f42a", fact.SubjectId);
        Assert.Equal("usr_2a323be9-ac4e-4502-af07-357d79c48ccf", fact.ActorId);
        Assert.Empty(Assert.IsType<JsonObject>(fact.Data!["auditData"]));
    }

    [Fact]
    public void TheLiveRoleRemovalLiftsTheRoleItNames()
    {
        var entry = LiveSample("gaud_2c2dddcc-7ce9-42a0-954a-3e282bfa43ae");
        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal("grol_10d89d15-2f5b-479c-ba00-0b257014713a", data["roleId"]!.GetValue<string>());
        Assert.Equal("Moderator - Guard", data["roleName"]!.GetValue<string>());
        Assert.Equal("grol_10d89d15-2f5b-479c-ba00-0b257014713a", data["auditData"]!["roleId"]!.GetValue<string>());
    }

    /// <summary>
    /// Joins, leaves, invites and bans arrive with <c>data: {}</c>. An empty object is what VRChat
    /// sent and is kept as one -- distinguishable from a null, which would mean it sent nothing.
    /// </summary>
    [Theory]
    [InlineData("gaud_4fdc324b-ff74-4fc9-83cb-d86e64325a3d", FactType.MemberJoined)]
    [InlineData("gaud_143d61fb-dcce-489f-8341-76498056e8ed", FactType.MemberLeft)]
    [InlineData("gaud_6f8a077d-598b-4eae-b39d-ceea8b5bd59c", FactType.InviteCreated)]
    public void LiveEmptyPayloadsAreKeptAsEmptyObjects(string entryId, string expectedType)
    {
        var fact = AuditLogEntryMapper.Map(LiveSample(entryId)).Fact!;

        Assert.Equal(expectedType, fact.Type);
        Assert.Empty(Assert.IsType<JsonObject>(fact.Data!["auditData"]));
    }

    /// <summary>
    /// Newtonsoft reads an offset-bearing timestamp as local time. The fact must carry the
    /// instant VRChat sent, whatever timezone the host happens to be in.
    /// </summary>
    [Fact]
    public void LiveTimestampsAreReadAsTheInstantVRChatSent()
    {
        var fact = AuditLogEntryMapper.Map(LiveSample("gaud_143d61fb-dcce-489f-8341-76498056e8ed")).Fact!;

        Assert.Equal(
            new DateTime(2026, 9, 13, 13, 40, 56, 594, DateTimeKind.Utc),
            fact.OccurredAt.UtcDateTime);
    }

    private static GroupAuditLogEntry LiveSample(string entryId)
        => LiveSamples().Single(e => e.Id == entryId);

    private static IReadOnlyList<GroupAuditLogEntry> LiveSamples()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-log-entries-2026-09-13.jsonl");

        return System.IO.File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonConvert.DeserializeObject<GroupAuditLogEntry>(line)!)
            .ToList();
    }

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
