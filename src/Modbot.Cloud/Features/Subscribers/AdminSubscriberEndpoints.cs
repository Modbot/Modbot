using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Mail;

namespace Modbot.Cloud.Features.Subscribers;

/// <param name="UnsubscribeUrl">The link that takes this person off the list, ready to paste into a message.</param>
public sealed record AdminSubscriberView(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("unsubscribedAt")] DateTimeOffset? UnsubscribedAt,
    [property: JsonPropertyName("unsubscribeUrl")] string UnsubscribeUrl);

/// <param name="Subscribed">Of those, still on the list.</param>
/// <param name="BySource">How many came from each place the box was ticked.</param>
public sealed record SubscriberCounts(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("subscribed")] int Subscribed,
    [property: JsonPropertyName("unsubscribed")] int Unsubscribed,
    [property: JsonPropertyName("bySource")] IReadOnlyDictionary<string, int> BySource);

/// <summary>
/// The mailing list as the project reads it: who is on it, where from, and how many.
/// </summary>
/// <remarks>
/// <strong>Both endpoints need the admin sign-in</strong> — the <c>/admin</c> cookie or
/// <c>Authorization: Bearer &lt;ROOT_API_KEY&gt;</c>. A list of email addresses is exactly the thing
/// that must not be readable with a key handed to another service.
/// </remarks>
public static class AdminSubscriberEndpoints
{
    public static IEndpointRouteBuilder MapAdminSubscribers(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/subscribers", SubscribersAsync);
        admin.MapGet("/subscribers/counts", CountsAsync);

        return app;
    }

    internal static async Task<IResult> SubscribersAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] CloudContext db,
        [FromServices] MailSettings mail,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        var query = db.Subscribers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Search.Contains(search);
            query = query.Where(s =>
                EF.Functions.ILike(s.Email, pattern)
                || (s.Source != null && EF.Functions.ILike(s.Source, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(s => s.FirstSeenAt)
            .ThenBy(s => s.Email)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        var items = rows
            .Select(s => new AdminSubscriberView(
                s.Email,
                s.Source,
                s.FirstSeenAt,
                s.LastSeenAt,
                s.UnsubscribedAt,
                UnsubscribeLink(mail.PublicAddress, s.UnsubscribeToken)))
            .ToList();

        return Results.Ok(new Page<AdminSubscriberView>(total, skip, take, items));
    }

    internal static async Task<IResult> CountsAsync([FromServices] CloudContext db, CancellationToken ct)
    {
        var bySource = await db.Subscribers
            .Where(s => s.Source != null)
            .GroupBy(s => s.Source!)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToDictionaryAsync(s => s.Source, s => s.Count, ct);

        var total = await db.Subscribers.CountAsync(ct);
        var gone = await db.Subscribers.CountAsync(s => s.UnsubscribedAt != null, ct);

        return Results.Ok(new SubscriberCounts(total, total - gone, gone, bySource));
    }

    /// <summary>
    /// Where an unsubscribe link points. The one place that spelling is written, so the page route
    /// and any message that carries the link cannot drift apart.
    /// </summary>
    public static string UnsubscribeLink(Uri publicAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(publicAddress);

        return new Uri(publicAddress, $"/unsubscribe?token={Uri.EscapeDataString(token)}").ToString();
    }
}
