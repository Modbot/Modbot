using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Modbot.Core.Data.Entities;
using Modbot.Shared.Names;

namespace Modbot.Core.Data;

/// <summary>
/// Writes the searchable form of every display name and username beside the name, on every save.
/// </summary>
/// <remarks>
/// <para>
/// A VRChat display name is written by the profile sync, the demo seeder and the account link; a
/// Discord member's four names by the gateway, the member sweep and the seeder. Each of those
/// would have to remember to write the searchable column too, and the one that forgot would leave
/// a person the search box cannot find. Doing it here, where every writer goes through, means
/// nothing has to remember.
/// </para>
/// <para>
/// Only a row whose name changed is touched, so a save that updates a timestamp does not rewrite
/// the column. <c>ExecuteUpdate</c> bypasses interceptors, and none of Modbot's touch a name; the
/// name catch-up covers rows from before the columns existed.
/// </para>
/// </remarks>
public sealed class SearchableNamesInterceptor : SaveChangesInterceptor
{
    /// <summary>One instance for every context, as with <see cref="NullCharacterInterceptor"/>.</summary>
    public static readonly SearchableNamesInterceptor Instance = new();

    private SearchableNamesInterceptor() { }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Fill(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Fill(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Fill(DbContext? context)
    {
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
                continue;

            switch (entry.Entity)
            {
                case VRChatUser user:
                    if (Changed(entry, nameof(VRChatUser.DisplayName)))
                        user.DisplayNameSearchable = SearchableOrNull(user.DisplayName);
                    break;

                case DiscordMember member:
                    if (Changed(entry, nameof(DiscordMember.Username)))
                        member.UsernameSearchable = SearchableOrNull(member.Username);
                    if (Changed(entry, nameof(DiscordMember.DisplayName)))
                        member.DisplayNameSearchable = SearchableOrNull(member.DisplayName);
                    if (Changed(entry, nameof(DiscordMember.GlobalName)))
                        member.GlobalNameSearchable = SearchableOrNull(member.GlobalName);
                    if (Changed(entry, nameof(DiscordMember.Nickname)))
                        member.NicknameSearchable = SearchableOrNull(member.Nickname);
                    break;
            }
        }
    }

    private static bool Changed(EntityEntry entry, string property)
        => entry.State is EntityState.Added || entry.Property(property).IsModified;

    /// <summary>The searchable form, or null for a null name -- a null name has nothing to match.</summary>
    public static string? SearchableOrNull(string? name)
        => name is null ? null : NameNormalizer.Searchable(name);
}
