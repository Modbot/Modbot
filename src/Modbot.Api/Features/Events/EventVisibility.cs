using Modbot.Analytics.Reports;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
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
/// <strong>Three narrowings.</strong> A fact about evidence additionally needs
/// <see cref="ModbotPermissions.ViewEvidence"/>, because its line names the file. Presence --
/// where somebody is standing -- additionally needs <see cref="ModbotPermissions.ViewLiveInstances"/>.
/// The audit log shows an instance join afterwards to anyone who reads it; a live feed says where
/// the person is now, which M3 section 7.4 made its own permission. And a member-report fact about
/// a staff account additionally needs <see cref="ModbotPermissions.ReviewTickets"/>, the same rule
/// the Reports page follows: that one depends on who the fact is about, not only on its type, so it
/// is <see cref="CanSeeAsync"/> that applies it (<see cref="MemberReportAccess"/>).
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

        if (!AuditVisibility.CanSeeType(held, type))
            return false;

        return !IsPresence(type) || ModbotAuth.Allows(held, ModbotPermissions.ViewLiveInstances);
    }

    /// <summary>
    /// <see cref="CanSee"/> for one fact about to be sent, with the rule that needs the fact: a
    /// member-report fact about a staff account is for those who hold Review tickets only. Looks
    /// nothing up for any other fact.
    /// </summary>
    public static async Task<bool> CanSeeAsync(
        ModbotContext db, ModbotPermissions held, ModbotEvent fact, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return CanSee(held, fact.Type) && !await MemberReportAccess.HidesAsync(db, held, fact, now, ct);
    }

    /// <summary>Whether this caller could be sent any event at all.</summary>
    public static bool SeesAnything(ModbotPermissions held)
        => AuditVisibility.CanSee(held, AuditCategory.Moderation)
           || AuditVisibility.CanSee(held, AuditCategory.Operational);
}
