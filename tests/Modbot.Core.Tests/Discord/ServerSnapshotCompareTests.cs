using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Discord;

/// <summary>
/// Server snapshots: what changed between two, and what putting one role's or one channel's
/// permissions back would change.
/// </summary>
public class ServerSnapshotCompareTests
{
    private const long ViewChannel = 1L << 10;
    private const long SendMessages = 1L << 11;
    private const long ManageRoles = 1L << 28;

    private static SnapshotRole Role(string id, string name, long permissions = 0, int position = 1, bool everyone = false)
        => new(id, name, 0, position, permissions, false, false, false, everyone);

    private static SnapshotChannel Channel(
        string id, string name, IReadOnlyList<SnapshotOverwrite>? overwrites = null, string? categoryId = null, int position = 0)
        => new(id, name, "text", categoryId, position, false, overwrites ?? []);

    private static ServerSnapshotContent Content(IReadOnlyList<SnapshotRole> roles, IReadOnlyList<SnapshotChannel> channels)
        => new(ServerSnapshotContent.CurrentVersion, "1", "Test", roles, channels);

    private static ServerStructure Live(
        ServerSnapshotContent content, bool canManageRoles = true, long botPermissions = ManageRoles, int botHighest = 10)
        => new(content, canManageRoles, botPermissions, botHighest);

    [Fact]
    public void Compare_TwoSameSnapshots_HasNoChanges()
    {
        var content = Content([Role("r1", "Mod", ViewChannel)], [Channel("c1", "general")]);

        Assert.Empty(ServerSnapshotCompare.Compare(content, content));
    }

    [Fact]
    public void Compare_RoleOnlyInTheSecond_IsAdded()
    {
        var from = Content([], []);
        var to = Content([Role("r1", "Mod")], []);

        var change = Assert.Single(ServerSnapshotCompare.Compare(from, to));

        Assert.Equal(SnapshotChangeKinds.Role, change.Kind);
        Assert.Equal("added", change.What);
        Assert.Equal("Mod", change.Name);
    }

    [Fact]
    public void Compare_ChannelOnlyInTheFirst_IsRemoved()
    {
        var from = Content([], [Channel("c1", "general")]);
        var to = Content([], []);

        var change = Assert.Single(ServerSnapshotCompare.Compare(from, to));

        Assert.Equal(SnapshotChangeKinds.Channel, change.Kind);
        Assert.Equal("removed", change.What);
    }

    [Fact]
    public void Compare_RenamedRole_IsOneChangeNotARemoveAndAnAdd()
    {
        var from = Content([Role("r1", "Mod")], []);
        var to = Content([Role("r1", "Moderator")], []);

        var change = Assert.Single(ServerSnapshotCompare.Compare(from, to));

        Assert.Equal("changed", change.What);
        Assert.Contains("Name: Mod to Moderator", change.Details);
    }

    [Fact]
    public void Compare_RoleGainsAPermission_SaysWhichInWords()
    {
        var from = Content([Role("r1", "Mod", ViewChannel)], []);
        var to = Content([Role("r1", "Mod", ViewChannel | SendMessages)], []);

        var change = Assert.Single(ServerSnapshotCompare.Compare(from, to));

        Assert.Equal(["Permissions gained: Send Messages"], change.Details);
    }

    [Fact]
    public void Compare_ChannelRoleSettingsChange_NamesTheRole()
    {
        var from = Content(
            [Role("r1", "Mod")],
            [Channel("c1", "staff", [new SnapshotOverwrite("r1", ViewChannel, 0)])]);
        var to = Content(
            [Role("r1", "Mod")],
            [Channel("c1", "staff", [new SnapshotOverwrite("r1", 0, ViewChannel)])]);

        var change = Assert.Single(ServerSnapshotCompare.Compare(from, to));

        Assert.Contains("Mod: no longer allowed: View Channel", change.Details);
        Assert.Contains("Mod: now denied: View Channel", change.Details);
    }

    [Fact]
    public void PlanRole_SameAsNow_HasNothingToChange()
    {
        var content = Content([Role("r1", "Mod", ViewChannel)], []);

        var plan = ServerSnapshotCompare.PlanRole(content, Live(content), "r1");

        Assert.True(plan.NothingToChange);
        Assert.Null(plan.Problem);
    }

    [Fact]
    public void PlanRole_PermissionsDiffer_ListsTheExactChange()
    {
        var snapshot = Content([Role("r1", "Mod", ViewChannel)], []);
        var live = Content([Role("r1", "Mod", SendMessages)], []);

        // The bot holds what the role would gain, so it may give it.
        var plan = ServerSnapshotCompare.PlanRole(snapshot, Live(live, botPermissions: ManageRoles | ViewChannel), "r1");

        Assert.Null(plan.Problem);
        Assert.Equal(["Mod gets: View Channel", "Mod loses: Send Messages"], plan.Lines);
        Assert.Equal(ViewChannel, plan.RolePermissions);
        Assert.NotEmpty(plan.Fingerprint);
    }

    [Fact]
    public void PlanRole_FingerprintFollowsTheServer()
    {
        var snapshot = Content([Role("r1", "Mod", ViewChannel)], []);

        var one = ServerSnapshotCompare.PlanRole(snapshot, Live(Content([Role("r1", "Mod", SendMessages)], [])), "r1");
        var again = ServerSnapshotCompare.PlanRole(snapshot, Live(Content([Role("r1", "Mod", SendMessages)], [])), "r1");
        var other = ServerSnapshotCompare.PlanRole(snapshot, Live(Content([Role("r1", "Mod", 0)], [])), "r1");

        Assert.Equal(one.Fingerprint, again.Fingerprint);
        Assert.NotEqual(one.Fingerprint, other.Fingerprint);
    }

    [Fact]
    public void PlanRole_BotWithoutManageRoles_IsAMissingPermission()
    {
        var snapshot = Content([Role("r1", "Mod", ViewChannel)], []);
        var live = Content([Role("r1", "Mod", 0)], []);

        var plan = ServerSnapshotCompare.PlanRole(snapshot, Live(live, canManageRoles: false), "r1");

        Assert.Equal("The bot needs Manage Roles.", plan.Problem);
        Assert.True(plan.MissingPermission);
    }

    [Fact]
    public void PlanRole_RoleAboveTheBotsHighest_IsRefused()
    {
        var snapshot = Content([Role("r1", "Admin", ViewChannel, position: 20)], []);
        var live = Content([Role("r1", "Admin", 0, position: 20)], []);

        var plan = ServerSnapshotCompare.PlanRole(snapshot, Live(live, botHighest: 10), "r1");

        Assert.Equal("The bot can only change roles below its own highest role.", plan.Problem);
    }

    [Fact]
    public void PlanRole_GivingAPermissionTheBotLacks_IsRefused()
    {
        var snapshot = Content([Role("r1", "Mod", SendMessages)], []);
        var live = Content([Role("r1", "Mod", 0)], []);

        var plan = ServerSnapshotCompare.PlanRole(snapshot, Live(live, botPermissions: ManageRoles), "r1");

        Assert.Contains("Send Messages", plan.Problem);
    }

    [Fact]
    public void PlanRole_RoleGoneFromTheServer_IsNotMadeAgain()
    {
        var snapshot = Content([Role("r1", "Mod", ViewChannel)], []);

        var plan = ServerSnapshotCompare.PlanRole(snapshot, Live(Content([], [])), "r1");

        Assert.NotNull(plan.Problem);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void PlanChannel_SetsChangedRolesAndRemovesExtraOnes()
    {
        var roles = new[] { Role("r1", "Mod"), Role("r2", "Guest") };
        var snapshot = Content(roles, [Channel("c1", "staff", [new SnapshotOverwrite("r1", ViewChannel, 0)])]);
        var live = Content(
            roles,
            [Channel("c1", "staff", [new SnapshotOverwrite("r1", 0, 0), new SnapshotOverwrite("r2", 0, ViewChannel)])]);

        // The bot holds the permission the channel setting would allow, so it may set it.
        var plan = ServerSnapshotCompare.PlanChannel(snapshot, Live(live, botPermissions: ManageRoles | ViewChannel), "c1");

        Assert.Null(plan.Problem);
        var set = Assert.Single(plan.Set);
        Assert.Equal("r1", set.RoleId);
        Assert.Equal(ViewChannel, set.Allow);
        Assert.Equal(["r2"], plan.Remove);
        Assert.Contains("Guest: permissions for this role removed", plan.Lines);
    }

    [Fact]
    public void PlanChannel_SameAsNow_HasNothingToChange()
    {
        var content = Content(
            [Role("r1", "Mod")],
            [Channel("c1", "staff", [new SnapshotOverwrite("r1", ViewChannel, 0)])]);

        var plan = ServerSnapshotCompare.PlanChannel(content, Live(content), "c1");

        Assert.True(plan.NothingToChange);
    }

    [Fact]
    public void PlanChannel_RoleGoneFromTheServer_IsLeftAlone()
    {
        var snapshot = Content(
            [Role("r1", "Mod")],
            [Channel("c1", "staff", [new SnapshotOverwrite("r1", ViewChannel, 0)])]);
        var live = Content([], [Channel("c1", "staff")]);

        var plan = ServerSnapshotCompare.PlanChannel(snapshot, Live(live), "c1");

        Assert.Empty(plan.Set);
        Assert.Contains("Mod: left alone, the role is gone from the server", plan.Lines);

        // A line that only says something was left alone is not a change to send.
        Assert.True(plan.NothingToChange);
    }

    [Fact]
    public void PlanChannel_AllowingWhatTheBotLacks_IsRefusedBeforeAnythingIsSent()
    {
        var snapshot = Content([Role("r1", "Mod")], [Channel("c1", "staff", [new SnapshotOverwrite("r1", SendMessages, 0)])]);
        var live = Content([Role("r1", "Mod")], [Channel("c1", "staff")]);

        var plan = ServerSnapshotCompare.PlanChannel(snapshot, Live(live, botPermissions: ManageRoles), "c1");

        Assert.Contains("Send Messages", plan.Problem);
        Assert.True(plan.MissingPermission);
    }

    [Fact]
    public void PlanChannel_AllowingWhatTheBotHas_IsNotRefused()
    {
        var snapshot = Content([Role("r1", "Mod")], [Channel("c1", "staff", [new SnapshotOverwrite("r1", SendMessages, 0)])]);
        var live = Content([Role("r1", "Mod")], [Channel("c1", "staff")]);

        var plan = ServerSnapshotCompare.PlanChannel(snapshot, Live(live, botPermissions: ManageRoles | SendMessages), "c1");

        Assert.Null(plan.Problem);
        // The channel has no setting for the role now, so it is set again rather than changed.
        Assert.Equal(["Mod: permissions set again (allows Send Messages)"], plan.Lines);
    }

    [Fact]
    public void PlanChannel_BotWithoutManageRoles_IsAMissingPermission()
    {
        var snapshot = Content([Role("r1", "Mod")], [Channel("c1", "staff", [new SnapshotOverwrite("r1", ViewChannel, 0)])]);
        var live = Content([Role("r1", "Mod")], [Channel("c1", "staff")]);

        var plan = ServerSnapshotCompare.PlanChannel(snapshot, Live(live, canManageRoles: false), "c1");

        Assert.Equal("The bot needs Manage Roles.", plan.Problem);
        Assert.True(plan.MissingPermission);
    }

    [Fact]
    public void PlanChannel_ChannelGoneFromTheServer_IsNotMadeAgain()
    {
        var snapshot = Content([], [Channel("c1", "staff")]);

        var plan = ServerSnapshotCompare.PlanChannel(snapshot, Live(Content([], [])), "c1");

        Assert.NotNull(plan.Problem);
    }

    [Fact]
    public void Snapshot_SurvivesItsJson()
    {
        var content = Content(
            [Role("r1", "Mod", ViewChannel)],
            [Channel("c1", "staff", [new SnapshotOverwrite("r1", ViewChannel, SendMessages)])]);

        var back = ServerSnapshotContent.FromJson(content.ToJson());

        Assert.NotNull(back);
        Assert.Empty(ServerSnapshotCompare.Compare(content, back));
    }

    [Fact]
    public void Snapshot_FromTextThatIsNotOne_IsNull()
    {
        Assert.Null(ServerSnapshotContent.FromJson("not json"));
    }
}
