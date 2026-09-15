namespace Modbot.Api.Features.Events;

/// <summary>How the live event WebSocket paces itself (API keys design §5). Tests shorten these.</summary>
public sealed class EventSocketOptions
{
    /// <summary>
    /// How often a caught-up connection or a waiting long poll looks for new facts without being
    /// woken. Ordinarily a written fact wakes them at once; this is the check that does not rely on it.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The longest a long poll may wait, and what it waits when it does not say.</summary>
    public int LongPollMaxWaitSeconds { get; init; } = 60;

    public int LongPollDefaultWaitSeconds { get; init; } = 30;

    public int LongPollMaxLimit { get; init; } = 500;

    public int LongPollDefaultLimit { get; init; } = 100;

    /// <summary>Pages a long poll reads past events the caller is not sent before answering anyway.</summary>
    public int LongPollMaxPages { get; init; } = 10;

    /// <summary>What a refused poll is told to wait, in seconds.</summary>
    public int LongPollRetryAfterSeconds { get; init; } = 5;

    /// <summary>How often a heartbeat message goes out, and access is checked again.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a new connection has to send its first subscribe.</summary>
    public TimeSpan SubscribeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A send that takes longer than this ends the connection.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Protocol-level pings, and how long an unanswered one is tolerated.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>Open connections per key, or per account for ticket connections made from a session.</summary>
    public int MaxConnectionsPerCaller { get; init; } = 5;

    /// <summary>Facts read per page while catching up.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>How long a ticket from <c>POST /api/events/tickets</c> can be used.</summary>
    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>See <see cref="FactFeed.GapWait"/>.</summary>
    public TimeSpan GapWait { get; init; } = FactFeed.DefaultGapWait;

    /// <summary>The largest message a client may send.</summary>
    public int MaxClientMessageBytes { get; init; } = 64 * 1024;
}
