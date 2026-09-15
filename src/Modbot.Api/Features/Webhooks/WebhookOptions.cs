using Modbot.Api.Features.Events;

namespace Modbot.Api.Features.Webhooks;

/// <summary>How webhook delivery paces itself (API keys design §6). Tests shorten these.</summary>
public sealed class WebhookOptions
{
    /// <summary>How often the delivery service looks for webhooks with something to send.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a receiver has to answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Requests per webhook per pass, so one busy webhook does not hold up the others for long.</summary>
    public int EventsPerPass { get; init; } = 50;

    /// <summary>Facts read per page.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>The first wait after a failure; each later one is three times longer.</summary>
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest wait between attempts, and the most a <c>Retry-After</c> is honoured for.</summary>
    public TimeSpan LongestRetry { get; init; } = TimeSpan.FromHours(1);

    /// <summary>A webhook without a 2xx for this long is turned off.</summary>
    public TimeSpan TurnOffAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Delivery attempts kept per webhook.</summary>
    public int DeliveriesKept { get; init; } = 50;

    /// <summary>See <see cref="FactFeed.GapWait"/>.</summary>
    public TimeSpan GapWait { get; init; } = FactFeed.DefaultGapWait;

    /// <summary>The wait before attempt <paramref name="failedAttempts"/> + 1: 10 s, 30 s, 90 s … up to an hour.</summary>
    public TimeSpan RetryDelay(int failedAttempts, TimeSpan? retryAfter)
    {
        if (retryAfter is { } asked)
            return asked < TimeSpan.Zero ? TimeSpan.Zero : asked > LongestRetry ? LongestRetry : asked;

        var seconds = FirstRetry.TotalSeconds * Math.Pow(3, Math.Max(0, failedAttempts - 1));
        return seconds >= LongestRetry.TotalSeconds ? LongestRetry : TimeSpan.FromSeconds(seconds);
    }
}
