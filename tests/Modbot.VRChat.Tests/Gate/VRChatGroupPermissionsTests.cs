using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Turning VRChat's "Forbidden" into the group permission Modbot's own VRChat account lacks.
/// </summary>
/// <remarks>
/// A moderator can do nothing with "Forbidden". What they can act on is the permission's name, the
/// roles the account has, and where roles are changed. Getting it wrong the other way is as bad:
/// a 403 that is not about a permission must not send somebody to edit roles.
/// </remarks>
public class VRChatGroupPermissionsTests
{
    private const string Group = "grp_1";

    [Theory]
    [InlineData("RespondGroupJoinRequest", "group-invites-manage")]
    [InlineData("CreateGroupInvite", "group-invites-manage")]
    [InlineData("KickGroupMember", "group-members-remove")]
    [InlineData("BanGroupMember", "group-bans-manage")]
    [InlineData("UnbanGroupMember", "group-bans-manage")]
    [InlineData("GetGroupBans", "group-bans-manage")]
    [InlineData("AddGroupMemberRole", "group-roles-assign")]
    [InlineData("RemoveGroupMemberRole", "group-roles-assign")]
    [InlineData("CreateGroupCalendarEvent", "group-calendar-manage")]
    [InlineData("UpdateGroupCalendarEvent", "group-calendar-manage")]
    [InlineData("DeleteGroupCalendarEvent", "group-calendar-manage")]
    public void EachGroupActionNamesThePermissionItNeeds(string operation, string permission)
        => Assert.Equal(permission, VRChatGroupPermissions.NeededFor(operation));

    [Theory]
    [InlineData("GetGroupRequests")]
    [InlineData("GetGroupInstances")]
    [InlineData("GetGroup")]
    [InlineData(null)]
    public void AnActionNoSourceSettlesNamesNone(string? operation)
        => Assert.Null(VRChatGroupPermissions.NeededFor(operation));

    [Fact]
    public void A403NamesThePermission_TheRoles_AndVRChatsOwnWords()
    {
        var settings = Account(roleIds: ["grol_mod"], permissions: ["group-audit-view"]);

        var missing = VRChatGroupPermissions.Refusal(
            403,
            VRChatFailureKind.Other,
            "RespondGroupJoinRequest",
            Group,
            """{"error":{"message":"You can't do that","status_code":403}}""",
            settings);

        Assert.NotNull(missing);
        Assert.Equal("group-invites-manage", missing.Permission);
        Assert.Equal(Group, missing.GroupId);
        Assert.Equal(["Moderator"], missing.Roles);
        Assert.Equal("You can't do that", missing.Said);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public void OnlyA403IsAMissingPermission(int status)
        => Assert.Null(VRChatGroupPermissions.Refusal(
            status, VRChatFailureKind.Other, "KickGroupMember", Group, null, Account(null, null)));

    [Fact]
    public void ACloudflareBlockIsNotAMissingPermission()
        => Assert.Null(VRChatGroupPermissions.Refusal(
            403, VRChatFailureKind.WafBlocked, "KickGroupMember", Group, "<html>blocked</html>", Account(null, null)));

    [Fact]
    public void A403WhenTheAccountHoldsThePermissionIsAboutSomethingElse()
    {
        // The account has Remove Group Members, so a refused kick is about the person (ranked
        // above it, say), and VRChat's own words are what gets shown.
        var settings = Account(roleIds: ["grol_mod"], permissions: ["group-members-remove"]);

        Assert.Null(VRChatGroupPermissions.Refusal(
            403, VRChatFailureKind.Other, "KickGroupMember", Group, null, settings));
    }

    [Fact]
    public void TheOwnersEveryPermissionCoversAnything()
    {
        var settings = Account(roleIds: ["grol_owner"], permissions: ["*"]);

        Assert.Null(VRChatGroupPermissions.Refusal(
            403, VRChatFailureKind.Other, "BanGroupMember", Group, null, settings));
    }

    [Fact]
    public void BeforeTheAccountHasBeenRead_A403IsStillReported_WithNoRoles()
    {
        var missing = VRChatGroupPermissions.Refusal(
            403, VRChatFailureKind.Other, "BanGroupMember", Group, null, Account(null, null));

        Assert.NotNull(missing);
        Assert.Equal("group-bans-manage", missing.Permission);
        Assert.Null(missing.Roles);
        Assert.Null(missing.Said);
    }

    [Fact]
    public void AnUnmappedActionIsReportedWithoutGuessingThePermission()
    {
        var missing = VRChatGroupPermissions.Refusal(
            403, VRChatFailureKind.Other, "GetGroupRequests", Group, null, Account(["grol_mod"], ["group-audit-view"]));

        Assert.NotNull(missing);
        Assert.Null(missing.Permission);
        Assert.Equal("Modbot's VRChat account needs a group permission in this group.", VRChatGroupPermissions.Sentence(missing));
    }

    [Fact]
    public void TheSentenceUsesVRChatsOwnLabel()
        => Assert.Equal(
            "Modbot's VRChat account needs Manage Group Invites in this group.",
            VRChatGroupPermissions.Sentence(new MissingGroupPermission("group-invites-manage", Group, null, null)));

    [Fact]
    public void ARoleTheStoredListDoesNotNameIsLeftOut()
        => Assert.Equal(["Moderator"], VRChatGroupPermissions.RoleNames(Account(["grol_mod", "grol_gone"], [])));

    [Fact]
    public void TheRolesPageIsVRChatsOwn()
        => Assert.Equal(
            "https://vrchat.com/home/group/grp_1/settings/roles",
            VRChatGroupPermissions.RolesPage(Group));

    [Fact]
    public void MissingOfListsWhatModbotUsesAndTheAccountLacks()
    {
        Assert.Null(VRChatGroupPermissions.MissingOf(null));
        Assert.Empty(VRChatGroupPermissions.MissingOf(["*"])!);

        var missing = VRChatGroupPermissions.MissingOf(
            ["group-members-viewall", "group-audit-view", "group-bans-manage", "group-members-remove"]);

        Assert.Equal(["group-invites-manage"], missing);
    }

    [Fact]
    public void TheAccountIsReadFromMyMember_IncludingPermissionsTheSdkCannotName()
    {
        const string group = """
            {
              "id": "grp_1",
              "myMember": {
                "roleIds": ["grol_b", "grol_a", "grol_a"],
                "permissions": ["group-calendar-manage", "group-bans-manage"]
              }
            }
            """;

        var (roleIds, permissions) = VRChatGroupPermissions.AccountFrom(group);

        Assert.Equal(["grol_a", "grol_b"], roleIds);
        Assert.Equal(["group-bans-manage", "group-calendar-manage"], permissions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"id":"grp_1"}""")]
    [InlineData("not json")]
    public void AnAnswerWithNoMyMemberReadsAsNothing(string? group)
    {
        var (roleIds, permissions) = VRChatGroupPermissions.AccountFrom(group);

        Assert.Null(roleIds);
        Assert.Null(permissions);
    }

    [Fact]
    public void TheGroupInfoPollKeepsWhatItHadWhenMyMemberIsMissing()
    {
        var settings = Account(["grol_mod"], ["group-audit-view"]);

        GroupInfoSync.RecordAccount(settings, """{"id":"grp_1"}""");
        Assert.Equal(["grol_mod"], settings.VRChatAccountRoleIds);

        GroupInfoSync.RecordAccount(settings, """{"myMember":{"roleIds":["grol_x"],"permissions":["*"]}}""");
        Assert.Equal(["grol_x"], settings.VRChatAccountRoleIds);
        Assert.Equal(["*"], settings.VRChatAccountPermissions);
    }

    private static Settings Account(List<string>? roleIds, List<string>? permissions) => new()
    {
        ManagedGroupId = Group,
        VRChatAccountRoleIds = roleIds,
        VRChatAccountPermissions = permissions,
        GroupInfoSnapshot = new GroupInfoSnapshot(
            "Group", null, null, null, null, null, null, null, false, 1, 0,
            [new GroupRoleSnapshot("grol_mod", "Moderator", null, 1, true, false, false, false, [])]).ToJson(),
    };
}
