using Modbot.Landing.Configuration;

namespace Modbot.Landing.Tests;

/// <summary>
/// MODBOT_MY_URL: a group running its own server selector points every link on the page at it
/// with one variable (central services spec 6).
/// </summary>
public class MyUrlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unset_leaves_the_project_own_selector_in_the_page()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/");
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("https://my.modbot.co/go", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_selector_replaces_it_everywhere_on_the_page()
    {
        await using var host = await LandingTestHost.StartAsync(myUrl: "https://my.example.org");

        using var response = await host.SendAsync(HttpMethod.Get, "/");
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("https://my.example.org/go", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://my.modbot.co", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://my.example.org/", "https://my.example.org")]
    [InlineData("my.example.org", LandingEnvironment.DefaultMyUrl)]
    [InlineData(null, LandingEnvironment.DefaultMyUrl)]
    [InlineData("", LandingEnvironment.DefaultMyUrl)]
    public void An_address_is_kept_without_its_trailing_slash_or_ignored(string? set, string expected)
    {
        var environment = LandingEnvironment.Read(name => name == LandingEnvironment.MyUrlVariable ? set : null);

        Assert.Equal(expected, environment.MyUrl);
    }
}
