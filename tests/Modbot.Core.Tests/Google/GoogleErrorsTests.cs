using System.Net;
using System.Net.Http.Headers;
using Modbot.Core.Google;

namespace Modbot.Core.Tests.Google;

/// <summary>
/// Google's refusals sorted into what Modbot does about them (Google Calendar design §3.4, §3.7):
/// a limit stops the lane and is never retried, everything else is a failure with a sentence.
/// </summary>
public class GoogleErrorsTests
{
    private static string CalendarError(int code, string reason, string message = "Something") =>
        $$$"""{"error":{"code":{{{code}}},"message":"{{{message}}}","errors":[{"domain":"global","reason":"{{{reason}}}","message":"{{{message}}}"}]}}""";

    [Theory]
    [InlineData("rateLimitExceeded", GoogleProblem.Limited)]
    [InlineData("userRateLimitExceeded", GoogleProblem.Limited)]
    [InlineData("usageLimits", GoogleProblem.Limited)]
    [InlineData("quotaExceeded", GoogleProblem.QuotaExceeded)]
    [InlineData("forbidden", GoogleProblem.Forbidden)]
    [InlineData("insufficientPermissions", GoogleProblem.Forbidden)]
    [InlineData("requiredAccessLevel", GoogleProblem.Forbidden)]
    public void Each403ReasonIsSortedByItsReason(string reason, GoogleProblem expected)
    {
        var failure = GoogleErrors.FromCalendar(HttpStatusCode.Forbidden, CalendarError(403, reason));

        Assert.Equal(expected, failure.Problem);
        Assert.Equal(reason, failure.Reason);
        Assert.Equal(expected is GoogleProblem.Limited or GoogleProblem.QuotaExceeded, failure.StopsTheLane);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authError", GoogleProblem.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, "notFound", GoogleProblem.NotFound)]
    [InlineData(HttpStatusCode.Conflict, "duplicate", GoogleProblem.Duplicate)]
    [InlineData(HttpStatusCode.Gone, "deleted", GoogleProblem.Gone)]
    [InlineData(HttpStatusCode.PreconditionFailed, "conditionNotMet", GoogleProblem.ConditionNotMet)]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", GoogleProblem.Limited)]
    [InlineData(HttpStatusCode.InternalServerError, "backendError", GoogleProblem.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError", GoogleProblem.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, "invalid", GoogleProblem.Other)]
    public void EachStatusIsSorted(HttpStatusCode status, string reason, GoogleProblem expected)
    {
        Assert.Equal(expected, GoogleErrors.FromCalendar(status, CalendarError((int)status, reason)).Problem);
    }

    [Fact]
    public void A429IsALimitWhateverTheBodySays()
    {
        Assert.Equal(GoogleProblem.Limited, GoogleErrors.FromCalendar(HttpStatusCode.TooManyRequests, null).Problem);
        Assert.Equal(GoogleProblem.Limited, GoogleErrors.FromCalendar(HttpStatusCode.TooManyRequests, "<html>").Problem);
    }

    [Theory]
    [InlineData("""{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""", GoogleProblem.KeyRefused)]
    [InlineData("""{"error":"invalid_grant","error_description":"Invalid grant: account not found"}""", GoogleProblem.KeyRefused)]
    [InlineData("""{"error":"invalid_client","error_description":"The OAuth client was not found."}""", GoogleProblem.KeyRefused)]
    [InlineData("""{"error":"invalid_grant","error_description":"Invalid JWT: Token must be a short-lived token (60 minutes) and in a reasonable timeframe. Check your iat and exp values in the JWT claim."}""", GoogleProblem.ClockOff)]
    public void TokenRefusalsSayWhetherTheKeyOrTheClockIsWrong(string body, GoogleProblem expected)
    {
        Assert.Equal(expected, GoogleErrors.FromToken(HttpStatusCode.BadRequest, body).Problem);
    }

    [Fact]
    public void ATokenEndpointLimitStopsTheLane()
    {
        var failure = GoogleErrors.FromToken(HttpStatusCode.TooManyRequests, null);

        Assert.Equal(GoogleProblem.Limited, failure.Problem);
        Assert.True(failure.StopsTheLane);
    }

    [Fact]
    public void ALimitStopsFor15Minutes_OrLongerWhenGoogleAsks()
    {
        var plain = GoogleErrors.FromCalendar(HttpStatusCode.TooManyRequests, null);
        var shortAsk = GoogleErrors.FromCalendar(HttpStatusCode.TooManyRequests, null, new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)));
        var longAsk = GoogleErrors.FromCalendar(HttpStatusCode.TooManyRequests, null, new RetryConditionHeaderValue(TimeSpan.FromMinutes(40)));

        Assert.Equal(TimeSpan.FromMinutes(15), GoogleErrors.StopFor(plain));
        Assert.Equal(TimeSpan.FromMinutes(15), GoogleErrors.StopFor(shortAsk));
        Assert.Equal(TimeSpan.FromMinutes(40), GoogleErrors.StopFor(longAsk));
    }

    [Fact]
    public void TheUseLimitsStopForSixHours()
    {
        var failure = GoogleErrors.FromCalendar(HttpStatusCode.Forbidden, CalendarError(403, "quotaExceeded", "Calendar usage limits exceeded."));

        Assert.Equal(TimeSpan.FromHours(6), GoogleErrors.StopFor(failure));
    }

    [Fact]
    public void EachFailureIsSaidInAPlainSentence()
    {
        Assert.Equal("Google did not accept the key.", GoogleErrors.Sentence(new GoogleFailure(GoogleProblem.KeyRefused, 400)));
        Assert.Equal("The server's clock is off.", GoogleErrors.Sentence(new GoogleFailure(GoogleProblem.ClockOff, 400)));
        Assert.Equal("Modbot can't see this calendar.", GoogleErrors.Sentence(new GoogleFailure(GoogleProblem.NotFound, 404)));
        Assert.Equal("Google is limiting Modbot.", GoogleErrors.Sentence(new GoogleFailure(GoogleProblem.Limited, 429)));
        Assert.Equal("Google did not answer.", GoogleErrors.Sentence(GoogleErrors.NoAnswer()));

        // Anything else is Google's own words.
        Assert.Equal("Bad Request", GoogleErrors.Sentence(GoogleErrors.FromCalendar(HttpStatusCode.BadRequest, CalendarError(400, "invalid", "Bad Request"))));
        Assert.Equal("Google answered 400.", GoogleErrors.Sentence(GoogleErrors.FromCalendar(HttpStatusCode.BadRequest, null)));
    }
}
