using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Events;

/// <summary>
/// Which facts a caller may be sent as live events (API keys design §4.3).
/// </summary>
/// <remarks>
/// <para>
/// The audit log's two-log split, unchanged: <see cref="AuditVisibility"/> decides moderation
/// against operational, so a type added to that table is classified for the live feed too.
/// </para>
/// <para>
/// <strong>One narrowing.</strong> Presence -- where somebody is standing -- additionally needs
/// <see cref="ModbotPermissions.ViewLiveInstances"/>. The audit log shows an instance join afterwards
/// to anyone who reads it; a live feed says where the person is now, which M3 section 7.4 made its
/// own permission.
/// </para>
/// </remarks>
public static class EventVisibility
{
    private static readonly string[] PresencePrefixes =
    [
        "vrchat.instance.",
        "vrchat.avatar.",
        "discord.voice.",
    ];

    public static bool IsPresence(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return PresencePrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal));
    }

    public static bool CanSee(ModbotPermissions held, string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!AuditVisibility.CanSee(held, AuditVisibility.CategoryOf(type)))
            return false;

        return !IsPresence(type) || ModbotAuth.Allows(held, ModbotPermissions.ViewLiveInstances);
    }

    /// <summary>Whether this caller could be sent any event at all.</summary>
    public static bool SeesAnything(ModbotPermissions held)
        => AuditVisibility.CanSee(held, AuditCategory.Moderation)
           || AuditVisibility.CanSee(held, AuditCategory.Operational);
}
