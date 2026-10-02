namespace Modbot.Core.Data.Entities;

/// <summary>
/// The kinds of Discord channel Modbot keeps track of, as stored in <see cref="DiscordChannel.Type"/>.
/// </summary>
/// <remarks>
/// Text rather than an enum so the API and the web app read the same word the database holds.
/// Threads and server directories are left out: nothing is ever posted to or read back from them
/// by a setting.
/// </remarks>
public static class DiscordChannelTypes
{
    public const string Text = "text";
    public const string Announcement = "announcement";
    public const string Forum = "forum";
    public const string Media = "media";
    public const string Voice = "voice";
    public const string Stage = "stage";
    public const string Category = "category";
}

/// <summary>
/// The Discord server the bot serves, as it last saw it. The table is <c>discord_server</c>.
/// </summary>
/// <remarks>
/// <para>
/// Together with <see cref="DiscordChannel"/> and <see cref="DiscordRole"/> this is a copy of the
/// server's channel and role lists, kept in Postgres rather than read from the bot's memory, so
/// that the settings page can offer channels and roles to pick from while the bot is offline or
/// restarting. Settings keep storing the chosen id; these tables only put a name and the bot's
/// permissions beside it.
/// </para>
/// <para>
/// One row per guild id the bot has served. Only the row for the guild in settings is ever shown;
/// a row for an old guild id is left alone rather than deleted, like the removed channels are.
/// </para>
/// </remarks>
public class DiscordServer
{
    /// <summary>Discord's id for the server. Stored as text, never parsed.</summary>
    public string GuildId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The server's icon on Discord's picture site, or null when it has none.</summary>
    /// <remarks>
    /// This and the three below come with the server when the bot signs in and whenever the server
    /// changes, so reading them costs no request. The Discord analytics page shows them at the top.
    /// </remarks>
    public string? IconUrl { get; set; }

    /// <summary>The server's banner, or null when it has none. Only boosted servers can set one.</summary>
    public string? BannerUrl { get; set; }

    /// <summary>How many boosts the server has. Null until the bot has read it.</summary>
    public int? BoostCount { get; set; }

    /// <summary>The server's boost level, 0 to 3, as Discord states it. Null until the bot has read it.</summary>
    /// <remarks>
    /// Kept as Discord gives it rather than worked out from <see cref="BoostCount"/>: Discord lets a
    /// server keep its level for a while after boosts run out.
    /// </remarks>
    public int? BoostLevel { get; set; }

    /// <summary>Whether the bot holds View Audit Log for the whole server.</summary>
    public bool BotCanViewAuditLog { get; set; }

    /// <summary>Whether the bot holds Manage Roles for the whole server.</summary>
    public bool BotCanManageRoles { get; set; }

    /// <summary>Whether the bot holds Manage Events for the whole server, which calendar events need.</summary>
    public bool BotCanManageEvents { get; set; }

    /// <summary>Whether the bot holds Ban Members, which copying a ban into Discord needs.</summary>
    public bool BotCanBanMembers { get; set; }

    /// <summary>Whether the bot holds Kick Members, which removing somebody from the server needs.</summary>
    public bool BotCanRemoveMembers { get; set; }

    /// <summary>
    /// Whether the bot holds Mention @everyone, @here and All Roles, which lets it ping a role that
    /// is not open to mentions (calendar design §3.3.1). The bot never pings @everyone or @here.
    /// </summary>
    public bool BotCanMentionEveryone { get; set; }

    /// <summary>When every channel and role was last read in one go -- on sign-in and on resume.</summary>
    public DateTimeOffset RefreshedAt { get; set; }

    /// <summary>When anything in this server's lists last changed, from a full read or a single event.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The newest audit log entry the bot has read. The next read starts after it, so an entry is
    /// recorded once whether it arrived live or was caught up after a disconnect.
    /// </summary>
    public string? AuditLogReadThrough { get; set; }

    public DateTimeOffset? AuditLogReadAt { get; set; }

    /// <summary>
    /// The last moment the bot is known to have been connected and listening. After a restart or a
    /// long disconnect, anything found changed happened between this and now.
    /// </summary>
    public DateTimeOffset? SeenThrough { get; set; }

    /// <summary>
    /// When the whole member list was first read. Before it, a member the bot has no row for is not
    /// a new join -- they were there all along.
    /// </summary>
    public DateTimeOffset? MembersListedAt { get; set; }

    /// <summary>
    /// When the whole ban list was last read from Discord. Null until the bot has read it, which
    /// needs Ban Members. Read on sign-in and again once a day (<see cref="DiscordBan"/>).
    /// </summary>
    public DateTimeOffset? BansListedAt { get; set; }
}

/// <summary>
/// One channel in the bot's server, with what the bot may do there. The table is
/// <c>discord_channel</c>.
/// </summary>
/// <remarks>
/// The permissions are the bot's <em>effective</em> ones: the server-wide role permissions with the
/// category's and the channel's overwrites applied, which is the only answer to "can the bot post
/// here" that matters. A channel deleted in Discord keeps its row with <see cref="RemovedAt"/> set,
/// so a setting that still names it can say which channel it was.
/// </remarks>
public class DiscordChannel
{
    /// <summary>Discord's id for the channel. Unique across Discord, so it is the key on its own.</summary>
    public string ChannelId { get; set; } = string.Empty;

    public string GuildId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>One of <see cref="DiscordChannelTypes"/>.</summary>
    public string Type { get; set; } = DiscordChannelTypes.Text;

    /// <summary>The category the channel sits under, or null for one at the top.</summary>
    public string? CategoryId { get; set; }

    /// <summary>Discord's sort order within the channel list.</summary>
    public int Position { get; set; }

    public bool Nsfw { get; set; }

    public bool BotCanView { get; set; }

    public bool BotCanReadHistory { get; set; }

    public bool BotCanSend { get; set; }

    public bool BotCanEmbedLinks { get; set; }

    public bool BotCanAttachFiles { get; set; }

    public bool BotCanManageMessages { get; set; }

    /// <summary>
    /// Whether the @everyone role may see the channel: the channel's own overwrite for @everyone,
    /// else @everyone's server-wide permission. False is a staff-only channel. Null until the bot
    /// has read the channel since this was added.
    /// </summary>
    /// <remarks>
    /// The channel's own overwrites are enough: Discord copies a category's overwrites into a
    /// channel when it is synced, and a category never changes what its channels allow by itself.
    /// Read for the quiet channels list, which can leave staff-only channels out (Discord tidy-up
    /// design §3).
    /// </remarks>
    public bool? EveryoneCanView { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the channel was found to be gone from Discord, or null while it exists.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>
/// One role in the bot's server. The table is <c>discord_role</c>.
/// </summary>
public class DiscordRole
{
    /// <summary>Discord's id for the role. The @everyone role's id is the server's id.</summary>
    public string RoleId { get; set; } = string.Empty;

    public string GuildId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The role's colour as 0xRRGGBB. Zero means the role has no colour of its own.</summary>
    public int Color { get; set; }

    /// <summary>Discord's order: higher sits above lower, and @everyone is 0.</summary>
    public int Position { get; set; }

    /// <summary>
    /// Owned by a bot or an integration (a booster role, a bot's own role). Nobody can hand these
    /// out, the bot included.
    /// </summary>
    public bool Managed { get; set; }

    public bool Everyone { get; set; }

    /// <summary>
    /// Whether the bot could give this role to somebody: it holds Manage Roles, the role sits below
    /// the bot's highest role, and the role is neither managed nor @everyone.
    /// </summary>
    public bool BotCanAssign { get; set; }

    /// <summary>
    /// The role's server-wide permissions, Discord's bitfield as it sends it. Null until the bot has
    /// read the role since this was added.
    /// </summary>
    /// <remarks>
    /// Only ever compared whole, never read bit by bit: the roles report puts two roles side by side
    /// when their permissions and colour are the same (Discord tidy-up design §2). Discord sends it
    /// as an unsigned 64-bit number; it is kept as the same 64 bits in a signed column.
    /// </remarks>
    public long? Permissions { get; set; }

    /// <summary>
    /// "Allow anyone to @mention this role" is on in Discord, so anybody's mention of it pings,
    /// the bot's included.
    /// </summary>
    public bool Mentionable { get; set; }

    /// <summary>
    /// Whether a mention of this role by the bot pings: the role is open to mentions, or the bot
    /// holds Mention @everyone, @here and All Roles. Never @everyone itself.
    /// </summary>
    public static bool BotCanMention(DiscordRole role, DiscordServer? server)
    {
        ArgumentNullException.ThrowIfNull(role);
        return !role.Everyone && role.RoleId != role.GuildId && (role.Mentionable || server is { BotCanMentionEveryone: true });
    }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the role was found to be gone from Discord, or null while it exists.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}
