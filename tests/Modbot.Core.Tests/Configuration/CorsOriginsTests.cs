using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Configuration;

/// <summary>
/// MODBOT_CORS_ORIGINS: the web addresses an operator lets call the API from a browser (API
/// conventions design §6). Unset means none; a typo is left out rather than guessed at.
/// </summary>
public class CorsOriginsTests
{
    private static IReadOnlyList<string> Read(string? value)
        => ModbotEnvironment.Read(new Dictionary<string, string?> { [ModbotEnvironment.CorsOriginsVariable] = value }).CorsOrigins;

    [Fact]
    public void UnsetAllowsNone()
    {
        Assert.Empty(ModbotEnvironment.Read(new Dictionary<string, string?>()).CorsOrigins);
        Assert.Empty(Read("   "));
    }

    [Fact]
    public void AddressesAreReadAsTheBrowserSendsThem()
    {
        var origins = Read("https://tools.example.org/dashboard, http://localhost:5173 https://tools.example.org");

        Assert.Equal(["https://tools.example.org", "http://localhost:5173"], origins);
    }

    [Fact]
    public void AStarAllowsAny()
        => Assert.Equal(["*"], Read("*"));

    [Fact]
    public void ATypoIsLeftOut()
        => Assert.Equal(["https://ok.example"], Read("tools.example.org, ftp://files.example, https://ok.example"));
}
