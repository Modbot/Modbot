using Modbot.Core.Data.Entities;
using Modbot.Core.Supporters;

namespace Modbot.Core.Tests.Supporters;

/// <summary>
/// The rules about when a supporter keeps their roles (three days after the paid month is up), how
/// one payer is told from another without keeping an email address, and how a secret is compared.
/// </summary>
public class SupporterRulesTests
{
    private static readonly DateTimeOffset Paid = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMonthlyPaymentCoversOneMonth_AndTheRolesGoThreeDaysAfter()
    {
        var through = SupporterRules.PaidThrough(Paid);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), through);
        Assert.Equal(through.AddDays(3), SupporterRules.RolesEndAt(through, SupporterLimits.DefaultRolesOffDays));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(365)]
    public void TheRolesGoTheSetNumberOfDaysAfterTheMonthIsUp(int days)
    {
        var through = SupporterRules.PaidThrough(Paid);

        Assert.Equal(through.AddDays(days), SupporterRules.RolesEndAt(through, days));
        Assert.False(SupporterRules.RolesAreOff(through, through.AddDays(days).AddMinutes(-1), days));
        Assert.True(SupporterRules.RolesAreOff(through, through.AddDays(days), days));
        Assert.False(SupporterRules.HasLapsed(through, through.AddDays(days).AddMinutes(-1), days));
        Assert.True(SupporterRules.HasLapsed(through, through.AddDays(days), days));
    }

    [Fact]
    public void WithNeverTheRolesNeverComeOff_ButTheSupporterEndsWhenTheMonthIsUp()
    {
        var through = SupporterRules.PaidThrough(Paid);

        Assert.Null(SupporterRules.RolesEndAt(through, SupporterLimits.RolesNeverOff));
        Assert.False(SupporterRules.RolesAreOff(through, through.AddDays(4000), SupporterLimits.RolesNeverOff));
        Assert.False(SupporterRules.HasLapsed(through, through.AddMinutes(-1), SupporterLimits.RolesNeverOff));
        Assert.True(SupporterRules.HasLapsed(through, through, SupporterLimits.RolesNeverOff));
    }

    [Fact]
    public void EachSiteHasItsOwnSetting()
    {
        Assert.Equal(1, SupporterRules.RolesOffDaysFor(SupporterProviders.Kofi, 1, 2));
        Assert.Equal(2, SupporterRules.RolesOffDaysFor(SupporterProviders.Patreon, 1, 2));
    }

    [Fact]
    public void RolesAreKeptUntilThreeDaysAfterTheMonthIsUp()
    {
        var through = SupporterRules.PaidThrough(Paid);

        Assert.False(SupporterRules.HasLapsed(through, through.AddDays(2).AddHours(23), SupporterLimits.DefaultRolesOffDays));
        Assert.True(SupporterRules.HasLapsed(through, through.AddDays(3), SupporterLimits.DefaultRolesOffDays));
        Assert.True(SupporterRules.HasLapsed(through, through.AddDays(30), SupporterLimits.DefaultRolesOffDays));
    }

    [Fact]
    public void ATierKeyIgnoresCaseAndSpacesAroundIt()
        => Assert.Equal(SupporterRules.TierKeyOf("Gold"), SupporterRules.TierKeyOf("  gold "));

    [Fact]
    public void ThePayerIsTheSameForTheSameEmail_WhateverItsCase()
        => Assert.Equal(
            SupporterRules.PayerIdOf("kofi", "Sam@Example.com", null, "Sam"),
            SupporterRules.PayerIdOf("kofi", " sam@example.com ", "123", "Someone else"));

    [Fact]
    public void ThePayerIdNeverContainsTheEmail()
    {
        var id = SupporterRules.PayerIdOf("kofi", "sam@example.com", null, "Sam");

        Assert.DoesNotContain("sam", id, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(40, id.Length);
    }

    [Fact]
    public void WithNoEmailTheDiscordAccountStandsIn_AndWithNeitherTheName()
    {
        Assert.Equal(
            SupporterRules.PayerIdOf("kofi", null, "123", "Sam"),
            SupporterRules.PayerIdOf("kofi", "", "123", "Another"));
        Assert.NotEqual(
            SupporterRules.PayerIdOf("kofi", null, "123", "Sam"),
            SupporterRules.PayerIdOf("kofi", null, "124", "Sam"));
        Assert.Equal(
            SupporterRules.PayerIdOf("kofi", null, null, "Sam"),
            SupporterRules.PayerIdOf("kofi", null, " ", " sam "));
    }

    [Fact]
    public void TwoSitesNeverShareAPayerId()
        => Assert.NotEqual(
            SupporterRules.PayerIdOf("kofi", "sam@example.com", null, "Sam"),
            SupporterRules.PayerIdOf("other", "sam@example.com", null, "Sam"));

    [Theory]
    [InlineData("secret", "secret", true)]
    [InlineData("secret", "Secret", false)]
    [InlineData("secret", "secre", false)]
    [InlineData("", "", false)]
    [InlineData(null, "secret", false)]
    [InlineData("secret", null, false)]
    public void ASecretMatchesOnlyItself(string? given, string? stored, bool expected)
        => Assert.Equal(expected, SupporterRules.SameSecret(given, stored));

    [Fact]
    public void TheThankYouFillsInTheNameAndTheTier()
        => Assert.Equal("Thanks Sam, for Gold!", SupporterRules.Thanks("Thanks {name}, for {TIER}!", "Sam", "Gold"));
}
