using Modbot.Core.Data;
using Modbot.Core.Time;

namespace Modbot.Core.Updates;

/// <param name="Running">The release this server is running.</param>
/// <param name="Newest">The newest release there is, or null when Modbot has not been told yet.</param>
/// <param name="NewerAvailable">True when <see cref="Newest"/> is later than <see cref="Running"/>.</param>
/// <param name="PublishedAt">When the newest release was published.</param>
/// <param name="NotesUrl">The page with its notes on it.</param>
/// <param name="Image">The image to pull for it.</param>
/// <param name="Tag">The tag to pull it with.</param>
/// <param name="CheckedAt">When the last check was made.</param>
/// <param name="Problem">One sentence about the last failed check, or null.</param>
/// <param name="On">Whether this server checks at all.</param>
public sealed record UpdateStatus(
    string Running,
    string? Newest,
    bool NewerAvailable,
    DateTimeOffset? PublishedAt,
    string? NotesUrl,
    string? Image,
    string? Tag,
    DateTimeOffset? CheckedAt,
    string? Problem,
    bool On);

/// <summary>
/// Asks what the newest release is and writes the answer on the settings row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Modbot never updates itself and never pulls an image.</strong> It says that a newer
/// version exists and what to pull; replacing a running deployment is the operator's decision and
/// their host's job. A server that swapped its own code would be a server whose operator cannot say
/// what is running, and on a schedule nobody chose.
/// </para>
/// <para>
/// Scoped, because it reads and writes the database. <see cref="UpdateCheckService"/> calls it on a
/// schedule and the settings endpoint reads what it stored.
/// </para>
/// </remarks>
public sealed class UpdateChecker(ModbotContext db, UpdateCheckClient client, IModbotClock clock)
{
    /// <summary>
    /// Asks, if the operator has left checking on, and records what came back. Returns what the
    /// settings screen should show either way.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.CheckForUpdates)
            return Read(settings);

        var (release, problem) = await client.ReadAsync(ct).ConfigureAwait(false);

        settings.UpdateCheckedAt = clock.UtcNow;
        settings.UpdateCheckProblem = problem;

        // A failed check keeps what was last known rather than blanking it. "We could not ask
        // today" is not the same as "there is no newer version", and the screen must not say the
        // second when it means the first.
        if (release is not null)
        {
            settings.NewestRelease = release.Version;
            settings.NewestReleaseAt = release.PublishedAt;
            settings.NewestReleaseNotesUrl = release.NotesUrl;
            settings.NewestReleaseImage = release.Image;
            settings.NewestReleaseTag = release.Tag;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Read(settings);
    }

    /// <summary>What was last learned, without asking anything.</summary>
    public async Task<UpdateStatus> ReadAsync(CancellationToken ct) =>
        Read(await db.GetSettingsAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Turns checking on or off. Off clears what was last learned, so a screen never shows an
    /// answer from a check the operator has since forbidden.
    /// </summary>
    public async Task<UpdateStatus> SetAsync(bool on, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.CheckForUpdates == on)
            return Read(settings);

        settings.CheckForUpdates = on;

        if (!on)
        {
            settings.NewestRelease = null;
            settings.NewestReleaseAt = null;
            settings.NewestReleaseNotesUrl = null;
            settings.NewestReleaseImage = null;
            settings.NewestReleaseTag = null;
            settings.UpdateCheckProblem = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Read(settings);
    }

    private static UpdateStatus Read(Data.Entities.Settings settings) => new(
        ModbotVersion.Release,
        settings.NewestRelease,
        ReleaseVersion.IsNewer(ModbotVersion.Release, settings.NewestRelease),
        settings.NewestReleaseAt,
        settings.NewestReleaseNotesUrl,
        settings.NewestReleaseImage,
        settings.NewestReleaseTag,
        settings.UpdateCheckedAt,
        settings.UpdateCheckProblem,
        settings.CheckForUpdates);
}
