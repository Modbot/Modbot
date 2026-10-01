using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Modbot.Api.Conventions;

/// <summary>
/// One name for one kind of query parameter across the API (API conventions design §3):
/// <c>search</c> for free text, <c>status</c> for which ones, <c>vrchatUserId</c> and
/// <c>discordUserId</c> for a person.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints grew their own words for these over time -- <c>q</c>, <c>text</c>, <c>state</c>,
/// <c>id</c>, <c>userId</c>, <c>vrchat</c> -- and the web app and existing scripts send those, so
/// they keep working exactly as they did. What this adds is the one name beside each: a request
/// that sends the common name and not the endpoint's own has it copied across before the endpoint
/// reads its parameters. The endpoint's own name wins when both are sent.
/// </para>
/// <para>
/// The OpenAPI document names the common one (<see cref="OpenApiReference"/>), so a generated
/// client learns one word per idea. A table rather than an attribute on each endpoint, so the whole
/// grammar can be read in one place, and so adding a name never means touching a feature.
/// </para>
/// <para>
/// Only where the parameter means exactly the common name: <c>/api/notes</c>' <c>userId</c> and the
/// audit log's <c>person</c> take a Discord or Modbot id too, so they are not VRChat ids and are
/// not listed.
/// </para>
/// </remarks>
public static class QueryAliases
{
    public const string Search = "search";
    public const string Status = "status";
    public const string VRChatUserId = "vrchatUserId";
    public const string DiscordUserId = "discordUserId";

    /// <summary>For each path, each common name and the name the endpoint itself reads.</summary>
    public static IReadOnlyDictionary<string, (string Common, string Own)[]> Table { get; } =
        new Dictionary<string, (string Common, string Own)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/search"] = [(Search, "q")],
            ["/api/audit"] = [(Search, "q")],
            ["/api/logs"] = [(Search, "text")],

            ["/api/moderation-flags"] = [(Status, "state"), (VRChatUserId, "vrchat"), (DiscordUserId, "discord")],
            ["/api/discord/members"] = [(Status, "state")],
            ["/api/reviews"] = [(Status, "state")],
            ["/api/giveaways"] = [(Status, "state")],

            ["/api/vrchat-users/profile"] = [(VRChatUserId, "id")],
            ["/api/vrchat-users/metrics"] = [(VRChatUserId, "id")],
            ["/api/vrchat-users/refresh"] = [(VRChatUserId, "id")],
            ["/api/vrchat-users/age-verified"] = [(VRChatUserId, "id")],
            ["/api/vrchat-users/history"] = [(VRChatUserId, "id")],
            ["/api/vrchat-users/raw"] = [(VRChatUserId, "id")],
            ["/api/members/membership"] = [(VRChatUserId, "id")],
            ["/api/repeat-offenders/one"] = [(VRChatUserId, "id")],
            ["/api/cases"] = [(VRChatUserId, "userId")],
            ["/api/cases/lookup"] = [(VRChatUserId, "userId")],
            ["/api/watches/person"] = [(VRChatUserId, "vrchat"), (DiscordUserId, "discord")],
        };

    /// <summary>The aliases for a path, or none.</summary>
    public static (string Common, string Own)[] For(string? path)
        => path is not null && Table.TryGetValue(path.TrimEnd('/'), out var aliases) ? aliases : [];

    public static IApplicationBuilder UseQueryAliases(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use((context, next) =>
        {
            Apply(context.Request);
            return next(context);
        });
    }

    /// <summary>Copies each common name the request sent to the endpoint's own, where the own is missing.</summary>
    public static void Apply(HttpRequest request)
    {
        var aliases = For(request.Path.Value);
        if (aliases.Length == 0 || request.Query.Count == 0)
            return;

        Dictionary<string, StringValues>? copy = null;

        foreach (var (common, own) in aliases)
        {
            if (request.Query.ContainsKey(own) || !request.Query.TryGetValue(common, out var values))
                continue;

            copy ??= request.Query.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            copy[own] = values;
        }

        if (copy is not null)
            request.Query = new QueryCollection(copy);
    }
}
