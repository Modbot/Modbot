using Modbot.Core.Configuration;

namespace Modbot.Demo.Tests;

/// <summary>
/// The gate: when <c>MODBOT_DEMO</c> is honoured and when it is ignored.
/// </summary>
/// <remarks>
/// Every test here is really the same test asked twice, because demo mode's whole security model is
/// that every visitor is an administrator (demo mode design §3). Turning it on over a deployment
/// that holds real data would hand that deployment to whoever opened the URL, so the conditions are
/// pinned rather than left to be read off the implementation.
/// </remarks>
public class DemoModeTests
{
    private static DemoMode Requested() => new() { Requested = true };

    [Fact]
    public void AnEmptyDatabaseWithTheVariableSetIsADemo()
    {
        var demo = Requested();

        Assert.True(demo.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: false));
        Assert.True(demo.IsOn);
    }

    [Fact]
    public void WithoutTheVariableNothingIsEverADemo()
    {
        var demo = new DemoMode();

        Assert.False(demo.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: false));
        Assert.False(demo.IsOn);
    }

    [Fact]
    public void AStaffAccountMeansTheVariableIsIgnored()
    {
        var demo = Requested();

        Assert.False(demo.Decide(hasStaffAccount: true, onboardingComplete: false, holdsDemoData: false));
        Assert.False(demo.IsOn);
    }

    [Fact]
    public void AFinishedSetupWizardMeansTheVariableIsIgnored()
    {
        var demo = Requested();

        Assert.False(demo.Decide(hasStaffAccount: false, onboardingComplete: true, holdsDemoData: false));
        Assert.False(demo.IsOn);
    }

    /// <summary>
    /// The demo seeds staff accounts and finishes the wizard itself, so without this a demo would
    /// refuse to be a demo on its own second start.
    /// </summary>
    [Fact]
    public void ADatabaseTheDemoSeededStaysADemo()
    {
        var demo = Requested();

        Assert.True(demo.Decide(hasStaffAccount: true, onboardingComplete: true, holdsDemoData: true));
        Assert.True(demo.IsOn);
    }

    [Fact]
    public void ReadingBeforeStartupDecidedThrowsRatherThanAnswering()
    {
        var demo = Requested();

        Assert.False(demo.Decided);
        Assert.Throws<InvalidOperationException>(() => demo.IsOn);
    }

    [Fact]
    public void TheIgnoredExplanationSaysNothingWasSeededOrRemoved()
    {
        var demo = Requested();
        demo.Decide(hasStaffAccount: true, onboardingComplete: true, holdsDemoData: false);

        var explanation = demo.Explain(hasStaffAccount: true, onboardingComplete: true, holdsDemoData: false);

        Assert.Contains("ignored", explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was seeded or removed", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResetScheduleDefaultsToADayAndAcceptsNever()
    {
        Assert.Equal(24, DemoMode.DefaultResetHours);

        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=x;Database=y",
            [DemoMode.Variable] = "1",
            [DemoMode.ResetHoursVariable] = "0",
        });

        Assert.True(environment.Demo);
        Assert.Equal(0, DemoMode.From(environment).ResetHours);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData("100000")]
    public void ANonsenseResetScheduleLeavesTheDefaultInPlace(string value)
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=x;Database=y",
            [DemoMode.ResetHoursVariable] = value,
        });

        Assert.Null(environment.DemoResetHours);
        Assert.Equal(DemoMode.DefaultResetHours, DemoMode.From(environment).ResetHours);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    [InlineData(" on ")]
    public void TheVariableIsReadTheSameWayAsEveryOtherSwitch(string value)
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=x;Database=y",
            [DemoMode.Variable] = value,
        });

        Assert.True(environment.Demo);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotAsking(string? value)
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=x;Database=y",
            [DemoMode.Variable] = value,
        });

        Assert.False(environment.Demo);
    }
}
