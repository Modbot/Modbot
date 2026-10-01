namespace Modbot.Api.Features.WorldLists;

/// <summary>One world in a list as a person fills it in.</summary>
/// <param name="MinPlayers">The fewest players the game is for, at least 1; null for any.</param>
/// <param name="MaxPlayers">The most players the game is for, at least the fewest; null for any.</param>
public sealed record WorldListWorldRequest(string WorldId, int? MinPlayers, int? MaxPlayers);

/// <summary>A world list as a person saves it: its name and its worlds, in order.</summary>
public sealed record WorldListRequest(string Name, IReadOnlyList<WorldListWorldRequest>? Worlds);

/// <summary>One world in a list, with what Modbot knows of it.</summary>
/// <param name="Name">The world's name; null until Modbot has read it.</param>
public sealed record WorldListWorldView(string WorldId, string? Name, string? ThumbnailUrl, int? MinPlayers, int? MaxPlayers);

/// <summary>An event that picks its world from a list.</summary>
/// <param name="State">The event's state: <c>draft</c>, <c>scheduled</c>, <c>open</c>, <c>finished</c> or <c>cancelled</c>.</param>
public sealed record WorldListUseView(Guid EventId, string Title, string State);

/// <param name="UsedBy">Events, not deleted, that pick from it.</param>
public sealed record WorldListView(
    Guid Id,
    string Name,
    IReadOnlyList<WorldListWorldView> Worlds,
    IReadOnlyList<WorldListUseView> UsedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="CanManage">The account may make, change and delete lists.</param>
public sealed record WorldListsView(IReadOnlyList<WorldListView> Lists, bool CanManage);

/// <summary>A world's link or id, as pasted.</summary>
public sealed record WorldFindRequest(string Text);
