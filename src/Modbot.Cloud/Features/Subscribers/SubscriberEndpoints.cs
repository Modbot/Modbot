using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Registry;
using Npgsql;

namespace Modbot.Cloud.Features.Subscribers;

/// <param name="Email">The address the person typed.</param>
/// <param name="Source">Where they ticked the box, such as <c>account-registration</c>.</param>
public sealed record SubscribeRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("source")] string? Source);

public sealed record UnsubscribeRequest([property: JsonPropertyName("token")] string? Token);

/// <summary>
/// The limits on subscribing, one per process: per calling server and per address.
/// </summary>
/// <remarks>
/// Per server, so one Modbot cannot pour a list of addresses in. Per address, so the same address
/// cannot be pushed in over and over from many servers — a mailing list that anybody can add an
/// address to is a way to mail strangers, and both halves are needed to close it.
/// </remarks>
public sealed class SubscriberLimits(TimeProvider time)
{
    /// <summary>Far above the handful of people who register an account on one Modbot in an hour.</summary>
    public const int PerServerPerHour = 60;

    public const int PerAddressPerHour = 5;

    public WindowLimit Servers { get; } = new(PerServerPerHour, TimeSpan.FromHours(1), time);

    public WindowLimit Addresses { get; } = new(PerAddressPerHour, TimeSpan.FromHours(1), time);
}

/// <summary>
/// Collecting the addresses of people who asked to hear about new features and updates, and letting
/// them stop (register details spec 4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Subscribing is a Modbot server's call, not a browser's.</strong> It carries the server's
/// own registry credential, the same one its reports and its log batches carry, because it is the
/// same kind of call: a deployment telling Cloud something. Nothing here is open to a page.
/// </para>
/// <para>
/// <strong>Unsubscribing is open</strong>, and deliberately: somebody who wants out has an address
/// and a link, not an account, and asking them to make one first would be a reason to mark the mail
/// as spam instead.
/// </para>
/// <para>
/// Sending a newsletter is not built. This collects addresses and lets people leave; nothing here
/// sends anything.
/// </para>
/// </remarks>
public static class SubscriberEndpoints
{
    private const int TokenBytes = 32;

    public static IEndpointRouteBuilder MapSubscribers(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/subscribers", SubscribeAsync).RequireServer();

        // Open, and about one token only. Nothing in the request names an address.
        app.MapPost("/api/v1/subscribers/unsubscribe", UnsubscribeAsync);

        return app;
    }

    internal static async Task<IResult> SubscribeAsync(
        [FromBody] SubscribeRequest? request,
        [FromServices] CloudContext db,
        [FromServices] SubscriberLimits limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        var server = await ServerAccess.RequiredAsync(http);

        if (limits.Servers.TryTake(server.Id.ToString()) is { } byServer)
            return CloudError.TooMany(http, byServer, "Too many addresses from this server.");

        // Loose on purpose: an address is checked by sending mail to it, not by a pattern
        // (Cloud accounts and registry spec 2.2).
        if (!EmailAddress.TryNormalise(request?.Email, out var email))
            return Results.BadRequest(new { error = "That does not look like an email address." });

        if (limits.Addresses.TryTake(email) is { } byAddress)
            return CloudError.TooMany(http, byAddress, "Too many attempts for that address.");

        var now = time.GetUtcNow();
        var row = await db.Subscribers.FirstOrDefaultAsync(s => s.Email == email, ct);

        if (row is null)
        {
            row = new Subscriber
            {
                Email = email,
                UnsubscribeToken = NewToken(),
                FirstSeenAt = now,
            };
            db.Subscribers.Add(row);
        }

        row.Source = ClientText.Clean(request?.Source, Subscriber.MaxSourceLength) ?? row.Source;
        row.LastSeenAt = now;

        // Somebody who ticks the box again is asking again. The token stays as it was, so an
        // unsubscribe link from an older message still works.
        row.UnsubscribedAt = null;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two servers sent the same address at once. It is on the list either way.
            db.ChangeTracker.Clear();
        }

        return Results.Accepted();
    }

    internal static async Task<IResult> UnsubscribeAsync(
        [FromBody] UnsubscribeRequest? request,
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var token = request?.Token?.Trim();

        if (string.IsNullOrEmpty(token) || token.Length > 64)
            return Results.BadRequest(new { error = "That link is no longer good." });

        var row = await db.Subscribers.FirstOrDefaultAsync(s => s.UnsubscribeToken == token, ct);

        if (row is null)
            return Results.BadRequest(new { error = "That link is no longer good." });

        // Already out stays out, and says so rather than failing: somebody who clicks the link twice
        // is asking for the same thing twice.
        row.UnsubscribedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// The secret in an unsubscribe link: 32 random bytes, kept for the life of the row.
    /// </summary>
    /// <remarks>
    /// Stored rather than signed. A signature would need a key that must never change and must be
    /// set, and a Cloud whose key was rotated or never configured would have a mailing list nobody
    /// could leave — the one failure a mailing list must not have. A row that is deleted takes its
    /// link with it, which is the right answer too.
    /// </remarks>
    internal static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
}
