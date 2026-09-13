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
/// <para>
/// Half of these run against real entries: <c>Fixtures/audit-log-entries-2026-09-13.jsonl</c>
/// is a pull from a live group with display names replaced and ids kept, deserialised through
/// Newtonsoft the way the SDK does it. VRChat types <c>eventType</c> as a bare string and
/// documents <c>data</c> only as "dependent on the event type", so captured samples are the only
/// evidence there is for either.
/// </para>
/// <para>
/// <c>Fixtures/audit-log-shapes-2026-09-13.jsonl</c> is one row per shape recorded in the
/// audit-log research, section 6, after the re-walk of 1,241 live entries: the key set of each
/// row is exactly what every live row of that type carried, and the location string is the
/// quoted one. Display names, post and announcement text are removed, and the parts of ids the
/// research elides are filled in -- so the ids are shaped like the live ones without being them.
/// </para>
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
    /// Verbatim, nothing invented. These types' payloads have not been observed -- or, for the
    /// membership events, were observed empty -- and a field lifted under a guessed name is one
    /// two producers and a query then depend on. Even data that looks like a role stays where
    /// VRChat put it until a real sample says so.
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
    [InlineData("group.post.delete")]
    [InlineData("group.calendarEvent.delete")]
    [InlineData("group.calendarEvent.series.update")]
    [InlineData("group.calendarEvent.series.delete")]
    public void NoScalarIsLiftedForTypesWhosePayloadHasNotBeenSeen(string eventType)
    {
        var entry = Entry(eventType);
        entry.Data = """{"roleId":"grol_9","title":"looks liftable"}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal(BaseKeys.Order(StringComparer.Ordinal), data.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("grol_9", data["auditData"]!["roleId"]!.GetValue<string>());
    }

    private static readonly string[] BaseKeys =
    [
        "auditEntryId", "eventType", "groupId", "actorId", "actorDisplayName",
        "targetId", "createdAt", "description", "auditData",
    ];

    // ── The diff lift is a shape, not a type list ──────────────────────────────────────────

    /// <summary>
    /// <c>group.instance.update</c> arrived with <c>calendarEntryId: {old, new}</c> and was not
    /// on the list of types the lift applied to. The lift is now by shape, so it is.
    /// </summary>
    [Fact]
    public void AnInstanceUpdateLiftsItsDiffUnderChanged()
    {
        var data = AuditLogEntryMapper.Map(ShapeSample("gaud_1c9f4e2b-7d3a-4b68-a0e5-6b2d8f7c1a39")).Fact!.Data!;

        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Null(changed["calendarEntryId"]!["old"]);
        Assert.Equal("cal_6f2d9b3e-8a1c-4d57-b9e0-3c7f5a2d1e84", changed["calendarEntryId"]!["new"]!.GetValue<string>());
    }

    /// <summary>
    /// The live shape of a role update is <c>{lastUpdatedByUserId, permissions: {old, new}}</c>.
    /// The pair is a diff; the scalar beside it is not, and it does not become one for sitting
    /// next to one.
    /// </summary>
    [Fact]
    public void ARoleUpdateLiftsTheDiffAndLeavesTheScalarBesideIt()
    {
        var data = AuditLogEntryMapper.Map(ShapeSample("gaud_d2b7e4a9-3f1c-4e86-a7b0-5c9d8e2f6a13")).Fact!.Data!;

        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Equal(["permissions"], changed.Select(p => p.Key));
        Assert.Equal(2, changed["permissions"]!["new"]!.AsArray().Count);

        // Still in the verbatim copy, where it was.
        Assert.Equal(
            "usr_2a323be9-ac4e-4502-af07-357d79c48ccf",
            data["auditData"]!["lastUpdatedByUserId"]!.GetValue<string>());
    }

    /// <summary>
    /// By shape means for any type -- including one Modbot has never heard of. A type list is
    /// what left <c>instance.update</c> uncovered; a shape cannot be forgotten.
    /// </summary>
    [Theory]
    [InlineData("group.instance.close")]
    [InlineData("group.calendarEvent.series.update")]
    [InlineData("group.something.new")]
    public void AnyEventTypeLiftsATopLevelOldNewPair(string eventType)
    {
        var entry = Entry(eventType);
        entry.Data = """{"name":{"old":"a","new":"b"},"note":"scalar"}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Equal("b", changed["name"]!["new"]!.GetValue<string>());
        Assert.False(changed.ContainsKey("note"));
    }

    /// <summary>
    /// "The fields of this entry that changed" is a statement about the top level. A pair buried
    /// inside some other object is that object's business, and lifting it would put a key under
    /// <c>changed</c> that is not a field of the entry.
    /// </summary>
    [Fact]
    public void AnOldNewPairNestedDeeperThanTheTopLevelIsNotLifted()
    {
        var entry = Entry("group.update");
        entry.Data = """{"settings":{"inner":{"old":1,"new":2}},"list":[{"old":1,"new":2}]}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.False(data.ContainsKey("changed"));
        Assert.Equal(1, data["auditData"]!["settings"]!["inner"]!["old"]!.GetValue<int>());
    }

    /// <summary>
    /// Exactly <c>old</c> and <c>new</c>. An object that happens to have those two keys among
    /// others is a different shape, and reading it as a diff would drop whatever else it carried.
    /// </summary>
    [Fact]
    public void AnObjectWithOldAndNewAmongOtherKeysIsNotADiff()
    {
        var entry = Entry("group.update");
        entry.Data = """{"a":{"old":1,"new":2,"reason":"x"},"b":{"old":1},"c":{"old":1,"new":2}}""";

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Equal(["c"], changed.Select(p => p.Key));
    }

    // ── Scalars observed stable, lifted beside the copy ────────────────────────────────────

    [Theory]
    [InlineData("gaud_3f7b1d9c-8e2a-4c05-b6d3-4a9e0f2c7b54", "group.instance.create")]
    [InlineData("gaud_b82e6a4d-5c19-4f7e-9d0b-3e1a7c5f2d96", "group.instance.close")]
    public void AnInstanceCreateOrCloseLiftsItsAccessTypeAndNothingElse(string entryId, string eventType)
    {
        var entry = ShapeSample(entryId);
        Assert.Equal(eventType, entry.EventType);

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal("public", data["groupAccessType"]!.GetValue<string>());
        Assert.Equal(
            BaseKeys.Append("groupAccessType").Order(StringComparer.Ordinal),
            data.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.False(data.ContainsKey("roleIds"));
    }

    [Fact]
    public void AnAnnouncementLiftsItsTitleAndMessage()
    {
        var data = AuditLogEntryMapper.Map(ShapeSample("gaud_e6c0d3b8-2a7f-4e19-8b5c-9f4d1a6e3c72")).Fact!.Data!;

        Assert.Equal("[announcement title removed]", data["title"]!.GetValue<string>());
        Assert.Equal("[announcement text removed]", data["message"]!.GetValue<string>());
        Assert.Equal("[announcement text removed]", data["auditData"]!["message"]!.GetValue<string>());
    }

    /// <summary>
    /// A post's <c>targetId</c> is the notification VRChat sent members, not the post; the post
    /// itself is in <c>auditData</c>. The four stable scalars come up; the rest of the shape
    /// (<c>imageId</c>, <c>roleIds</c>, <c>sendNotification</c>) stays in the copy only.
    /// </summary>
    [Fact]
    public void APostLiftsItsTitleTextAuthorAndVisibility()
    {
        var entry = ShapeSample("gaud_7e3a9c5d-0b4f-4d21-9f6e-2c8b1d7a4e05");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;
        var data = fact.Data!;

        Assert.Equal("not_4a8d2f6b-9e1c-4b73-a5d0-7f3e6c2b9a18", fact.SubjectId);
        Assert.Equal("[post title removed]", data["title"]!.GetValue<string>());
        Assert.Equal("[post text removed]", data["text"]!.GetValue<string>());
        Assert.Equal("usr_2a323be9-ac4e-4502-af07-357d79c48ccf", data["authorId"]!.GetValue<string>());
        Assert.Equal("public", data["visibility"]!.GetValue<string>());

        Assert.False(data.ContainsKey("imageId"));
        Assert.False(data.ContainsKey("roleIds"));
        Assert.False(data.ContainsKey("sendNotification"));
        Assert.True(data["auditData"]!["sendNotification"]!.GetValue<bool>());
    }

    [Fact]
    public void ACalendarEventLiftsItsTitleTypeAndAccessType()
    {
        var data = AuditLogEntryMapper.Map(ShapeSample("gaud_a5d8f2c1-6e9b-4a07-b3f4-8d1c5e0a7b62")).Fact!.Data!;

        Assert.Equal("[event title removed]", data["title"]!.GetValue<string>());
        Assert.Equal("event", data["type"]!.GetValue<string>());
        Assert.Equal("public", data["accessType"]!.GetValue<string>());

        Assert.False(data.ContainsKey("imageId"));
        Assert.Equal("[event description removed]", data["auditData"]!["description"]!.GetValue<string>());
    }

    // ── Instance events fill the instance columns ──────────────────────────────────────────

    private const string ShapeWorldId = "wrld_44f4a344-2d1b-4c7e-9a3f-8b5e6d7c0f12";
    private const string ShapeInstanceId = "93927";

    private const string ShapeLocation =
        "wrld_44f4a344-2d1b-4c7e-9a3f-8b5e6d7c0f12:93927~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(public)~region(us)";

    /// <summary>
    /// A kick or a warn names the person in <c>targetId</c> and carries the instance in
    /// <c>auditData.location</c> -- 447 live rows, every one the same. The person stays the
    /// subject; the instance goes into the two columns that exist for it.
    /// </summary>
    [Theory]
    [InlineData("gaud_5d1e7c0a-3b2f-4e8a-9c41-0f6b2a7d8e13", "group.instance.kick", "usr_7b3f9e21-6c4d-4a58-8e07-1d2c3b4a5f60")]
    [InlineData("gaud_9a4c2e7f-1d8b-4f36-a52e-7c0b3d9e6a21", "group.instance.warn", "usr_c15e8d3a-9f27-4b06-b3c4-2e7a1d6f9b08")]
    public void AKickOrWarnTakesTheInstanceFromItsDataAndKeepsThePersonAsTheSubject(
        string entryId, string eventType, string person)
    {
        var entry = ShapeSample(entryId);
        Assert.Equal(eventType, entry.EventType);

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(person, fact.SubjectId);
        Assert.Equal(ShapeWorldId, fact.WorldId);
        Assert.Equal(ShapeInstanceId, fact.InstanceId);

        // The location is not lifted into the payload: it is in the copy, and in the columns.
        var data = fact.Data!;
        Assert.Equal(BaseKeys.Order(StringComparer.Ordinal), data.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ShapeLocation, data["auditData"]!["location"]!.GetValue<string>());
    }

    /// <summary>
    /// A create, close, announcement or update puts the location in <c>targetId</c>. The subject
    /// stays that raw string, byte for byte -- the columns are filled beside it, not from it.
    /// </summary>
    [Theory]
    [InlineData("gaud_3f7b1d9c-8e2a-4c05-b6d3-4a9e0f2c7b54", "group.instance.create")]
    [InlineData("gaud_b82e6a4d-5c19-4f7e-9d0b-3e1a7c5f2d96", "group.instance.close")]
    [InlineData("gaud_e6c0d3b8-2a7f-4e19-8b5c-9f4d1a6e3c72", "group.instance.announcement")]
    [InlineData("gaud_1c9f4e2b-7d3a-4b68-a0e5-6b2d8f7c1a39", "group.instance.update")]
    public void ACreateCloseAnnouncementOrUpdateTakesTheInstanceFromItsTargetAndKeepsTheTargetWhole(
        string entryId, string eventType)
    {
        var entry = ShapeSample(entryId);
        Assert.Equal(eventType, entry.EventType);

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(ShapeLocation, fact.SubjectId);
        Assert.Equal(ShapeLocation, fact.Data!["targetId"]!.GetValue<string>());
        Assert.Equal(ShapeWorldId, fact.WorldId);
        Assert.Equal(ShapeInstanceId, fact.InstanceId);
    }

    /// <summary>
    /// Delimiters, not shapes. A world id that does not start with <c>wrld_</c> and an instance
    /// id that is not a number are both legitimate -- legacy ids follow no structure and groups
    /// set instance ids to readable text (spec 3.1.1) -- and a check on either would silently
    /// leave the oldest instances out of every per-instance count.
    /// </summary>
    [Fact]
    public void TheWorldAndInstanceAreNotCheckedForShape()
    {
        var entry = Entry("group.instance.create");
        entry.TargetId = "Old Lobby:VIP Lounge~group(grp_x)";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("Old Lobby", fact.WorldId);
        Assert.Equal("VIP Lounge", fact.InstanceId);
        Assert.Equal("Old Lobby:VIP Lounge~group(grp_x)", fact.SubjectId);
    }

    /// <summary>
    /// No <c>:</c> means the string is not a world-and-instance pair as Modbot reads it. It is
    /// kept whole as the world rather than guessed at, and nothing is invented for the instance.
    /// </summary>
    [Theory]
    [InlineData("group.instance.create")]
    [InlineData("group.instance.kick")]
    public void ALocationWithNoColonKeepsTheWholeStringAsTheWorldAndNoInstance(string eventType)
    {
        var entry = Entry(eventType);
        if (eventType == "group.instance.kick")
            entry.Data = """{"location":"just-a-world"}""";
        else
            entry.TargetId = "just-a-world";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("just-a-world", fact.WorldId);
        Assert.Null(fact.InstanceId);
    }

    [Fact]
    public void ALocationWithNoQualifiersStillSplits()
    {
        var entry = Entry("group.instance.close");
        entry.TargetId = "wrld_a:12345";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("wrld_a", fact.WorldId);
        Assert.Equal("12345", fact.InstanceId);
    }

    /// <summary>
    /// A kick whose data has no <c>location</c> -- the shape has been stable so far, but VRChat
    /// documents none of it -- fills nothing. The person is still the subject, the entry is still
    /// recorded, and nothing throws.
    /// </summary>
    [Fact]
    public void AKickWithNoLocationInItsDataFillsNoInstanceColumns()
    {
        var entry = Entry("group.instance.kick");
        entry.Data = """{}""";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("usr_target", fact.SubjectId);
        Assert.Null(fact.WorldId);
        Assert.Null(fact.InstanceId);
    }

    /// <summary>
    /// Only instance events are read this way. A ban whose target happens to look like a
    /// location is a ban of whatever that string is, and the columns stay empty.
    /// </summary>
    [Theory]
    [InlineData("group.user.ban")]
    [InlineData("group.member.join")]
    [InlineData("group.post.create")]
    [InlineData("group.something.new")]
    public void NoOtherEventTypeFillsTheInstanceColumns(string eventType)
    {
        var entry = Entry(eventType);
        entry.TargetId = ShapeLocation;
        entry.Data = $$$"""{"location":"{{{ShapeLocation}}}"}""";

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Null(fact.WorldId);
        Assert.Null(fact.InstanceId);
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
    /// A location-shaped <c>targetId</c> reaches the subject column byte for byte, whatever the
    /// type. For an instance create it is also split into the world and instance columns; for a
    /// kick it is not, because a kick's target is the person and its location lives in the data.
    /// Neither reading changes the subject.
    /// </summary>
    [Theory]
    [InlineData("group.instance.kick")]
    [InlineData("group.instance.create")]
    public void ALocationShapedTargetIsCarriedUntouched(string eventType)
    {
        const string location = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_x)~groupAccessType(plus)~region(use)";

        var entry = Entry(eventType);
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

    /// <summary>
    /// One row per shape in the research, and every one of them maps under its own name with the
    /// columns filled -- so a shape the research records is a shape the mapper reads.
    /// </summary>
    [Fact]
    public void EveryRecordedShapeMapsToANamedFactType()
    {
        var samples = ShapeSamples();
        Assert.Equal(10, samples.Count);

        Assert.All(samples, entry =>
        {
            var mapping = AuditLogEntryMapper.Map(entry);

            Assert.True(mapping.Mapped, entry.EventType);
            Assert.Equal(AuditLogRejection.None, mapping.Rejection);
            Assert.NotEqual(FactType.Unrecognised, mapping.Fact!.Type);
        });
    }

    private static GroupAuditLogEntry LiveSample(string entryId)
        => LiveSamples().Single(e => e.Id == entryId);

    private static GroupAuditLogEntry ShapeSample(string entryId)
        => ShapeSamples().Single(e => e.Id == entryId);

    private static IReadOnlyList<GroupAuditLogEntry> LiveSamples()
        => Fixture("audit-log-entries-2026-09-13.jsonl");

    private static IReadOnlyList<GroupAuditLogEntry> ShapeSamples()
        => Fixture("audit-log-shapes-2026-09-13.jsonl");

    private static IReadOnlyList<GroupAuditLogEntry> Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

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
