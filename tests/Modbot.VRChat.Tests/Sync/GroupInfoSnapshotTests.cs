using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Change detection, which is the whole reason this producer is allowed to write facts at all.
/// </summary>
/// <remarks>
/// The research on VRChat's logs (§4.0) found that 82% of the lines that looked like avatar
/// changes were restatements of what was already true, and that recording them naively inflates
/// the count of a thing by five times, silently. A group-info fact written on every poll would be
/// the same mistake: 288 rows a day saying nothing happened, and every "how often does this
/// change" answer wrong by two orders of magnitude.
/// </remarks>
public class GroupInfoSnapshotTests
{
    [Fact]
    public void APollThatSawNoChangeReportsNoChange()
    {
        var snapshot = GroupInfoSnapshot.From(Group());

        Assert.Empty(snapshot.DifferencesFrom(GroupInfoSnapshot.From(Group())));
    }

    /// <summary>
    /// The trap that would have made this producer useless. A record's generated equality
    /// compares a list by reference, so two roles deserialised from identical JSON compare
    /// unequal -- and the snapshot is stored as JSON and read back on every poll. The symptom
    /// would be a group that appears to change its roles every five minutes forever.
    /// </summary>
    [Fact]
    public void ASnapshotThatHasBeenThroughJsonStillComparesEqual()
    {
        var snapshot = GroupInfoSnapshot.From(Group());
        var restored = GroupInfoSnapshot.Parse(snapshot.ToJson());

        Assert.NotNull(restored);
        Assert.Empty(snapshot.DifferencesFrom(restored));
    }

    [Fact]
    public void RolesArrivingInADifferentOrderAreNotAChange()
    {
        var group = Group();
        var reordered = Group();
        reordered.Roles!.Reverse();

        Assert.Empty(GroupInfoSnapshot.From(group).DifferencesFrom(GroupInfoSnapshot.From(reordered)));
    }

    [Fact]
    public void PermissionsArrivingInADifferentOrderAreNotAChange()
    {
        var group = Group();
        var reordered = Group();
        reordered.Roles![0].Permissions!.Reverse();

        Assert.Empty(GroupInfoSnapshot.From(group).DifferencesFrom(GroupInfoSnapshot.From(reordered)));
    }

    [Fact]
    public void ARenamedRoleIsAChange()
    {
        var previous = GroupInfoSnapshot.From(Group());

        var group = Group();
        group.Roles![0].Name = "Senior Moderator";

        Assert.Equal(["Roles"], GroupInfoSnapshot.From(group).DifferencesFrom(previous));
    }

    [Fact]
    public void ANewRoleIsAChange()
    {
        var previous = GroupInfoSnapshot.From(Group());

        var group = Group();
        group.Roles!.Add(new GroupRole { Id = "grol_3", Name = "Helper", Permissions = [] });

        Assert.Equal(["Roles"], GroupInfoSnapshot.From(group).DifferencesFrom(previous));
    }

    [Fact]
    public void ChangedFieldsAreNamedIndividually()
    {
        var previous = GroupInfoSnapshot.From(Group());

        var group = Group();
        group.MemberCount = 8124;
        group.Description = "now with more rules";

        var changed = GroupInfoSnapshot.From(group).DifferencesFrom(previous);

        Assert.Contains("MemberCount", changed);
        Assert.Contains("Description", changed);
        Assert.DoesNotContain("Roles", changed);
    }

    /// <summary>
    /// Only the fields that moved. A group whose member count ticks every five minutes would
    /// otherwise carry its whole role list into the fact log 288 times a day to record one
    /// integer.
    /// </summary>
    [Fact]
    public void TheChangePayloadCarriesOnlyWhatChangedWithBothValues()
    {
        var previous = GroupInfoSnapshot.From(Group());

        var group = Group();
        group.MemberCount = 8124;

        var current = GroupInfoSnapshot.From(group);
        var changed = current.DifferencesFrom(previous);
        var payload = current.ChangePayload(previous, changed);

        var fields = payload["changed"]!.AsObject();

        Assert.Single(fields);
        Assert.Equal(8123, fields["MemberCount"]!["old"]!.GetValue<int>());
        Assert.Equal(8124, fields["MemberCount"]!["new"]!.GetValue<int>());
    }

    /// <summary>
    /// The headcount the member series is counted forward from. <c>members.total</c> is the net of
    /// recorded joins and leaves, so without this a group that installs Modbot with 8,000 members
    /// watches its own chart start at zero.
    /// </summary>
    [Fact]
    public void TheBaselinePayloadCarriesTheWholeSnapshot()
    {
        var payload = GroupInfoSnapshot.From(Group()).BaselinePayload();
        var baseline = payload["baseline"]!;

        Assert.Equal(8123, baseline["MemberCount"]!.GetValue<int>());
        Assert.Equal("Test Group", baseline["Name"]!.GetValue<string>());
        Assert.Equal(2, baseline["Roles"]!.AsArray().Count);
    }

    /// <summary>
    /// Stored JSON that will not parse is treated as "never seen", which costs one baseline fact.
    /// Throwing would stop group-info sync permanently over a field shape nobody has looked at.
    /// </summary>
    [Fact]
    public void AnUnreadableStoredSnapshotIsTreatedAsNoSnapshot()
    {
        Assert.Null(GroupInfoSnapshot.Parse("{ this is not json"));
        Assert.Null(GroupInfoSnapshot.Parse(null));
        Assert.Null(GroupInfoSnapshot.Parse("  "));
    }

    internal static Group Group() => new()
    {
        Id = "grp_test",
        Name = "Test Group",
        ShortCode = "TEST",
        Discriminator = "1234",
        Description = "a group",
        Rules = "be nice",
        OwnerId = "usr_owner",
        JoinState = GroupJoinState.Open,
        Privacy = GroupPrivacy.Default,
        MemberCount = 8123,
        OnlineMemberCount = 12,
        Roles =
        [
            new GroupRole
            {
                Id = "grol_1",
                Name = "Moderator",
                Order = 1,
                IsManagementRole = true,
                Permissions = [GroupPermissions.group_members_manage, GroupPermissions.group_bans_manage],
            },
            new GroupRole
            {
                Id = "grol_2",
                Name = "Member",
                Order = 2,
                DefaultRole = true,
                Permissions = [],
            },
        ],
    };
}
