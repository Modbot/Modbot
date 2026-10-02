namespace Modbot.Api.Features.DiscordReports;

/// <summary>The marks the roles report puts on a role, as the API sends them.</summary>
/// <remarks>
/// Words a program compares, in kebab case like the error codes; the web app names each one.
/// <see cref="All"/> is also the order a role's marks are listed in.
/// </remarks>
public static class RoleFlags
{
    /// <summary>Nobody in the server holds it.</summary>
    public const string NoMembers = "no-members";

    /// <summary>Another role has the same name, ignoring case and spaces at either end.</summary>
    public const string SameName = "same-name";

    /// <summary>
    /// Another role has the same permissions and the same colour: possibly the same role made twice.
    /// </summary>
    public const string SamePermissionsAndColour = "same-permissions-and-colour";

    /// <summary>Owned by a bot or an integration (a bot's own role, a booster or subscriber role).</summary>
    public const string BotRole = "bot-role";

    public static readonly IReadOnlyList<string> All = [NoMembers, SameName, SamePermissionsAndColour, BotRole];
}

/// <summary>One of the server's roles, with how many hold it and what the report marks on it.</summary>
/// <param name="Color">0xRRGGBB, zero for a role with no colour.</param>
/// <param name="Position">Discord's order: higher sits above lower.</param>
/// <param name="Members">
/// How many people in the server hold it. Null when the member list has never been read, so no count
/// can be trusted.
/// </param>
/// <param name="Flags">Any of <see cref="RoleFlags"/>, in the order <see cref="RoleFlags.All"/> lists them.</param>
/// <param name="SamePermissionsAndColourAs">
/// The names of the other roles that set <see cref="RoleFlags.SamePermissionsAndColour"/>, highest
/// first. Empty when that mark is not set.
/// </param>
public sealed record RoleReportRow(
    string Id,
    string Name,
    int Color,
    int Position,
    int? Members,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> SamePermissionsAndColourAs);

/// <param name="GuildId">The server in settings, or null when none is set.</param>
/// <param name="MembersListedAt">When the whole member list was first read. Null until then, and every count is null.</param>
/// <param name="RolesReadAt">When every role was last read in one go. Null until the first time.</param>
/// <param name="Roles">Every role but @everyone and roles since deleted, marked roles first.</param>
public sealed record RoleReport(
    string? GuildId,
    DateTimeOffset? MembersListedAt,
    DateTimeOffset? RolesReadAt,
    IReadOnlyList<RoleReportRow> Roles);

/// <summary>One text, announcement or forum channel and when anybody last wrote in it.</summary>
/// <param name="Type">text, announcement or forum.</param>
/// <param name="CategoryName">The category it sits under, or null for one at the top.</param>
/// <param name="StaffOnly">@everyone cannot see it. False when that is not known yet.</param>
/// <param name="CanRead">The bot may see the channel and read its history. When false, the last message is not known.</param>
/// <param name="StillReading">Modbot is still reading the channel's history back, so an older message may yet be found.</param>
/// <param name="LastMessageAt">
/// When the newest message Modbot has stored for the channel or any of its threads was sent. Null
/// when there is none, or when <see cref="CanRead"/> is false.
/// </param>
public sealed record QuietChannelRow(
    string Id,
    string Name,
    string Type,
    string? CategoryName,
    bool StaffOnly,
    bool CanRead,
    bool StillReading,
    DateTimeOffset? LastMessageAt);

/// <param name="GuildId">The server in settings, or null when none is set.</param>
/// <param name="Now">The server's clock (spec 4.4), so how long each channel has been quiet is worked out against it.</param>
/// <param name="Channels">Quietest first.</param>
public sealed record QuietChannelList(
    string? GuildId,
    DateTimeOffset Now,
    IReadOnlyList<QuietChannelRow> Channels);

/// <summary>
/// The two read-only reports for tidying a Discord server: its roles with their marks, and its
/// channels by how long they have been quiet (Discord tidy-up design). Pure, so the rules are tested
/// without a database; the endpoints only gather what goes in.
/// </summary>
public static class DiscordTidyUp
{
    /// <summary>One stored role, as the roles report needs it.</summary>
    /// <param name="Permissions">Discord's permission bits, or null when not read yet.</param>
    public sealed record RoleFacts(string Id, string Name, int Color, int Position, bool Managed, long? Permissions);

    /// <summary>One stored channel, as the quiet channels list needs it.</summary>
    /// <param name="EveryoneCanView">Null when not read yet.</param>
    public sealed record ChannelFacts(
        string Id,
        string Name,
        string Type,
        string? CategoryName,
        int Position,
        bool? EveryoneCanView,
        bool CanRead,
        bool StillReading,
        DateTimeOffset? LastMessageAt);

    /// <summary>
    /// Every role with its marks, marked roles first: the most marks first, then the fewest members,
    /// then Discord's own order.
    /// </summary>
    /// <param name="roles">The server's roles, without @everyone.</param>
    /// <param name="everyonePermissions">@everyone's permission bits, or null when not known.</param>
    /// <param name="members">How many people hold each role, or null when the member list was never read.</param>
    /// <remarks>
    /// <para>
    /// <strong>Same permissions and colour leaves plain roles out.</strong> A server's self-picked
    /// roles (pronouns, time zones, hobbies) usually have no colour and give nothing @everyone does
    /// not already have, so every one of them matches every other. Marking them all would bury the
    /// pairs that matter -- two coloured "Investor" roles, two staff roles with the same powers -- so
    /// a role with no colour and either no permissions or exactly @everyone's is never marked. Bot
    /// roles are left out of it too: they are marked as bot roles already, and thirty bots with the
    /// same permissions are not thirty duplicates.
    /// </para>
    /// <para>
    /// A role whose permissions are not known yet is never marked as matching: nothing can be said
    /// about it until the bot reads the server again.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RoleReportRow> Roles(
        IReadOnlyList<RoleFacts> roles,
        long? everyonePermissions,
        IReadOnlyDictionary<string, int>? members)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var byName = roles
            .GroupBy(r => NameKey(r.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        bool Comparable(RoleFacts r)
            => !r.Managed
               && r.Permissions is { } bits
               && !(r.Color == 0 && (bits == 0 || bits == everyonePermissions));

        var alike = roles
            .Where(Comparable)
            .GroupBy(r => (r.Color, r.Permissions!.Value))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(r => (Role: r, Others: g.Where(o => o.Id != r.Id).ToList())))
            .ToDictionary(x => x.Role.Id, x => x.Others, StringComparer.Ordinal);

        var rows = new List<RoleReportRow>(roles.Count);

        foreach (var role in roles)
        {
            int? count = members is null ? null : members.GetValueOrDefault(role.Id);
            var flags = new List<string>(RoleFlags.All.Count);

            if (count == 0)
                flags.Add(RoleFlags.NoMembers);

            if (byName[NameKey(role.Name)] > 1)
                flags.Add(RoleFlags.SameName);

            var others = alike.GetValueOrDefault(role.Id);
            if (others is not null)
                flags.Add(RoleFlags.SamePermissionsAndColour);

            if (role.Managed)
                flags.Add(RoleFlags.BotRole);

            rows.Add(new RoleReportRow(
                role.Id,
                role.Name,
                role.Color,
                role.Position,
                count,
                flags,
                others is null
                    ? []
                    : others.OrderByDescending(o => o.Position).Select(o => o.Name).ToList()));
        }

        return rows
            .OrderByDescending(r => r.Flags.Count)
            .ThenBy(r => r.Members ?? int.MaxValue)
            .ThenByDescending(r => r.Position)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The channels, quietest first: those with no message at all, then the longest since the last
    /// message, then those still being read back with nothing found yet, then those the bot cannot read.
    /// </summary>
    /// <param name="hideStaffOnly">Leave out channels @everyone cannot see. A channel not read yet counts as one everybody sees.</param>
    public static IReadOnlyList<QuietChannelRow> Channels(IReadOnlyList<ChannelFacts> channels, bool hideStaffOnly)
    {
        ArgumentNullException.ThrowIfNull(channels);

        return channels
            .Where(c => !hideStaffOnly || c.EveryoneCanView != false)
            .Select(c => (Facts: c, Row: new QuietChannelRow(
                c.Id,
                c.Name,
                c.Type,
                c.CategoryName,
                StaffOnly: c.EveryoneCanView == false,
                c.CanRead,
                c.StillReading,

                // What the bot once read in a channel it can no longer see would read as "quiet since
                // then", which is not what happened.
                c.CanRead ? c.LastMessageAt : null)))
            .OrderBy(x => Rank(x.Row))
            .ThenBy(x => x.Row.LastMessageAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Facts.Position)
            .ThenBy(x => x.Row.Id, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>
    /// Where a channel falls in the list: 0 nothing ever written, 1 a last message, 2 nothing found
    /// yet while still reading, 3 not readable.
    /// </summary>
    private static int Rank(QuietChannelRow row) => row switch
    {
        { CanRead: false } => 3,
        { LastMessageAt: not null } => 1,
        { StillReading: true } => 2,
        _ => 0,
    };

    private static string NameKey(string name) => name.Trim().ToUpperInvariant();
}
