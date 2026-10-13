using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Discord;

/// <summary>
/// What counts as a birthday and when it is celebrated: real dates only, 29 February on 28 February in
/// a year with no 29th, and the post's text with the members put in.
/// </summary>
public class BirthdayRulesTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(12, 31, true)]
    [InlineData(2, 29, true)]
    [InlineData(2, 30, false)]
    [InlineData(4, 31, false)]
    [InlineData(0, 10, false)]
    [InlineData(13, 1, false)]
    [InlineData(5, 0, false)]
    [InlineData(5, 32, false)]
    [InlineData(-1, 5, false)]
    public void IsDate_AcceptsOnlyDatesThatExist_And29FebruaryCounts(int month, int day, bool expected)
        => Assert.Equal(expected, BirthdayRules.IsDate(month, day));

    [Fact]
    public void FallsOn_AnOrdinaryDay_IsThatDayInAnyYear()
    {
        Assert.True(BirthdayRules.FallsOn(3, 14, new DateOnly(2026, 3, 14)));
        Assert.True(BirthdayRules.FallsOn(3, 14, new DateOnly(2028, 3, 14)));
        Assert.False(BirthdayRules.FallsOn(3, 14, new DateOnly(2026, 3, 15)));
        Assert.False(BirthdayRules.FallsOn(3, 14, new DateOnly(2026, 4, 14)));
    }

    [Fact]
    public void FallsOn_29February_IsThe29thInALeapYear_AndThe28thOtherwise()
    {
        Assert.True(BirthdayRules.FallsOn(2, 29, new DateOnly(2028, 2, 29)));
        Assert.False(BirthdayRules.FallsOn(2, 29, new DateOnly(2028, 2, 28)));

        Assert.True(BirthdayRules.FallsOn(2, 29, new DateOnly(2027, 2, 28)));
        Assert.False(BirthdayRules.FallsOn(2, 29, new DateOnly(2027, 3, 1)));
    }

    [Fact]
    public void FallsOn_28February_IsStillTheDayOfSomeoneBornOn28February_InALeapYear()
        => Assert.True(BirthdayRules.FallsOn(2, 28, new DateOnly(2028, 2, 28)));

    [Fact]
    public void Fill_PutsTheMembersInPlaceOfTheToken_SeparatedByCommas()
    {
        var text = BirthdayRules.Fill("Cake for {members}!", ["<@1>", "<@2>"]);

        Assert.Equal("Cake for <@1>, <@2>!", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fill_WithNoMessage_UsesTheDefault(string? message)
        => Assert.Equal("Happy birthday <@1>!", BirthdayRules.Fill(message, ["<@1>"]));

    [Fact]
    public void Fill_AMessageWithNoToken_GetsNoMembers()
        => Assert.Equal("Hello everyone", BirthdayRules.Fill("Hello everyone", ["<@1>"]));

    [Fact]
    public void AMemberBirthday_HasNowhereToKeepAYear()
    {
        var names = typeof(MemberBirthday).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Year", StringComparison.OrdinalIgnoreCase));
    }
}
