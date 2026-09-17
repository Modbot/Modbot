using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Shared.Names;
using Serilog;

namespace Modbot.Core.Names;

/// <summary>
/// Fills the searchable name columns for rows that do not have them: everything from before the
/// columns existed, and everything again after the folding rules change.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as the audit-log catch-up (member-and-ban sync design, <c>AuditLogCatchUpVersion</c>):
/// <c>settings.name_catch_up_version</c> records which <see cref="NameNormalizer.Version"/> made
/// the stored forms. Below the running build's version, every stored form is cleared and made
/// again; rows are then filled a batch at a time until none is left, and the version is written.
/// </para>
/// <para>
/// "Not filled" is a null searchable column beside a non-null name, so there is no second cursor
/// to keep: a batch is whatever is still null, and a pass that is interrupted simply carries on
/// next time. A name that is null has nothing to search and stays null.
/// </para>
/// </remarks>
public static class NameCatchUp
{
    public const int BatchSize = 500;

    /// <summary>
    /// Clears every stored form when the stored version is behind, and says whether it was.
    /// </summary>
    public static async Task<bool> RestartIfOutOfDateAsync(ModbotContext db, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(log);

        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings.NameCatchUpVersion >= NameNormalizer.Version)
            return false;

        log.Information(
            "Making every searchable name again: the stored forms are from version {Stored} of the name rules and this build carries {Current}",
            settings.NameCatchUpVersion,
            NameNormalizer.Version);

        await db.VRChatUsers
            .Where(u => u.DisplayNameSearchable != null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayNameSearchable, (string?)null), ct)
            .ConfigureAwait(false);

        await db.DiscordMembers
            .Where(m => m.UsernameSearchable != null || m.DisplayNameSearchable != null
                || m.GlobalNameSearchable != null || m.NicknameSearchable != null)
            .ExecuteUpdateAsync(m => m
                .SetProperty(x => x.UsernameSearchable, (string?)null)
                .SetProperty(x => x.DisplayNameSearchable, (string?)null)
                .SetProperty(x => x.GlobalNameSearchable, (string?)null)
                .SetProperty(x => x.NicknameSearchable, (string?)null), ct)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>Fills one batch of each table. Returns how many rows were written; zero means nothing is left.</summary>
    public static async Task<int> FillBatchAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var users = await db.VRChatUsers
            .Where(u => u.DisplayName != null && u.DisplayNameSearchable == null)
            .OrderBy(u => u.UserId)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var user in users)
            user.DisplayNameSearchable = NameNormalizer.Searchable(user.DisplayName);

        var members = await db.DiscordMembers
            .Where(m => m.UsernameSearchable == null
                || m.DisplayNameSearchable == null
                || (m.GlobalName != null && m.GlobalNameSearchable == null)
                || (m.Nickname != null && m.NicknameSearchable == null))
            .OrderBy(m => m.GuildId).ThenBy(m => m.UserId)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var member in members)
        {
            member.UsernameSearchable = NameNormalizer.Searchable(member.Username);
            member.DisplayNameSearchable = NameNormalizer.Searchable(member.DisplayName);
            member.GlobalNameSearchable = SearchableNamesInterceptor.SearchableOrNull(member.GlobalName);
            member.NicknameSearchable = SearchableNamesInterceptor.SearchableOrNull(member.Nickname);
        }

        if (users.Count + members.Count > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return users.Count + members.Count;
    }

    /// <summary>Records that every row now carries forms made by this build's rules.</summary>
    public static async Task MarkCompleteAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings.NameCatchUpVersion == NameNormalizer.Version)
            return;

        settings.NameCatchUpVersion = NameNormalizer.Version;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
