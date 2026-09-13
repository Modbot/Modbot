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
/// <c>Fixtures/audit-log-shapes-2026-09-13.jsonl</c> is real too: one production row per event
/// type the entries fixture does not already cover, pulled on 2026-09-13 from the same group and
/// redacted the same way. <c>actorDisplayName</c> is "Moderator A", <c>description</c> is a
/// placeholder, and every free-text field inside <c>data</c> (<c>title</c>, <c>text</c>,
/// <c>message</c>, <c>description</c>) is the literal string <c>[redacted]</c>. Ids, timestamps,
/// locations, ids inside <c>data</c>, permission lists, booleans and enums are as VRChat sent
/// them. It replaced a reconstructed set whose ids and locations were invented, and several of
/// the assertions below pin details the reconstruction had wrong.
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
    /// on the list of types the lift applied to. The lift is now by shape, so it is. In the real
    /// row both sides are the same id -- VRChat recorded an update that changed nothing -- and the
    /// pair is lifted as it is. Whether a diff amounts to anything is a reader's question, not
    /// the mapper's; dropping it would be interpreting.
    /// </summary>
    [Fact]
    public void AnInstanceUpdateLiftsItsDiffUnderChangedEvenWhenOldEqualsNew()
    {
        var data = AuditLogEntryMapper.Map(ShapeSample("gaud_15bc3934-d312-46ad-b811-8353054bd82d")).Fact!.Data!;

        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Equal(["calendarEntryId"], changed.Select(p => p.Key));
        Assert.Equal("cal_a3a5d7bd-aa89-4b11-acf8-213278488657", changed["calendarEntryId"]!["old"]!.GetValue<string>());
        Assert.Equal("cal_a3a5d7bd-aa89-4b11-acf8-213278488657", changed["calendarEntryId"]!["new"]!.GetValue<string>());
    }

    /// <summary>
    /// The live shape of a role update is <c>{permissions: {old, new}, lastUpdatedByUserId: {old,
    /// new}}</c> -- two pairs, so two entries under <c>changed</c>. The reconstruction this row
    /// replaced had <c>lastUpdatedByUserId</c> as a bare scalar; a lift by type list would have
    /// needed telling about the difference, and a lift by shape did not. The permission lists are
    /// long and repeat entries exactly as VRChat sent them, so the check is on the shape and on
    /// one permission visible in the row, not on a count. The role is the target, not a field of
    /// <c>data</c>, so nothing comes up under <c>roleId</c>.
    /// </summary>
    [Fact]
    public void ARoleUpdateLiftsEveryPairItCarries()
    {
        var entry = ShapeSample("gaud_7f18287a-7dda-4ab6-85ca-6c4ee2975174");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;
        var data = fact.Data!;

        Assert.Equal("grol_b2488a21-05a7-4bea-95d1-07b25291d28b", fact.SubjectId);

        var changed = Assert.IsType<JsonObject>(data["changed"]);
        Assert.Equal(["permissions", "lastUpdatedByUserId"], changed.Select(p => p.Key));

        var permissions = changed["permissions"]!;
        Assert.IsType<JsonArray>(permissions["old"]);
        Assert.IsType<JsonArray>(permissions["new"]);
        Assert.Contains("group-audit-view", permissions["new"]!.AsArray().Select(p => p!.GetValue<string>()));

        Assert.Null(changed["lastUpdatedByUserId"]!["old"]);
        Assert.Equal(
            "usr_64dbb478-bcd0-42ad-8b77-ac85b858b9a7",
            changed["lastUpdatedByUserId"]!["new"]!.GetValue<string>());

        Assert.False(data.ContainsKey("roleId"));
        Assert.False(data.ContainsKey("roleName"));

        // Both pairs are still in the verbatim copy, where they were.
        Assert.Equal(
            "usr_64dbb478-bcd0-42ad-8b77-ac85b858b9a7",
            data["auditData"]!["lastUpdatedByUserId"]!["new"]!.GetValue<string>());
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

    /// <summary>
    /// <c>groupAccessType</c> comes up; <c>roleIds</c> and <c>calendarEntryId</c> do not. The two
    /// real rows differ from each other in ways the reconstruction had flattened: the create is a
    /// <c>plus</c> instance and the close a <c>public</c> one.
    /// </summary>
    [Theory]
    [InlineData("gaud_87905023-d926-46dc-a662-5c5d6d1065b9", "group.instance.create", "plus")]
    [InlineData("gaud_78bda451-9973-4592-9e85-666d40f93929", "group.instance.close", "public")]
    public void AnInstanceCreateOrCloseLiftsItsAccessTypeAndNothingElse(
        string entryId, string eventType, string accessType)
    {
        var entry = ShapeSample(entryId);
        Assert.Equal(eventType, entry.EventType);

        var data = AuditLogEntryMapper.Map(entry).Fact!.Data!;

        Assert.Equal(accessType, data["groupAccessType"]!.GetValue<string>());
        Assert.Equal(
            BaseKeys.Append("groupAccessType").Order(StringComparer.Ordinal),
            data.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.False(data.ContainsKey("roleIds"));
        Assert.False(data.ContainsKey("calendarEntryId"));
    }

    /// <summary>
    /// The same field, two shapes: the real create's <c>roleIds</c> is an empty array and the
    /// real close's is a null, and only the create carries <c>calendarEntryId</c> at all. Neither
    /// is normalised towards the other. The copy is what VRChat sent, and here VRChat sent two
    /// different things for what looks like one field.
    /// </summary>
    [Fact]
    public void TheCreateAndCloseKeepTheirDifferentRoleIdsShapesInTheCopy()
    {
        var create = Assert.IsType<JsonObject>(
            AuditLogEntryMapper.Map(ShapeSample("gaud_87905023-d926-46dc-a662-5c5d6d1065b9")).Fact!.Data!["auditData"]);
        var close = Assert.IsType<JsonObject>(
            AuditLogEntryMapper.Map(ShapeSample("gaud_78bda451-9973-4592-9e85-666d40f93929")).Fact!.Data!["auditData"]);

        Assert.Empty(Assert.IsType<JsonArray>(create["roleIds"]));
        Assert.True(create.ContainsKey("calendarEntryId"));
        Assert.Null(create["calendarEntryId"]);

        Assert.True(close.ContainsKey("roleIds"));
        Assert.Null(close["roleIds"]);
        Assert.False(close.ContainsKey("calendarEntryId"));
    }

    [Fact]
    public void AnAnnouncementLiftsItsTitleAndMessage()
    {
        var data = AuditLogEntryMapper.Map(ShapeSample("gaud_d44da8cd-2cdb-4f79-b2f4-0c917be6fe03")).Fact!.Data!;

        Assert.Equal("[redacted]", data["title"]!.GetValue<string>());
        Assert.Equal("[redacted]", data["message"]!.GetValue<string>());
        Assert.Equal("[redacted]", data["auditData"]!["message"]!.GetValue<string>());
    }

    /// <summary>
    /// A post's <c>targetId</c> is the notification VRChat sent members, not the post; the post
    /// itself is in <c>auditData</c>. The four stable scalars come up; the rest of the shape
    /// (<c>imageId</c>, <c>roleIds</c>, <c>sendNotification</c>) stays in the copy only. The real
    /// post is group-visible, has an image, and was written by the moderator who logged it.
    /// </summary>
    [Fact]
    public void APostLiftsItsTitleTextAuthorAndVisibility()
    {
        var entry = ShapeSample("gaud_cc537d0f-2b36-4949-9b36-6054a9511174");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;
        var data = fact.Data!;

        Assert.Equal("not_b0f5582e-4a40-47aa-af4b-299702e88428", fact.SubjectId);
        Assert.Equal("[redacted]", data["title"]!.GetValue<string>());
        Assert.Equal("[redacted]", data["text"]!.GetValue<string>());
        Assert.Equal("usr_64dbb478-bcd0-42ad-8b77-ac85b858b9a7", data["authorId"]!.GetValue<string>());
        Assert.Equal(fact.ActorId, data["authorId"]!.GetValue<string>());
        Assert.Equal("group", data["visibility"]!.GetValue<string>());

        Assert.False(data.ContainsKey("imageId"));
        Assert.False(data.ContainsKey("roleIds"));
        Assert.False(data.ContainsKey("sendNotification"));
        Assert.Equal("file_567be062-29ea-47a7-82bc-14146d4f2bb6", data["auditData"]!["imageId"]!.GetValue<string>());
        Assert.True(data["auditData"]!["sendNotification"]!.GetValue<bool>());
    }

    /// <summary>
    /// The event's own <c>description</c> lives inside <c>data</c> and is not lifted, so it never
    /// collides with the entry's <c>description</c> at the top of the payload. The fixture makes
    /// the two distinguishable: one is the redaction placeholder for VRChat's template sentence,
    /// the other is <c>[redacted]</c>.
    /// </summary>
    [Fact]
    public void ACalendarEventLiftsItsTitleTypeAndAccessType()
    {
        var entry = ShapeSample("gaud_9cb8ba60-2a01-4150-8f04-6276decd81b7");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;
        var data = fact.Data!;

        Assert.Equal("cal_36dbfdce-518f-4d32-9ad0-f4cb2b5be8e3", fact.SubjectId);
        Assert.Equal("[redacted]", data["title"]!.GetValue<string>());
        Assert.Equal("event", data["type"]!.GetValue<string>());
        Assert.Equal("public", data["accessType"]!.GetValue<string>());

        Assert.False(data.ContainsKey("imageId"));
        Assert.Equal("file_2fe613a8-8a32-4253-83f8-63d70c80d59d", data["auditData"]!["imageId"]!.GetValue<string>());
        Assert.Equal("[redacted]", data["auditData"]!["description"]!.GetValue<string>());
        Assert.Equal(entry.Description, data["description"]!.GetValue<string>());
        Assert.NotEqual("[redacted]", data["description"]!.GetValue<string>());
    }

    // ── Instance events fill the instance columns ──────────────────────────────────────────

    /// <summary>
    /// The instance the real kick, warn and close all happened in: three different moderators,
    /// one night, one instance. The create, announcement and update are each somewhere else.
    /// </summary>
    private const string KickWarnCloseLocation =
        "wrld_44f4a344-4489-4e11-a54a-86971f16f0e6:93927~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(public)~region(us)";

    /// <summary>
    /// A kick or a warn names the person in <c>targetId</c> and carries the instance in
    /// <c>auditData.location</c> -- 447 live rows, every one the same. The person stays the
    /// subject; the instance goes into the two columns that exist for it.
    /// </summary>
    [Theory]
    [InlineData("gaud_534f534f-f652-42a1-8e88-71d986650528", "group.instance.kick", "usr_45e0370d-c03d-45cc-8aca-5a8dad03c5f0")]
    [InlineData("gaud_2483b0fa-da6f-4437-9de0-7f4041ada59c", "group.instance.warn", "usr_38d4c925-a8df-4bad-825a-551f7e231baf")]
    public void AKickOrWarnTakesTheInstanceFromItsDataAndKeepsThePersonAsTheSubject(
        string entryId, string eventType, string person)
    {
        var entry = ShapeSample(entryId);
        Assert.Equal(eventType, entry.EventType);

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(person, fact.SubjectId);
        Assert.Equal("wrld_44f4a344-4489-4e11-a54a-86971f16f0e6", fact.WorldId);
        Assert.Equal("93927", fact.InstanceId);

        // The location is not lifted into the payload: it is in the copy, and in the columns.
        var data = fact.Data!;
        Assert.Equal(BaseKeys.Order(StringComparer.Ordinal), data.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(KickWarnCloseLocation, data["auditData"]!["location"]!.GetValue<string>());
    }

    /// <summary>
    /// A create, close, announcement or update puts the location in <c>targetId</c>. The subject
    /// stays that raw string, byte for byte -- the columns are filled beside it, not from it.
    /// Four real rows, three different worlds; two of the locations carry a grammar case the
    /// reconstruction never exercised, and each of those has its own test below.
    /// </summary>
    [Theory]
    [InlineData(
        "gaud_87905023-d926-46dc-a662-5c5d6d1065b9", "group.instance.create",
        "wrld_ebca0ab7-7ea8-46e2-aac6-fc5a67f94924:65688~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(plus)~region(us)",
        "wrld_ebca0ab7-7ea8-46e2-aac6-fc5a67f94924", "65688")]
    [InlineData(
        "gaud_78bda451-9973-4592-9e85-666d40f93929", "group.instance.close",
        KickWarnCloseLocation,
        "wrld_44f4a344-4489-4e11-a54a-86971f16f0e6", "93927")]
    [InlineData(
        "gaud_d44da8cd-2cdb-4f79-b2f4-0c917be6fe03", "group.instance.announcement",
        "wrld_71ff0336-d86c-4095-b0f7-e628f1da3a02:63944~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(public)~ageGate~region(us)",
        "wrld_71ff0336-d86c-4095-b0f7-e628f1da3a02", "63944")]
    [InlineData(
        "gaud_15bc3934-d312-46ad-b811-8353054bd82d", "group.instance.update",
        "wrld_3c231334-2f20-442f-924c-a504d9f98543:09595~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(public)~region(use)",
        "wrld_3c231334-2f20-442f-924c-a504d9f98543", "09595")]
    public void ACreateCloseAnnouncementOrUpdateTakesTheInstanceFromItsTargetAndKeepsTheTargetWhole(
        string entryId, string eventType, string location, string worldId, string instanceId)
    {
        var entry = ShapeSample(entryId);
        Assert.Equal(eventType, entry.EventType);

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(location, fact.SubjectId);
        Assert.Equal(location, fact.Data!["targetId"]!.GetValue<string>());
        Assert.Equal(worldId, fact.WorldId);
        Assert.Equal(instanceId, fact.InstanceId);
    }

    /// <summary>
    /// <c>~ageGate</c> is a qualifier with no value and no parentheses (log-format research,
    /// section 1.2), and a parser that expects <c>name(value)</c> of every qualifier loses the
    /// rest of the string at it. The real announcement went to an age-gated instance, so the
    /// fixture carries the case: the split stops at the first <c>~</c> and never reads past it.
    /// </summary>
    [Fact]
    public void AValuelessQualifierInTheLocationDoesNotDisturbTheSplit()
    {
        var entry = ShapeSample("gaud_d44da8cd-2cdb-4f79-b2f4-0c917be6fe03");
        Assert.Contains("~ageGate~", entry.TargetId);

        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("wrld_71ff0336-d86c-4095-b0f7-e628f1da3a02", fact.WorldId);
        Assert.Equal("63944", fact.InstanceId);
        Assert.Equal(entry.TargetId, fact.SubjectId);
    }

    /// <summary>
    /// The real update's instance id is <c>09595</c>. It is text, not a number -- ids are never
    /// validated or normalised (foundation 3.1.1), and the id is whatever the creator set
    /// (log-format research, section 1.3) -- so a reading that arrived at <c>9595</c> would file
    /// this instance's facts under an id VRChat never issued.
    /// </summary>
    [Fact]
    public void ALeadingZeroInTheInstanceIdIsKept()
    {
        var fact = AuditLogEntryMapper.Map(ShapeSample("gaud_15bc3934-d312-46ad-b811-8353054bd82d")).Fact!;

        Assert.Equal("09595", fact.InstanceId);
        Assert.NotEqual("9595", fact.InstanceId);
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
        entry.TargetId = KickWarnCloseLocation;
        entry.Data = $$$"""{"location":"{{{KickWarnCloseLocation}}}"}""";

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
    /// One real row per event type the entries fixture does not already carry, and every one of
    /// them maps under its own name -- so between the two fixtures, every type production has
    /// produced is read by the mapper from a real sample of it.
    /// </summary>
    [Fact]
    public void EveryRecordedShapeMapsToANamedFactType()
    {
        var samples = ShapeSamples();
        Assert.Equal(11, samples.Count);
        Assert.Equal(11, samples.Select(e => e.EventType).Distinct().Count());
        Assert.Empty(samples.Select(e => e.EventType).Intersect(LiveSamples().Select(e => e.EventType)));

        Assert.All(samples, entry =>
        {
            var mapping = AuditLogEntryMapper.Map(entry);

            Assert.True(mapping.Mapped, entry.EventType);
            Assert.Equal(AuditLogRejection.None, mapping.Rejection);
            Assert.NotEqual(FactType.Unrecognised, mapping.Fact!.Type);
        });
    }

    /// <summary>
    /// A rejection arrives with <c>data: {}</c> like the membership events do, and names the
    /// person turned away in <c>targetId</c>. The empty object is kept as one for the same reason
    /// as the others: it is what VRChat sent.
    /// </summary>
    [Fact]
    public void TheRealJoinRequestRejectionMapsWithAnEmptyPayload()
    {
        var entry = ShapeSample("gaud_b25a3980-6f83-4700-8674-092e1515f706");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal("group.request.reject", entry.EventType);
        Assert.Equal(FactType.JoinRequestRejected, fact.Type);
        Assert.Equal("usr_7e3e5b38-024b-47ca-be14-a72b3f239cb3", fact.SubjectId);
        Assert.Equal("usr_2a323be9-ac4e-4502-af07-357d79c48ccf", fact.ActorId);
        Assert.Empty(Assert.IsType<JsonObject>(fact.Data!["auditData"]));
    }

    /// <summary>
    /// The real group update is a banner change: one pair under <c>changed</c>, and the group's
    /// own id as the subject.
    /// </summary>
    [Fact]
    public void TheRealGroupUpdateLiftsItsBannerChange()
    {
        var entry = ShapeSample("gaud_17b03abf-440d-4ecd-8634-68e3017acf51");
        var fact = AuditLogEntryMapper.Map(entry).Fact!;

        Assert.Equal(FactType.GroupInfoChanged, fact.Type);
        Assert.Equal(entry.GroupId, fact.SubjectId);

        var changed = Assert.IsType<JsonObject>(fact.Data!["changed"]);
        Assert.Equal(["bannerId"], changed.Select(p => p.Key));
        Assert.Equal("file_41422f83-4bef-488a-8a6c-7df782dbb2de", changed["bannerId"]!["old"]!.GetValue<string>());
        Assert.Equal("file_41b320e5-2b7d-4aa2-9bc6-32af1cea17df", changed["bannerId"]!["new"]!.GetValue<string>());
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
