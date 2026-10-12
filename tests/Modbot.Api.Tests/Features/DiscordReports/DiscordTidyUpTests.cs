using Modbot.Api.Features.DiscordReports;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.DiscordReports;

/// <summary>
/// The rules behind the Discord page's Roles and Channels tabs, without a database: what a role is
/// marked with, the order both lists come in, and leaving staff-only channels out (Discord tidy-up
/// design).
/// </summary>
public class DiscordTidyUpTests
{
    private const long Everyone = 0x6_4000;
    private const long Staff = 0x2_0000_0006;

    private static DiscordTidyUp.RoleFacts Role(
        string id, string name, int position = 1, int color = 0, bool managed = false, long? permissions = Everyone)
        => new(id, name, color, position, managed, permissions);

    private static IReadOnlyList<RoleReportRow> Report(
        IReadOnlyList<DiscordTidyUp.RoleFacts> roles, Dictionary<string, int>? members = null)
        => DiscordTidyUp.Roles(roles, Everyone, members ?? roles.ToDictionary(r => r.Id, _ => 5));

    private static RoleReportRow Row(IReadOnlyList<RoleReportRow> rows, string id) => rows.Single(r => r.Id == id);

    // ── Marks ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARoleNobodyHolds_IsMarkedNoMembers()
    {
        var rows = Report([Role("1", "Empty"), Role("2", "Held")], new() { ["2"] = 3 });

        Assert.Equal([RoleFlags.NoMembers], Row(rows, "1").Flags);
        Assert.Equal(0, Row(rows, "1").Members);
        Assert.Empty(Row(rows, "2").Flags);
    }

    [Fact]
    public void BeforeTheMemberListIsRead_NoCountIsGiven_AndNothingIsMarkedNoMembers()
    {
        var rows = DiscordTidyUp.Roles([Role("1", "Empty")], Everyone, members: null);

        Assert.Null(rows[0].Members);
        Assert.Empty(rows[0].Flags);
    }

    [Fact]
    public void TwoRolesWithOneName_IgnoringCaseAndSpaces_AreBothMarked()
    {
        var rows = Report([Role("1", "Zuelbot"), Role("2", " ZuelBot "), Role("3", "Other")]);

        Assert.Contains(RoleFlags.SameName, Row(rows, "1").Flags);
        Assert.Contains(RoleFlags.SameName, Row(rows, "2").Flags);
        Assert.DoesNotContain(RoleFlags.SameName, Row(rows, "3").Flags);
    }

    [Fact]
    public void ABotOrIntegrationRole_IsMarkedBotRole()
    {
        var rows = Report([Role("1", "Ticket Tool", managed: true), Role("2", "Member")]);

        Assert.Equal([RoleFlags.BotRole], Row(rows, "1").Flags);
        Assert.Empty(Row(rows, "2").Flags);
    }

    [Fact]
    public void SamePermissionsAndColour_MarksBoth_AndNamesTheOthers_HighestFirst()
    {
        var rows = Report(
        [
            Role("1", "Investor", position: 3, color: 0xF1C40F, permissions: Staff),
            Role("2", "Ko-fi Investor", position: 5, color: 0xF1C40F, permissions: Staff),
            Role("3", "Dragon", position: 4, color: 0xF1C40F, permissions: Staff),
            Role("4", "Gold, other powers", position: 2, color: 0xF1C40F, permissions: Everyone),
        ]);

        Assert.Equal([RoleFlags.SamePermissionsAndColour], Row(rows, "1").Flags);
        Assert.Equal(["Ko-fi Investor", "Dragon"], Row(rows, "1").SamePermissionsAndColourAs);
        Assert.Equal(["Dragon", "Investor"], Row(rows, "2").SamePermissionsAndColourAs);

        // The same colour alone is not enough.
        Assert.Empty(Row(rows, "4").Flags);
        Assert.Empty(Row(rows, "4").SamePermissionsAndColourAs);
    }

    [Fact]
    public void PlainRoles_AreNeverMarkedAsTheSame()
    {
        // Pronoun and hobby roles: no colour, and nothing @everyone does not already have.
        var rows = Report(
        [
            Role("1", "she/her"),
            Role("2", "he/him"),
            Role("3", "Hiking", permissions: 0),
            Role("4", "Fishing", permissions: 0),
        ]);

        Assert.All(rows, r => Assert.Empty(r.Flags));
    }

    [Fact]
    public void RolesWithNoColour_ButPowersOfTheirOwn_AreStillCompared()
    {
        var rows = Report([Role("1", "Mod", permissions: Staff), Role("2", "Moderator", permissions: Staff)]);

        Assert.Equal([RoleFlags.SamePermissionsAndColour], Row(rows, "1").Flags);
        Assert.Equal([RoleFlags.SamePermissionsAndColour], Row(rows, "2").Flags);
    }

    [Fact]
    public void BotRoles_AndRolesNotReadYet_AreLeftOutOfSamePermissionsAndColour()
    {
        var rows = Report(
        [
            Role("1", "Bot A", managed: true, permissions: Staff),
            Role("2", "Bot B", managed: true, permissions: Staff),
            Role("3", "Unread", color: 0xFF0000, permissions: null),
            Role("4", "Unread too", color: 0xFF0000, permissions: null),
        ]);

        Assert.Equal([RoleFlags.BotRole], Row(rows, "1").Flags);
        Assert.Equal([RoleFlags.BotRole], Row(rows, "2").Flags);
        Assert.Empty(Row(rows, "3").Flags);
        Assert.Empty(Row(rows, "4").Flags);
    }

    [Fact]
    public void ARolesMarks_ComeInOneOrder()
    {
        var rows = Report(
        [
            Role("1", "Twin", color: 0x00FF00, permissions: Staff),
            Role("2", "twin", color: 0x00FF00, permissions: Staff),
            Role("3", "Twin", managed: true),
        ], new() { ["2"] = 4, ["3"] = 1 });

        Assert.Equal([RoleFlags.NoMembers, RoleFlags.SameName, RoleFlags.SamePermissionsAndColour], Row(rows, "1").Flags);
        Assert.Equal([RoleFlags.SameName, RoleFlags.BotRole], Row(rows, "3").Flags);
    }

    // ── Order ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Roles_AreMostMarksFirst_ThenFewestMembers_ThenDiscordsOrder()
    {
        var rows = Report(
        [
            Role("plain-big", "Villager", position: 9),
            Role("plain-small", "Artist", position: 8),
            Role("bot", "Carl", position: 7, managed: true),
            Role("empty", "UTC-12", position: 2),
            Role("empty-bot", "Old bot", position: 6, managed: true),
            Role("plain-tie-high", "Tie high", position: 5),
            Role("plain-tie-low", "Tie low", position: 4),
        ], new()
        {
            ["plain-big"] = 700,
            ["plain-small"] = 3,
            ["bot"] = 1,
            ["plain-tie-high"] = 10,
            ["plain-tie-low"] = 10,
        });

        Assert.Equal(
            ["empty-bot", "empty", "bot", "plain-small", "plain-tie-high", "plain-tie-low", "plain-big"],
            rows.Select(r => r.Id));
    }

    // ── Quiet channels ──────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static DiscordTidyUp.ChannelFacts Channel(
        string id,
        DateTimeOffset? last = null,
        bool canRead = true,
        bool stillReading = false,
        bool? everyoneCanView = true,
        int position = 0,
        string type = DiscordChannelTypes.Text,
        ChannelViewOverwrites? overwrites = null,
        string? categoryId = null)
        => new(id, "#" + id, type, categoryId, categoryId is null ? null : "category " + categoryId, position, everyoneCanView, overwrites, canRead, stillReading, last);

    [Fact]
    public void Channels_AreQuietestFirst()
    {
        var rows = DiscordTidyUp.Channels(
        [
            Channel("busy", Now.AddMinutes(-5)),
            Channel("unreadable", canRead: false),
            Channel("five-years", Now.AddYears(-5)),
            Channel("reading", stillReading: true),
            Channel("never"),
            Channel("three-months", Now.AddMonths(-3), type: DiscordChannelTypes.Forum),
        ]);

        Assert.Equal(["never", "five-years", "three-months", "busy", "reading", "unreadable"], rows.Select(r => r.Id));
    }

    [Fact]
    public void AStillReadingChannel_WithAMessageFound_IsPlacedByThatMessage()
    {
        var rows = DiscordTidyUp.Channels(
        [
            Channel("newer", Now.AddDays(-1)),
            Channel("reading", Now.AddDays(-30), stillReading: true),
        ]);

        Assert.Equal(["reading", "newer"], rows.Select(r => r.Id));
        Assert.Equal(Now.AddDays(-30), rows[0].LastMessageAt);
    }

    [Fact]
    public void AChannelTheBotCannotRead_GivesNoLastMessage()
    {
        var rows = DiscordTidyUp.Channels([Channel("lost", Now.AddDays(-90), canRead: false)]);

        Assert.False(rows[0].CanRead);
        Assert.Null(rows[0].LastMessageAt);
    }

    [Fact]
    public void StaffOnlyChannels_AreMarked()
    {
        var all = DiscordTidyUp.Channels(
        [
            Channel("public", Now.AddDays(-1)),
            Channel("staff", Now.AddDays(-2), everyoneCanView: false),
            Channel("not-read-yet", Now.AddDays(-3), everyoneCanView: null),
        ]);

        Assert.Equal(["not-read-yet", "staff", "public"], all.Select(r => r.Id));
        Assert.True(all.Single(r => r.Id == "staff").StaffOnly);
        Assert.False(all.Single(r => r.Id == "not-read-yet").StaffOnly);
    }

    [Fact]
    public void AChannel_CarriesItsCategory()
    {
        var row = DiscordTidyUp.Channels([Channel("a", categoryId: "9")]).Single();

        Assert.Equal("9", row.CategoryId);
        Assert.Equal("category 9", row.CategoryName);
    }

    // ── Seen by a role ──────────────────────────────────────────────────────────────────────────

    // The server's id is @everyone's role id.
    private const string EveryoneId = "100";
    private const string Mods = "200";
    private const string Members = "300";

    private const long Nothing = 0;
    private const long CanViewChannels = DiscordTidyUp.ViewChannels;

    private static ChannelViewOverwrites Overwrites(string[]? allow = null, string[]? deny = null)
        => new(allow ?? [], deny ?? []);

    [Fact]
    public void ARole_SeesWhatEveryoneSeesServerWide_UntilTheChannelSaysOtherwise()
    {
        Assert.True(DiscordTidyUp.RoleCanView(Members, Nothing, EveryoneId, CanViewChannels, Overwrites()));
        Assert.False(DiscordTidyUp.RoleCanView(Members, Nothing, EveryoneId, Nothing, Overwrites()));
    }

    [Fact]
    public void ARoleWithViewChannelsServerWide_IsStillHiddenByEveryonesDeny()
    {
        // Discord applies the channel's @everyone overwrite to the combined permissions of
        // @everyone and the role, so having the permission server-wide does not put the role back.
        Assert.False(DiscordTidyUp.RoleCanView(Members, CanViewChannels, EveryoneId, CanViewChannels, Overwrites(deny: [EveryoneId])));
    }

    [Fact]
    public void ARolesOwnOverwrite_BeatsEveryonesOverwrite()
    {
        Assert.True(DiscordTidyUp.RoleCanView(Mods, Nothing, EveryoneId, CanViewChannels, Overwrites(allow: [Mods], deny: [EveryoneId])));
        Assert.False(DiscordTidyUp.RoleCanView(Mods, CanViewChannels, EveryoneId, CanViewChannels, Overwrites(allow: [EveryoneId], deny: [Mods])));
    }

    [Fact]
    public void AnOverwriteForAnotherRole_ChangesNothing()
    {
        Assert.False(DiscordTidyUp.RoleCanView(Members, Nothing, EveryoneId, Nothing, Overwrites(allow: [Mods])));
        Assert.True(DiscordTidyUp.RoleCanView(Members, Nothing, EveryoneId, CanViewChannels, Overwrites(deny: [Mods])));
    }

    [Fact]
    public void AnAdministratorRole_SeesEverything()
    {
        Assert.True(DiscordTidyUp.RoleCanView(Mods, DiscordTidyUp.Administrator, EveryoneId, Nothing, Overwrites(deny: [EveryoneId, Mods])));
    }

    [Fact]
    public void WhatIsNotReadYet_IsNotGuessed()
    {
        Assert.Null(DiscordTidyUp.RoleCanView(Mods, null, EveryoneId, CanViewChannels, Overwrites()));
        Assert.Null(DiscordTidyUp.RoleCanView(Mods, CanViewChannels, EveryoneId, null, Overwrites()));
        Assert.Null(DiscordTidyUp.RoleCanView(Mods, CanViewChannels, EveryoneId, CanViewChannels, null));
    }

    private static readonly Dictionary<string, long?> RolePermissions = new()
    {
        [EveryoneId] = CanViewChannels,
        [Mods] = Nothing,
        [Members] = Nothing,
        ["unread"] = null,
    };

    private static IReadOnlyList<DiscordTidyUp.ChannelFacts> ChannelsBySight() =>
    [
        Channel("public", overwrites: Overwrites()),
        Channel("mods-only", overwrites: Overwrites(allow: [Mods], deny: [EveryoneId])),
        Channel("members-only", overwrites: Overwrites(allow: [Members], deny: [EveryoneId])),
        Channel("not-read-yet"),
    ];

    private static IEnumerable<string> SeenBy(string[] seenBy, string[] notSeenBy)
        => DiscordTidyUp.SeenBy(ChannelsBySight(), RolePermissions, EveryoneId, seenBy, notSeenBy).Select(c => c.Id);

    [Fact]
    public void SeenBy_KeepsTheChannelsAnyOfTheRolesCanSee()
    {
        Assert.Equal(["public", "mods-only"], SeenBy([Mods], []));
        Assert.Equal(["public", "mods-only", "members-only"], SeenBy([Mods, Members], []));
    }

    [Fact]
    public void NotSeenBy_KeepsTheChannelsNoneOfTheRolesCanSee()
    {
        Assert.Equal(["members-only"], SeenBy([], [Mods]));
        Assert.Empty(SeenBy([], [Mods, Members]));
    }

    [Fact]
    public void SeenBy_WithNothingAsked_ChangesNothing()
    {
        Assert.Equal(["public", "mods-only", "members-only", "not-read-yet"], SeenBy([], []));
    }

    [Fact]
    public void SeenBy_LeavesOutWhatIsNotKnown()
    {
        // "not-read-yet" has no overwrites, so it is neither seen nor not seen by anybody.
        Assert.DoesNotContain("not-read-yet", SeenBy([Mods], []));
        Assert.DoesNotContain("not-read-yet", SeenBy([], [Mods]));

        // A role whose permissions are not read yet, or that is not in the server, matches nothing.
        Assert.Empty(SeenBy(["unread"], []));
        Assert.Empty(SeenBy(["gone"], []));
        Assert.Empty(SeenBy([], ["unread"]));
    }

    [Fact]
    public void ViewOverwrites_AreKeptInOneFixedOrder_AndReadBack()
    {
        var a = new ChannelViewOverwrites(["2", "1"], ["3"]);
        var b = new ChannelViewOverwrites(["1", "2"], ["3"]);

        Assert.Equal(a.ToJson(), b.ToJson());

        var back = ChannelViewOverwrites.FromJson(a.ToJson());
        Assert.NotNull(back);
        Assert.Equal(["1", "2"], back.Allow);
        Assert.Equal(["3"], back.Deny);

        Assert.Null(ChannelViewOverwrites.FromJson(null));
        Assert.Null(ChannelViewOverwrites.FromJson("not json"));
    }

    [Fact]
    public void ChannelsQuietForTheSameTime_KeepDiscordsOrder()
    {
        var rows = DiscordTidyUp.Channels(
        [
            Channel("second", position: 2),
            Channel("first", position: 1),
        ]);

        Assert.Equal(["first", "second"], rows.Select(r => r.Id));
    }
}
