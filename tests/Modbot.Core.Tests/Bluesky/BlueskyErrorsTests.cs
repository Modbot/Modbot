using System.Net;
using Modbot.Core.Bluesky;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// Bluesky's refusals sorted into what Modbot does about them (Bluesky design §3.1, §3.4): the swap
/// that means "already there", an ended token, a refused sign-in, a rate limit with its reset time,
/// and a taken-down account.
/// </summary>
public class BlueskyErrorsTests
{
    private static BlueskyFailure From(HttpStatusCode status, string error, bool signingIn = false, long? reset = null)
    {
        using var response = new HttpResponseMessage(status);
        if (reset is { } at)
            response.Headers.Add("ratelimit-reset", at.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return BlueskyErrors.FromXrpc(status, $$"""{"error":"{{error}}","message":"Bluesky's own words"}""", response.Headers, signingIn);
    }

    [Fact]
    public void SomethingAlreadyAtTheKeyIsAnInvalidSwap()
    {
        Assert.Equal(BlueskyProblem.InvalidSwap, From(HttpStatusCode.BadRequest, "InvalidSwap").Problem);
    }

    [Fact]
    public void NothingAtTheKeyIsRecordNotFound()
    {
        Assert.Equal(BlueskyProblem.RecordNotFound, From(HttpStatusCode.BadRequest, "RecordNotFound").Problem);
    }

    [Fact]
    public void AnEndedTokenIsSaidSo()
    {
        Assert.Equal(BlueskyProblem.TokenExpired, From(HttpStatusCode.BadRequest, "ExpiredToken").Problem);
        Assert.Equal(BlueskyProblem.TokenRefused, From(HttpStatusCode.Unauthorized, "InvalidToken").Problem);
    }

    [Fact]
    public void AuthenticationRequiredIsThePasswordAtSignIn_AndTheTokenAnywhereElse()
    {
        var atSignIn = From(HttpStatusCode.Unauthorized, "AuthenticationRequired", signingIn: true);
        var elsewhere = From(HttpStatusCode.Unauthorized, "AuthenticationRequired");

        Assert.Equal(BlueskyProblem.BadSignIn, atSignIn.Problem);
        Assert.Equal(BlueskyErrors.NotAccepted, BlueskyErrors.Sentence(atSignIn));
        Assert.Equal(BlueskyProblem.TokenRefused, elsewhere.Problem);
    }

    [Fact]
    public void ARateLimitStopsTheLaneUntilItsReset()
    {
        var reset = new DateTimeOffset(2026, 10, 3, 21, 40, 0, TimeSpan.Zero);
        var failure = From(HttpStatusCode.TooManyRequests, "RateLimitExceeded", reset: reset.ToUnixTimeSeconds());

        Assert.Equal(BlueskyProblem.RateLimited, failure.Problem);
        Assert.True(failure.StopsTheLane);
        Assert.Equal(reset, failure.ResetAt);
        Assert.Equal(reset, BlueskyErrors.StopUntil(failure, reset.AddMinutes(-30)));
    }

    [Fact]
    public void ARateLimitWithNoTimeStopsForFifteenMinutes_AndNeverLessThanOne()
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var noTime = From(HttpStatusCode.TooManyRequests, "RateLimitExceeded");
        var alreadyPast = From(HttpStatusCode.TooManyRequests, "RateLimitExceeded", reset: now.AddMinutes(-5).ToUnixTimeSeconds());

        Assert.Equal(now + BlueskyErrors.LimitStop, BlueskyErrors.StopUntil(noTime, now));
        Assert.Equal(now + BlueskyErrors.ShortestStop, BlueskyErrors.StopUntil(alreadyPast, now));
    }

    [Fact]
    public void ATakenDownAccountIsGone()
    {
        var failure = From(HttpStatusCode.Unauthorized, "AccountTakedown", signingIn: true);

        Assert.Equal(BlueskyProblem.AccountGone, failure.Problem);
        Assert.Equal("Bluesky's own words", BlueskyErrors.Sentence(failure));
    }

    [Fact]
    public void A5xxOrNoAnswerIsUnclear()
    {
        Assert.True(From(HttpStatusCode.BadGateway, "InternalServerError").Unclear);
        Assert.True(BlueskyErrors.NoAnswer().Unclear);
        Assert.Equal(BlueskyErrors.Unreachable, BlueskyErrors.Sentence(BlueskyErrors.NoAnswer()));
    }
}
