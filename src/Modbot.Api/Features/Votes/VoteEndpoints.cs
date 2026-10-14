using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Votes;

namespace Modbot.Api.Features.Votes;

/// <summary>Somebody who picked an option, by the name the server shows.</summary>
public sealed record VoterView(string DiscordUserId, string Name);

/// <summary>One option of a vote with the number of people who picked it.</summary>
/// <param name="Voters">Who picked it, or null on an anonymous vote.</param>
public sealed record VoteOptionView(Guid Id, string Label, int Count, IReadOnlyList<VoterView>? Voters);

/// <summary>A vote as the page shows it.</summary>
/// <param name="RoleId">The Discord role a person must hold to vote, or null when anybody may.</param>
/// <param name="ShowCounts">The Discord message shows the count for each option while the vote is open.</param>
/// <param name="Anonymous">Nobody sees who picked what: the options carry no voters.</param>
/// <param name="Status"><c>open</c> or <c>closed</c>.</param>
/// <param name="Total">How many people have a pick on it.</param>
/// <param name="MessageProblem">
/// Set on the answer to a close: why the Discord message still shows the buttons. The vote is
/// closed in Modbot either way.
/// </param>
public sealed record VoteView(
    Guid Id,
    string Question,
    string ChannelId,
    string? RoleId,
    bool ShowCounts,
    bool Anonymous,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    int Total,
    IReadOnlyList<VoteOptionView> Options,
    string? MessageProblem);

/// <param name="CanManage">The caller may post and close votes (Manage posts).</param>
public sealed record VotesResponse(
    IReadOnlyList<VoteView> Votes,
    bool CanManage,
    int MostOptions,
    int MostQuestionLength,
    int MostLabelLength);

/// <summary>The body of a new vote. The options are in the order they are shown.</summary>
/// <param name="ShowCounts">Show the count for each option on the Discord message while the vote is open.</param>
/// <param name="Anonymous">Nobody sees who picked what. Left out, the vote is anonymous.</param>
public sealed record VoteBody(
    string Question,
    string ChannelId,
    string? RoleId,
    IReadOnlyList<string>? Options,
    bool ShowCounts = false,
    bool Anonymous = true);

/// <summary>
/// Votes: a question Modbot posts in a Discord channel, with a button for each option. Members
/// press a button to vote and can press another to change it; staff close it.
/// </summary>
/// <remarks>
/// Making and closing go through <see cref="VoteService"/>, the same code <c>/vote</c> uses.
/// Reading needs View posts and writing needs Manage posts, the same as <c>/post</c>. Modbot keeps
/// each person's pick on an anonymous vote too, to allow one vote each and changes; it is only not
/// shown. Anonymous is chosen when the vote is made and there is no way to turn it off after.
/// </remarks>
public static class VoteEndpoints
{
    private const int MostListed = 100;

    public static IEndpointRouteBuilder MapVotes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/votes").WithTags("Votes");

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] VoteService votes,
                CancellationToken ct) =>
            {
                var found = await db.Votes.AsNoTracking()
                    .Include(v => v.Options)
                    .OrderByDescending(v => v.CreatedAt)
                    .Take(MostListed)
                    .ToListAsync(ct);

                var counts = await votes.CountsAsync([.. found.Select(v => v.Id)], ct);
                var voters = await VotersAsync(db, found, ct);

                return Results.Ok(new VotesResponse(
                    [.. found.Select(v => View(v, counts, voters, null))],
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManagePosts),
                    VoteLimits.MostOptions,
                    VoteLimits.MostQuestionLength,
                    VoteLimits.MostLabelLength));
            })
            .WithName("ListVotes")
            .WithSummary("List votes")
            .WithDescription("The most recent votes, newest first, each with how many people picked each option.")
            .Produces<VotesResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ViewPosts);

        group.MapPost("", async (
                HttpContext http,
                [FromBody] VoteBody body,
                [FromServices] ModbotContext db,
                [FromServices] VoteService votes,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Actor.Of(http) is not { } actor)
                    return Results.Unauthorized();

                var draft = new VoteDraft(body.Question ?? string.Empty, body.ChannelId ?? string.Empty, body.Options ?? [], body.RoleId, body.ShowCounts, body.Anonymous);
                var made = await votes.CreateAsync(draft, actor.Id, ct);

                if (made.Vote is not { } vote)
                {
                    return made.BotOffline
                        ? Results.Json(new { error = made.Problem }, statusCode: StatusCodes.Status503ServiceUnavailable)
                        : Results.BadRequest(new { error = made.Problem });
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await new SettingsChange("votes")
                    .Items<string>("created", [], [vote.Question])
                    .RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(vote, new Dictionary<Guid, int>(), new Dictionary<Guid, List<VoterView>>(), null));
            })
            .WithName("CreateVote")
            .WithSummary("Post a vote")
            .WithDescription(
                "Post a question in a Discord channel with a button for each option. "
                + "Needs the Discord bot to be connected and to send messages in the channel.")
            .Produces<VoteView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManagePosts);

        group.MapPost("/{id:guid}/close", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] VoteService votes,
                CancellationToken ct) =>
            {
                var closed = await votes.CloseAsync(id, ct);

                if (closed.Vote is not { } vote)
                    return Results.NotFound(new { error = "No such vote." });

                if (!closed.AlreadyClosed)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    await new SettingsChange("votes")
                        .Items<string>("closed", [], [vote.Question])
                        .RecordAsync(http, ct);
                    await transaction.CommitAsync(ct);
                }

                var counts = await votes.CountsAsync([vote.Id], ct);
                var voters = await VotersAsync(db, [vote], ct);
                return Results.Ok(View(vote, counts, voters, closed.MessageProblem));
            })
            .WithName("CloseVote")
            .WithSummary("Close a vote")
            .WithDescription(
                "Stop taking votes and rewrite the Discord message with the result. "
                + "The vote is closed in Modbot even when Discord refuses the rewrite; the answer then says why.")
            .Produces<VoteView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManagePosts);

        return app;
    }

    /// <summary>Who picked each option, by option id, for the votes that are not anonymous.</summary>
    private static async Task<Dictionary<Guid, List<VoterView>>> VotersAsync(ModbotContext db, IReadOnlyCollection<Vote> votes, CancellationToken ct)
    {
        var named = votes.Where(v => !v.Anonymous).Select(v => v.Id).ToList();

        if (named.Count == 0)
            return [];

        var picks = await db.VotePicks.AsNoTracking()
            .Where(p => named.Contains(p.VoteId))
            .Select(p => new { p.OptionId, p.DiscordUserId })
            .ToListAsync(ct);

        var ids = picks.Select(p => p.DiscordUserId).Distinct().ToList();

        var names = await db.DiscordMembers.AsNoTracking()
            .Where(m => ids.Contains(m.UserId))
            .Select(m => new { m.UserId, m.DisplayName })
            .ToListAsync(ct);

        var nameOf = names
            .GroupBy(n => n.UserId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);

        return picks
            .GroupBy(p => p.OptionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new VoterView(
                        p.DiscordUserId,
                        nameOf.TryGetValue(p.DiscordUserId, out var name) && name.Length > 0 ? name : p.DiscordUserId))
                    .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList());
    }

    private static VoteView View(
        Vote vote,
        IReadOnlyDictionary<Guid, int> counts,
        IReadOnlyDictionary<Guid, List<VoterView>> voters,
        string? messageProblem)
    {
        var options = vote.Options
            .OrderBy(o => o.Position)
            .Select(o => new VoteOptionView(
                o.Id,
                o.Label,
                counts.GetValueOrDefault(o.Id),
                vote.Anonymous ? null : voters.GetValueOrDefault(o.Id) ?? []))
            .ToList();

        return new VoteView(
            vote.Id,
            vote.Question,
            vote.ChannelId,
            vote.RoleId,
            vote.ShowCounts,
            vote.Anonymous,
            vote.Status,
            vote.CreatedAt,
            vote.ClosedAt,
            options.Sum(o => o.Count),
            options,
            messageProblem);
    }
}
