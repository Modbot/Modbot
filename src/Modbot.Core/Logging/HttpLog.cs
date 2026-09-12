using Serilog;
using Serilog.Context;
using Serilog.Events;

namespace Modbot.Core.Logging;

/// <summary>
/// Records one outbound API call into the HTTP stream.
/// </summary>
/// <remarks>
/// <para>
/// Everything that reaches VRChat or Discord goes through here, so the HTTP stream is a complete
/// record of Modbot's outbound traffic. That completeness is the point: section 4.3's questions are
/// things like <em>"every 429 on <c>groups.members</c> last week with the bucket state at the
/// time"</em>, and a partial log cannot answer them.
/// </para>
/// <para>
/// Properties are structured, never interpolated. The interpolated form renders identically in the
/// console and is worthless in the JSONL — which is why the mistake is invisible exactly where you
/// would notice it.
/// </para>
/// </remarks>
public static class HttpLog
{
    /// <summary>
    /// Logs a completed call. <paramref name="retryAfter"/> is nullable because VRChat does not
    /// send <c>Retry-After</c> — see section 4.3, which is why the limiter cold-stops instead of
    /// backing off against a header that is not there.
    /// </summary>
    public static void Completed(
        ILogger logger,
        string service,
        string endpointClass,
        string method,
        int status,
        TimeSpan duration,
        double? bucketTokens = null,
        TimeSpan? retryAfter = null,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var level = status switch
        {
            429 => LogEventLevel.Warning,          // rate limited -- always worth seeing
            >= 500 => LogEventLevel.Warning,
            >= 400 => LogEventLevel.Warning,
            _ => LogEventLevel.Information,
        };

        using (LogContext.PushProperty(LogArea.Name, LogArea.Http))
        using (correlationId is null ? null : LogContext.PushProperty("CorrelationId", correlationId))
        {
            logger.Write(
                level,
                "{Service} {Method} {EndpointClass} -> {Status} in {DurationMs:0} ms",
                service, method, endpointClass, status, duration.TotalMilliseconds);

            // Bucket state and retry hints ride as separate properties rather than being formatted
            // into the message, so they stay queryable.
            if (bucketTokens is not null || retryAfter is not null)
            {
                logger
                    .ForContext("BucketTokens", bucketTokens)
                    .ForContext("RetryAfterSeconds", retryAfter?.TotalSeconds)
                    .ForContext(LogArea.Name, LogArea.Http)
                    .Write(LogEventLevel.Debug,
                        "{Service} {EndpointClass} limiter state after call", service, endpointClass);
            }
        }
    }

    /// <summary>Logs a call that never produced a response — DNS failure, timeout, cancellation.</summary>
    public static void Failed(
        ILogger logger,
        string service,
        string endpointClass,
        string method,
        Exception exception,
        TimeSpan duration,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        using (LogContext.PushProperty(LogArea.Name, LogArea.Http))
        using (correlationId is null ? null : LogContext.PushProperty("CorrelationId", correlationId))
        {
            logger.Error(
                exception,
                "{Service} {Method} {EndpointClass} failed after {DurationMs:0} ms",
                service, method, endpointClass, duration.TotalMilliseconds);
        }
    }
}
