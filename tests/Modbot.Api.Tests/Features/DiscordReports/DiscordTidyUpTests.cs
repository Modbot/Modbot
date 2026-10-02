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
        string type = DiscordChannelTypes.Text)
        => new(id, "#" + id, type, null, position, everyoneCanView, canRead, stillReading, last);

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
        ], hideStaffOnly: false);

        Assert.Equal(["never", "five-years", "three-months", "busy", "reading", "unreadable"], rows.Select(r => r.Id));
    }

    [Fact]
    public void AStillReadingChannel_WithAMessageFound_IsPlacedByThatMessage()
    {
        var rows = DiscordTidyUp.Channels(
        [
            Channel("newer", Now.AddDays(-1)),
            Channel("reading", Now.AddDays(-30), stillReading: true),
        ], hideStaffOnly: false);

        Assert.Equal(["reading", "newer"], rows.Select(r => r.Id));
        Assert.Equal(Now.AddDays(-30), rows[0].LastMessageAt);
    }

    [Fact]
    public void AChannelTheBotCannotRead_GivesNoLastMessage()
    {
        var rows = DiscordTidyUp.Channels([Channel("lost", Now.AddDays(-90), canRead: false)], hideStaffOnly: false);

        Assert.False(rows[0].CanRead);
        Assert.Null(rows[0].LastMessageAt);
    }

    [Fact]
    public void StaffOnlyChannels_AreMarked_AndLeftOutOnlyWhenAsked()
    {
        IReadOnlyList<DiscordTidyUp.ChannelFacts> channels =
        [
            Channel("public", Now.AddDays(-1)),
            Channel("staff", Now.AddDays(-2), everyoneCanView: false),
            Channel("not-read-yet", Now.AddDays(-3), everyoneCanView: null),
        ];

        var all = DiscordTidyUp.Channels(channels, hideStaffOnly: false);
        Assert.Equal(["not-read-yet", "staff", "public"], all.Select(r => r.Id));
        Assert.True(all.Single(r => r.Id == "staff").StaffOnly);
        Assert.False(all.Single(r => r.Id == "not-read-yet").StaffOnly);

        var shown = DiscordTidyUp.Channels(channels, hideStaffOnly: true);
        Assert.Equal(["not-read-yet", "public"], shown.Select(r => r.Id));
    }

    [Fact]
    public void ChannelsQuietForTheSameTime_KeepDiscordsOrder()
    {
        var rows = DiscordTidyUp.Channels(
        [
            Channel("second", position: 2),
            Channel("first", position: 1),
        ], hideStaffOnly: false);

        Assert.Equal(["first", "second"], rows.Select(r => r.Id));
    }
}
