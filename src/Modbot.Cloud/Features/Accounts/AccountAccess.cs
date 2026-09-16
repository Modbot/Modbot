using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// Everything an account does for itself: the session cookie, and nothing else.
/// </summary>
/// <remarks>
/// An account session opens nothing under <c>/api/admin</c>, and <c>ROOT_API_KEY</c> is not an
/// account. The two are separate powers and neither stands in for the other.
/// </remarks>
public static class AccountAccess
{
    private const string ItemKey = "modbot.account";

    /// <summary>Refuses anyone without a live session, and remembers the account for the endpoint.</summary>
    public static TBuilder RequireAccount<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            if (await ReadAsync(http) is null)
                return Results.Json(new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized);

            return await next(context);
        });

    /// <summary>The signed-in account, or null. Read once per request and remembered.</summary>
    public static async Task<Account?> ReadAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.Items.TryGetValue(ItemKey, out var known))
            return known as Account;

        var services = http.RequestServices;
        var account = http.Request.Cookies.TryGetValue(AccountSessions.CookieName, out var token)
            ? await services.GetRequiredService<AccountSessions>()
                .ReadAsync(services.GetRequiredService<CloudContext>(), token, http.RequestAborted)
            : null;

        http.Items[ItemKey] = account;
        return account;
    }

    /// <summary>The signed-in account, for an endpoint behind <see cref="RequireAccount"/>.</summary>
    public static async Task<Account> RequiredAsync(HttpContext http) =>
        await ReadAsync(http) ?? throw new InvalidOperationException("This endpoint needs RequireAccount.");
}
