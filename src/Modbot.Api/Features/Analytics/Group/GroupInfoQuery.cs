using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Analytics.Group;

/// <summary>
/// The top of the VRChat analytics page: the group as VRChat's own group page shows it.
/// </summary>
/// <remarks>
/// <para>
/// Read entirely from what the group-info sync left behind. The name, code, description and rules
/// are in the snapshot it keeps to tell a change from a restatement; the pictures, languages and
/// links are kept beside it on the settings row; the counts are the newest of the five-minute
/// readings. Opening the page never asks VRChat anything.
/// </para>
/// <para>
/// The counts fall back to the snapshot's when there is no reading at all, which on a running
/// deployment only happens between setup and the first poll. The snapshot is rewritten whenever
/// either count moves, so it is as current as the last poll either way.
/// </para>
/// </remarks>
public sealed class GroupInfoQuery(ModbotContext db)
{
    public async Task<GroupInfo> RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);
        var groupId = settings?.ManagedGroupId;

        var latest = string.IsNullOrWhiteSpace(groupId)
            ? null
            : await db.GroupMemberCounts.AsNoTracking()
                .Where(c => c.GroupId == groupId)
                .OrderByDescending(c => c.CountedAt)
                .FirstOrDefaultAsync(ct);

        return new GroupInfo(
            groupId,
            Blank(snapshot?.Name) ?? Blank(settings?.ManagedGroupName),
            Blank(snapshot?.ShortCode),
            Blank(snapshot?.Discriminator),
            Blank(settings?.ManagedGroupIconUrl),
            Blank(settings?.ManagedGroupBannerUrl),
            Blank(snapshot?.Description),
            Blank(snapshot?.Rules),
            settings?.ManagedGroupLanguages ?? [],
            settings?.ManagedGroupLinks ?? [],
            latest?.MemberCount ?? snapshot?.MemberCount,
            latest?.OnlineMemberCount ?? snapshot?.OnlineMemberCount,
            latest?.CountedAt,
            settings?.GroupInfoPolledAt,
            now);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
