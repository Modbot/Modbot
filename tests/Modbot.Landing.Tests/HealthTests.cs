using System.Net;

namespace Modbot.Landing.Tests;

public class HealthTests
{
    [Fact]
    public async Task LiveAnswersWheneverTheProcessIsUp()
    {
        await using var host = await LandingTestHost.StartAsync(built: false);

        using var response = await host.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadyAnswersOnceThereIsAPageToServe()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadyRefusesWhenThePageWasNeverBuilt()
    {
        await using var host = await LandingTestHost.StartAsync(built: false);

        using var response = await host.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
