namespace Modbot.Core.Data.Entities;

/// <summary>
/// The things Modbot can email somebody about when they go wrong.
/// </summary>
/// <remarks>
/// <para>
/// One name per check, stored as text so a new check never renumbers the ones already written, and
/// so a deployment whose database was written by a newer Modbot keeps rows it does not understand
/// rather than losing them.
/// </para>
/// <para>
/// <strong>These are not the unusual-activity alerts</strong> (AI insights design §8). Those watch
/// what the group is doing — a burst of joins, a run of flags — and are a moderation tool. These
/// watch whether Modbot itself is working, and go to whoever keeps it running.
/// </para>
/// <para>
/// <strong>Modbot cannot watch itself being down.</strong> A deployment whose database is gone, or
/// whose process is not running, cannot notice that and cannot send mail about it. That is the
/// half Modbot Cloud watches, from outside, and why the two halves both exist.
/// </para>
/// </remarks>
public static class HealthChecks
{
    /// <summary>Modbot is not reaching VRChat and waiting will not fix it, or it has been waiting a long time.</summary>
    public const string VRChat = "vrchat";

    /// <summary>The Discord bot is disconnected or stopped.</summary>
    public const string DiscordBot = "discord-bot";

    /// <summary>A sync job has not completed a pass for hours while a group is set up.</summary>
    public const string Sync = "sync";

    /// <summary>The database has grown past the size somebody asked to be told about.</summary>
    public const string Storage = "storage";

    /// <summary>An AI spending limit has been reached.</summary>
    public const string AiSpend = "ai-spend";

    /// <summary>Email is failing, or has been queued for hours.</summary>
    public const string Email = "email";

    /// <summary>Sending logs to Modbot Cloud has been failing for hours.</summary>
    public const string LogShipping = "log-shipping";

    /// <summary>In the order the settings card lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [VRChat, DiscordBot, Sync, Storage, AiSpend, Email, LogShipping];

    public static string LabelOf(string check) => check switch
    {
        VRChat => "VRChat",
        DiscordBot => "Discord bot",
        Sync => "Sync",
        Storage => "Storage",
        AiSpend => "AI spend",
        Email => "Email",
        LogShipping => "Logs to Modbot Cloud",
        _ => check,
    };

    public static bool IsKnown(string? check) =>
        check is not null && All.Contains(check, StringComparer.Ordinal);
}

/// <summary>
/// One check: whether it is watched, and what it last said.
/// </summary>
/// <remarks>
/// The state is the row. A problem emails once and then stays quiet for the quiet time; a recovery
/// emails once and clears the row. Keeping it in the database rather than in memory means a restart
/// in the middle of a problem does not start the whole thing again — which is the failure mode that
/// makes people turn alerts off.
/// </remarks>
public class HealthWatch
{
    /// <summary><see cref="HealthChecks"/>.</summary>
    public string Check { get; set; } = string.Empty;

    /// <summary>Whether this check emails anybody. Everything is off until somebody turns it on.</summary>
    public bool On { get; set; }

    /// <summary>True while the check is failing.</summary>
    public bool Problem { get; set; }

    /// <summary>When it started failing.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>What was wrong, in one sentence, as the last email said it.</summary>
    public string? Detail { get; set; }

    /// <summary>When the last email about this check went out. Null means none has.</summary>
    public DateTimeOffset? LastSentAt { get; set; }
}

/// <summary>Whether Modbot emails about its own health at all, and how often it may repeat itself.</summary>
public class HealthAlertSettings
{
    public const int DefaultQuietHours = 6;

    /// <summary>A week. Past this a "quiet time" is really "off", and turning it off is the honest control.</summary>
    public const int MaxQuietHours = 168;

    public int Id { get; set; } = 1;

    /// <summary>
    /// How long a check stays quiet after an email about it. A problem that is still there when the
    /// quiet time is up is emailed again, because a problem nobody fixed is still a problem.
    /// </summary>
    public int QuietHours { get; set; } = DefaultQuietHours;

    /// <summary>
    /// The size the database has to pass for the storage check to fire. 0 turns that check off.
    /// </summary>
    /// <remarks>
    /// Its own number rather than the disk size on the Data settings screen, on purpose. That one is
    /// a what-if input the browser remembers and Modbot never stores (see <c>DataSettingsEndpoints</c>);
    /// this one is a line somebody wants to be told about, which is a different thing and belongs to
    /// the alert that reads it.
    /// </remarks>
    public long StorageWarnBytes { get; set; }

    /// <summary>When the checks last ran. Claimed before anything is read, so two copies do not both run them.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>A staff account that gets the health emails.</summary>
/// <remarks>
/// Chosen accounts, not every administrator: the person who keeps the server running is often not
/// the person who moderates, and mail nobody wanted is mail everybody filters.
/// </remarks>
public class HealthAlertRecipient
{
    public Guid UserId { get; set; }

    public ModbotUser User { get; set; } = null!;
}
