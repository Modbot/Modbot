using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Analytics;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Reviews;

/// <summary>
/// Puts display names to VRChat ids for the review and repeat-offender screens.
/// </summary>
/// <remarks>
/// The stored profile first, because it is the name VRChat gave for the person most recently;
/// then whatever a fact recorded beside the id (<see cref="AnalyticsSql.NamesAsync"/>), for
/// people the profile sync has not reached yet. An id with no name anywhere shows as the id --
/// never dressed up as a name (spec 3.1.1).
/// </remarks>
internal static class PeopleNames
{
    public static async Task<IReadOnlyDictionary<string, string>> LookupAsync(
        ModbotContext db,
        IReadOnlyCollection<string> ids,
        CancellationToken ct)
    {
        var distinct = ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var names = new Dictionary<string, string>(
            await new AnalyticsSql(db).NamesAsync(distinct, ct),
            StringComparer.Ordinal);

        var profiles = await db.VRChatUsers.AsNoTracking()
            .Where(u => distinct.Contains(u.UserId) && u.DisplayName != null && u.DisplayName != "")
            .Select(u => new { u.UserId, u.DisplayName })
            .ToListAsync(ct);

        foreach (var profile in profiles)
            names[profile.UserId] = profile.DisplayName!;

        return names;
    }
}
