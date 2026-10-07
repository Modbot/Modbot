namespace Modbot.Api.Features.Twitch;

/// <summary>One time the channel was live, as the Live and Now cards and the event's line show it.</summary>
/// <param name="Id">Twitch's stream id. Opaque.</param>
/// <param name="Category">The category (game) Twitch shows.</param>
/// <param name="Viewers">The viewer count at the last poll.</param>
/// <param name="PeakViewers">The most viewers any poll saw.</param>
/// <param name="EndedAt">When the poll found the channel no longer live. Null while it is.</param>
/// <param name="EventId">The calendar event it is linked to, or null.</param>
/// <param name="EventTitle">That event's title, or null.</param>
/// <param name="Link">The channel's page on Twitch, or null when no channel is set.</param>
public sealed record TwitchStreamView(
    string Id,
    string? Title,
    string? Category,
    int Viewers,
    int PeakViewers,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    Guid? EventId,
    string? EventTitle,
    string? Link);

/// <summary>What the Live and Now pages' "Live on Twitch" card shows.</summary>
/// <param name="On">The poll is on and Twitch is set up. When false the card is not drawn at all.</param>
/// <param name="ChannelName">The channel's display name.</param>
/// <param name="Stream">The stream now live, or null when the channel is not.</param>
public sealed record TwitchLiveView(bool On, string? ChannelName, TwitchStreamView? Stream);

/// <param name="EventId">The event to link the stream to, or null to clear the link.</param>
public sealed record TwitchStreamEventRequest(Guid? EventId);

/// <summary>What the Health page's Twitch card shows.</summary>
/// <param name="On">The poll is on.</param>
/// <param name="Silent">The poll is on and Twitch has not answered for more than five minutes.</param>
/// <param name="CheckProblem">What the last Check found wrong, or null.</param>
/// <param name="PollProblem">What went wrong at the last poll, or null.</param>
/// <param name="PolledAt">When the poll last got an answer.</param>
/// <param name="LimitedUntil">Twitch is limiting Modbot: nothing is sent to Twitch before this.</param>
public sealed record TwitchHealth(
    bool On,
    bool Silent,
    string? CheckProblem,
    string? PollProblem,
    DateTimeOffset? PolledAt,
    DateTimeOffset? LimitedUntil);
