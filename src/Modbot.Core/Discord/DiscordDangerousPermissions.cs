namespace Modbot.Core.Discord;

/// <summary>
/// The Discord permissions the permission watch tells staff about when a role or a member gains
/// them, and the words it uses for them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The list is the ones that let somebody take a server over or silence it:</strong>
/// Administrator (everything), Manage Server, Manage Roles, Manage Channels, Manage Webhooks, Ban
/// Members, Kick Members, Mention Everyone and Manage Messages. A permission outside the list
/// (Moderate Members, Manage Events, Create Invite) is not watched.
/// </para>
/// <para>
/// Permissions are Discord's 64-bit set held in a signed number, as <c>DiscordRoleSnapshot</c> and
/// the stored role keep them; only whole bits are ever compared.
/// </para>
/// </remarks>
public static class DiscordDangerousPermissions
{
    public const long Administrator = 1L << 3;

    /// <summary>Each watched permission and its name, strongest first.</summary>
    public static IReadOnlyList<(long Bit, string Name)> All { get; } =
    [
        (Administrator, "Administrator"),
        (1L << 5, "Manage Server"),
        (1L << 28, "Manage Roles"),
        (1L << 4, "Manage Channels"),
        (1L << 29, "Manage Webhooks"),
        (1L << 2, "Ban Members"),
        (1L << 1, "Kick Members"),
        (1L << 17, "Mention Everyone"),
        (1L << 13, "Manage Messages"),
    ];

    /// <summary>Every watched bit together.</summary>
    public static long Mask { get; } = All.Aggregate(0L, (all, p) => all | p.Bit);

    /// <summary>The watched permissions in this set.</summary>
    public static long In(long permissions) => permissions & Mask;

    /// <summary>The watched permissions the new set has and the old one did not. A role never seen before had none.</summary>
    public static long Gained(long? before, long after) => In(after) & ~In(before ?? 0);

    /// <summary>The names of the watched permissions in this set, strongest first.</summary>
    public static IReadOnlyList<string> Names(long permissions)
        => [.. All.Where(p => (permissions & p.Bit) != 0).Select(p => p.Name)];
}
