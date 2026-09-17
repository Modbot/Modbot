using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Imports;

/// <summary>
/// What a record's <c>kind</c> becomes in the fact log (import design §3).
/// </summary>
/// <remarks>
/// <para>
/// A short word maps by the subject's platform: a <c>ban</c> of a VRChat user is the group ban
/// type, a <c>ban</c> of a Discord user is the Discord one. A kind with a dot in it is taken as
/// a Modbot fact type name and used as is when Modbot has it.
/// </para>
/// <para>
/// Anything else is <strong>kept, never dropped</strong>: written as
/// <see cref="FactType.Unrecognised"/> with the kind in <c>type_raw</c>, the same way foundation
/// §5.3.1 keeps a VRChat audit entry Modbot has no name for.
/// </para>
/// </remarks>
public static class ImportKinds
{
    private static readonly Dictionary<string, (string? VRChat, string? Discord)> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["join"] = (FactType.MemberJoined, FactType.DiscordMemberJoined),
        ["leave"] = (FactType.MemberLeft, FactType.DiscordMemberLeft),
        ["remove"] = (FactType.MemberKicked, null),
        ["kick"] = (FactType.GroupInstanceKick, FactType.DiscordMemberKicked),
        ["warn"] = (FactType.GroupInstanceWarn, null),
        ["ban"] = (FactType.MemberBanned, FactType.DiscordMemberBanned),
        ["unban"] = (FactType.MemberUnbanned, FactType.DiscordMemberUnbanned),
        ["timeout"] = (null, FactType.DiscordMemberTimedOut),
        ["note"] = (FactType.NoteAdded, FactType.NoteAdded),
        ["role-add"] = (FactType.RoleGranted, FactType.DiscordRoleGranted),
        ["role-remove"] = (FactType.RoleRevoked, FactType.DiscordRoleRevoked),
        ["invite"] = (FactType.InviteCreated, null),
        ["join-request"] = (FactType.JoinRequestCreated, null),
    };

    private static readonly HashSet<string> KnownTypes = new(FactType.All, StringComparer.Ordinal);

    /// <summary>The short words, for the docs and the tests.</summary>
    public static IReadOnlyCollection<string> ShortWords => Words.Keys;

    /// <summary>
    /// The fact type for a kind on a platform, and the raw kind when the type is
    /// <see cref="FactType.Unrecognised"/>.
    /// </summary>
    public static (string Type, string? TypeRaw) Resolve(string kind, FactPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var word = kind.Trim();

        if (Words.TryGetValue(word, out var pair))
        {
            var type = platform == FactPlatform.Discord ? pair.Discord : pair.VRChat;
            if (type is not null)
                return (type, null);
        }
        else if (word.Contains('.') && KnownTypes.Contains(word))
        {
            return (word, null);
        }

        return (FactType.Unrecognised, word);
    }
}
