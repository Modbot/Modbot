namespace Modbot.Core.Discord;

/// <summary>
/// Every Discord permission Modbot can name, with the words Discord uses for it in its own screens.
/// </summary>
/// <remarks>
/// Used to say what changed between two server snapshots and what a restore would change, in words
/// instead of bit numbers. A bit not in the list is shown as "Permission 47", so a permission
/// Discord adds later still shows up in a comparison. Permissions are Discord's set held in a signed
/// number, as the rest of Modbot keeps them.
/// </remarks>
public static class DiscordPermissionNames
{
    private static readonly (int Bit, string Name)[] Known =
    [
        (0, "Create Invite"),
        (1, "Kick Members"),
        (2, "Ban Members"),
        (3, "Administrator"),
        (4, "Manage Channels"),
        (5, "Manage Server"),
        (6, "Add Reactions"),
        (7, "View Audit Log"),
        (8, "Priority Speaker"),
        (9, "Video"),
        (10, "View Channel"),
        (11, "Send Messages"),
        (12, "Send Text-to-Speech Messages"),
        (13, "Manage Messages"),
        (14, "Embed Links"),
        (15, "Attach Files"),
        (16, "Read Message History"),
        (17, "Mention Everyone"),
        (18, "Use External Emoji"),
        (19, "View Server Insights"),
        (20, "Connect"),
        (21, "Speak"),
        (22, "Mute Members"),
        (23, "Deafen Members"),
        (24, "Move Members"),
        (25, "Use Voice Activity"),
        (26, "Change Nickname"),
        (27, "Manage Nicknames"),
        (28, "Manage Roles"),
        (29, "Manage Webhooks"),
        (30, "Manage Expressions"),
        (31, "Use Application Commands"),
        (32, "Request to Speak"),
        (33, "Manage Events"),
        (34, "Manage Threads"),
        (35, "Create Public Threads"),
        (36, "Create Private Threads"),
        (37, "Use External Stickers"),
        (38, "Send Messages in Threads"),
        (39, "Use Activities"),
        (40, "Timeout Members"),
        (41, "View Creator Monetization Analytics"),
        (42, "Use Soundboard"),
        (43, "Create Expressions"),
        (44, "Create Events"),
        (45, "Use External Sounds"),
        (46, "Send Voice Messages"),
        (48, "Set Voice Channel Status"),
        (49, "Send Polls"),
        (50, "Use External Apps"),
        (51, "Pin Messages"),
        (52, "Bypass Slowmode"),
    ];

    /// <summary>The names of the permissions in this set, in Discord's own order.</summary>
    public static IReadOnlyList<string> Of(long permissions)
    {
        var names = new List<string>();

        for (var bit = 0; bit < 63; bit++)
        {
            if ((permissions & (1L << bit)) == 0)
                continue;

            var known = Array.Find(Known, k => k.Bit == bit);
            names.Add(known.Name ?? $"Permission {bit}");
        }

        return names;
    }

    /// <summary>The names as one line, or "none" for an empty set.</summary>
    public static string Line(long permissions)
        => permissions == 0 ? "none" : string.Join(", ", Of(permissions));
}
