using System.Net;
using Modbot.Core.Twitch;

namespace Modbot.Core.Tests.Twitch;

/// <summary>
/// How Twitch's answers are sorted, and how long a rate limit stops Modbot (Twitch design: a 429 is
/// never retried, and the stop comes from <c>Ratelimit-Reset</c> through the clock).
/// </summary>
public class TwitchErrorsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 19, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage WithReset(long? reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        if (reset is { } value)
            response.Headers.Add("Ratelimit-Reset", value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return response;
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TwitchProblem.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, TwitchProblem.Limited)]
    [InlineData(HttpStatusCode.InternalServerError, TwitchProblem.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, TwitchProblem.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, TwitchProblem.Other)]
    [InlineData(HttpStatusCode.Forbidden, TwitchProblem.Other)]
    public void AnApiAnswerIsSortedByItsStatus(HttpStatusCode status, TwitchProblem problem)
    {
        Assert.Equal(problem, TwitchErrors.FromApi(status, null).Problem);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void ARefusedTokenRequestMeansTheClientIdOrSecretIsWrong(HttpStatusCode status)
    {
        var failure = TwitchErrors.FromToken(status, """{"status":400,"message":"invalid client"}""");

        Assert.Equal(TwitchProblem.CredentialsRefused, failure.Problem);
        Assert.Equal("Twitch did not accept the client id and secret.", TwitchErrors.Sentence(failure));
    }

    [Fact]
    public void ATokenRequestThatIsLimitedOrFailsOnTwitchsSideIsNotTheSecretsFault()
    {
        Assert.Equal(TwitchProblem.Limited, TwitchErrors.FromToken(HttpStatusCode.TooManyRequests, null).Problem);
        Assert.Equal(TwitchProblem.Unavailable, TwitchErrors.FromToken(HttpStatusCode.ServiceUnavailable, null).Problem);
    }

    [Fact]
    public void TwitchsOwnWordsAreKept_AndCutLong()
    {
        var failure = TwitchErrors.FromApi(HttpStatusCode.BadRequest, """{"error":"Bad Request","status":400,"message":"Malformed query params."}""");

        Assert.Equal("Malformed query params.", TwitchErrors.Sentence(failure));

        var long300 = TwitchErrors.FromApi(HttpStatusCode.BadRequest, $$"""{"message":"{{new string('x', 500)}}"}""");
        Assert.Equal(300, TwitchErrors.Sentence(long300).Length);
    }

    [Fact]
    public void WithNoWordsTheStatusIsSaid()
    {
        Assert.Equal("Twitch answered 418.", TwitchErrors.Sentence(TwitchErrors.FromApi((HttpStatusCode)418, "not json")));
    }

    [Fact]
    public void ARateLimitStopsUntilTwitchsReset()
    {
        using var response = WithReset(Now.AddMinutes(7).ToUnixTimeSeconds());
        var failure = TwitchErrors.FromApi(HttpStatusCode.TooManyRequests, null, response.Headers);

        Assert.True(failure.IsALimit);
        Assert.Equal(Now.AddMinutes(7), TwitchErrors.StopUntil(failure, Now));
        Assert.Equal("Twitch is limiting Modbot.", TwitchErrors.Sentence(failure));
    }

    [Fact]
    public void WithNoResetTheStopIsFifteenMinutes()
    {
        using var response = WithReset(null);
        var failure = TwitchErrors.FromApi(HttpStatusCode.TooManyRequests, null, response.Headers);

        Assert.Equal(Now.AddMinutes(15), TwitchErrors.StopUntil(failure, Now));
    }

    [Fact]
    public void AResetThatIsNotATimeIsNotBelieved()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Ratelimit-Reset", "soon");
        var failure = TwitchErrors.FromApi(HttpStatusCode.TooManyRequests, null, response.Headers);

        Assert.Null(failure.ResetAt);
        Assert.Equal(Now.AddMinutes(15), TwitchErrors.StopUntil(failure, Now));
    }

    [Fact]
    public void TheStopIsNeverUnderAMinuteOrOverADay()
    {
        using var soon = WithReset(Now.AddSeconds(3).ToUnixTimeSeconds());
        using var past = WithReset(Now.AddMinutes(-5).ToUnixTimeSeconds());
        using var far = WithReset(Now.AddDays(30).ToUnixTimeSeconds());

        Assert.Equal(Now.AddMinutes(1), TwitchErrors.StopUntil(TwitchErrors.FromApi(HttpStatusCode.TooManyRequests, null, soon.Headers), Now));
        Assert.Equal(Now.AddMinutes(1), TwitchErrors.StopUntil(TwitchErrors.FromApi(HttpStatusCode.TooManyRequests, null, past.Headers), Now));
        Assert.Equal(Now.AddDays(1), TwitchErrors.StopUntil(TwitchErrors.FromApi(HttpStatusCode.TooManyRequests, null, far.Headers), Now));
    }

    [Fact]
    public void OnlyARateLimitIsALimit()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.BadRequest })
            Assert.False(TwitchErrors.FromApi(status, null).IsALimit);

        Assert.False(TwitchErrors.NoAnswer().IsALimit);
        Assert.Equal("Twitch did not answer.", TwitchErrors.Sentence(TwitchErrors.NoAnswer()));
    }
}
