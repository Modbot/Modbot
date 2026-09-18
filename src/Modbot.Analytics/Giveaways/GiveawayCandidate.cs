using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Giveaways;

/// <summary>
/// One person a giveaway might let in, and everything about them that does not need counting.
/// </summary>
/// <remarks>
/// <para>
/// A person is a VRChat account, a Discord account, or the two of them linked. <see cref="Key"/>
/// names them the way the snapshot does — their VRChat id when Modbot knows one, their Discord id
/// otherwise — so the same person is one entrant however many rules ask about which side.
/// </para>
/// <para>
/// Every id here is opaque text stored exactly as it arrived, and every name is untrusted text
/// somebody chose for themselves (foundation §3.1.1).
/// </para>
/// </remarks>
public sealed class GiveawayCandidate
{
    /// <summary>How the snapshot names them: <c>vrchat:usr_…</c> or <c>discord:…</c>.</summary>
    public string Key => VRChatUserId is { } vrchat
        ? $"vrchat:{vrchat}"
        : $"discord:{DiscordUserId}";

    public string? VRChatUserId { get; set; }

    public string? DiscordUserId { get; set; }

    /// <summary>The name to show. Their VRChat display name, else the name Discord shows.</summary>
    public string? Name { get; set; }

    /// <summary>Both accounts are linked to each other right now.</summary>
    public bool Linked { get; set; }

    // ── The Discord server ───────────────────────────────────────────────────────────────

    public bool InDiscord { get; set; }

    public DateTimeOffset? DiscordJoinedAt { get; set; }

    public IReadOnlyList<string> DiscordRoles { get; set; } = [];

    // ── The VRChat group ─────────────────────────────────────────────────────────────────

    public bool InGroup { get; set; }

    public DateTimeOffset? GroupJoinedAt { get; set; }

    public IReadOnlyList<string> GroupRoles { get; set; } = [];

    /// <summary>Banned from the group right now.</summary>
    public bool Banned { get; set; }

    // ── The VRChat account itself ────────────────────────────────────────────────────────

    /// <summary>The day VRChat says the account was made, when Modbot has read the profile.</summary>
    public DateOnly? VRChatJoined { get; set; }

    /// <summary>They hold a Modbot account: one of the people running the giveaway.</summary>
    public bool Staff { get; set; }

    /// <summary>They have already won a giveaway on this Modbot.</summary>
    public bool WonBefore { get; set; }

    /// <summary>Whether this person's ids include the one given, on either side.</summary>
    public bool Is(string id) =>
        string.Equals(VRChatUserId, id, StringComparison.Ordinal)
        || string.Equals(DiscordUserId, id, StringComparison.Ordinal);

    /// <summary>The platform the snapshot records them on.</summary>
    public FactPlatform Platform => VRChatUserId is not null ? FactPlatform.VRChat : FactPlatform.Discord;

    /// <summary>The id the snapshot records them under.</summary>
    public string SubjectId => VRChatUserId ?? DiscordUserId ?? string.Empty;
}
