using System.Globalization;

namespace Modbot.Cloud.Common;

/// <summary>
/// The error body clients read: a stable <c>code</c> to branch on and a <c>message</c> for people,
/// the same shape as the Modbot server's client protocol (protocol section 7).
/// </summary>
public sealed record CloudError(string Code, string Message)
{
    public static IResult Result(int status, string code, string message) =>
        Results.Json(new CloudError(code, message), statusCode: status);

    /// <summary>A <c>429</c> with <c>Retry-After</c> in whole seconds, never less than one.</summary>
    public static IResult TooMany(HttpContext http, TimeSpan wait, string message)
    {
        ArgumentNullException.ThrowIfNull(http);

        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Result(StatusCodes.Status429TooManyRequests, "rate_limited", message);
    }
}
