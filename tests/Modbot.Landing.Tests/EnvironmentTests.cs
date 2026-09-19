using Modbot.Landing.Configuration;

namespace Modbot.Landing.Tests;

public class EnvironmentTests
{
    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("3000", 3000)]
    [InlineData("0", 8080)]
    [InlineData("70000", 8080)]
    [InlineData("not-a-port", 8080)]
    public void ThePortComesFromPortOrFallsBackTo8080(string? value, int expected)
    {
        var environment = LandingEnvironment.Read(name => name == LandingEnvironment.PortVariable ? value : null);

        Assert.Equal(expected, environment.Port);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("https://discord.gg/modbot", "https://discord.gg/modbot")]
    [InlineData("  https://discord.gg/modbot  ", "https://discord.gg/modbot")]
    [InlineData("http://discord.gg/modbot", "http://discord.gg/modbot")]
    [InlineData("discord.gg/modbot", null)]
    [InlineData("javascript:alert(1)", null)]
    public void TheDiscordAddressIsTakenOnlyWhenItIsAWholeHttpAddress(string? value, string? expected)
    {
        var environment = LandingEnvironment.Read(name => name == LandingEnvironment.DiscordVariable ? value : null);

        Assert.Equal(expected, environment.DiscordUrl);
    }

    [Theory]
    [InlineData(null, LandingEnvironment.DefaultGithubUrl)]
    [InlineData("", LandingEnvironment.DefaultGithubUrl)]
    [InlineData("not-an-address", LandingEnvironment.DefaultGithubUrl)]
    [InlineData("https://github.com/someone/fork", "https://github.com/someone/fork")]
    public void TheGithubAddressFallsBackToTheProjectsOwnRepository(string? value, string expected)
    {
        var environment = LandingEnvironment.Read(name => name == LandingEnvironment.GithubVariable ? value : null);

        Assert.Equal(expected, environment.GithubUrl);
    }
}
