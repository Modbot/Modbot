using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>
/// The <c>linked</c> filter both member lists take, and who may see links on them.
/// </summary>
/// <remarks>
/// <para>
/// The group's members and the Discord server's are separate lists, because most people are on one
/// side only and most never link. A moderator still wants to narrow either list to the people who
/// did link, or who did not, so both take the same words.
/// </para>
/// <para>
/// Seeing a link needs <see cref="ModbotPermissions.ViewProfile"/> (Discord account linking design
/// §11), and the lists need only <see cref="ModbotPermissions.ViewMembers"/>. So a list shows the
/// linked account only to a caller who holds both, and refuses the filter to anybody else rather
/// than answer a question about links through the back door of a row count.
/// </para>
/// </remarks>
public static class LinkFilter
{
    public const string Linked = "linked";
    public const string NotLinked = "not-linked";

    public const string Error = "`linked` is linked, not-linked or all.";

    public static bool IsValid(string? value)
        => Normalised(value) is null or "all" or Linked or NotLinked;

    /// <summary>The value, trimmed and lower-cased, or null when none was given.</summary>
    public static string? Normalised(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>True when the filter leaves people out, which is when it needs See profiles.</summary>
    public static bool Narrows(string? value)
        => Normalised(value) is Linked or NotLinked;

    public static bool SeesLinks(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ViewProfile);
    }
}
