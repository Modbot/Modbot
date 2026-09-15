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
}
