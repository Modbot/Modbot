using Modbot.Core.Time;

namespace Modbot.Companion.Clips;

/// <summary>One saved clip sitting in the clips folder.</summary>
public readonly record struct SavedClip(string Path, long Bytes, DateTimeOffset SavedAt);

/// <summary>
/// The clips folder's contents: what is in it, and what has to go when it gets too big.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> One folder — the clips folder chosen on the
/// settings screen — and inside it only the <c>.mp4</c> files Modbot itself wrote. It lists them to
/// add up how much room they take, and deletes the oldest of them when saving another would put the
/// folder over the limit in settings. It never opens a clip, never reads any other kind of file, and
/// never touches anything outside that one folder.</para>
/// <para><strong>Nothing leaves the machine.</strong> No clip, no file name and no count is sent
/// anywhere. Attaching a clip to a case is something a moderator does afterwards, in Modbot's web
/// interface, in a browser, by choosing the file — the client has no way to upload one and does not
/// gain one here (clips design spec, §6).</para>
/// <para><strong>Why anything is deleted at all.</strong> Video is large and a rolling recorder that
/// only ever adds will eventually fill a moderator's disk, which would be Modbot breaking the
/// machine it was installed to help. The limit is in settings, the oldest go first, and a clip is
/// never deleted to make room for itself.</para>
/// </remarks>
public sealed class ClipLibrary
{
    /// <summary>The only kind of file Modbot writes here, and the only kind it counts or deletes.</summary>
    public const string ClipExtension = ".mp4";

    private readonly IModbotClock _clock;

    public ClipLibrary(IModbotClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The clips in the folder, oldest first. An unreadable or missing folder is empty, not an
    /// error: the moderator may have moved or deleted it while the client was running.
    /// </summary>
    public IReadOnlyList<SavedClip> List(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var clips = new List<SavedClip>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!string.Equals(Path.GetExtension(path), ClipExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var file = new FileInfo(path);
                    clips.Add(new SavedClip(path, file.Length, file.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted or locked between listing and asking. Not worth a word.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        clips.Sort((a, b) => a.SavedAt.CompareTo(b.SavedAt));
        return clips;
    }

    /// <summary>How much room the clips in the folder take, in bytes.</summary>
    public long Bytes(string folder)
    {
        long total = 0;
        foreach (var clip in List(folder))
            total += clip.Bytes;

        return total;
    }

    /// <summary>
    /// Deletes the oldest clips until the folder, plus <paramref name="aboutToAdd"/> more bytes,
    /// fits inside the limit. Returns how many were deleted.
    /// </summary>
    /// <remarks>
    /// The newest clip is never deleted, however large it is. A limit smaller than one clip would
    /// otherwise delete the thing that was just saved, which is the one outcome a moderator who
    /// pressed Save must not get.
    /// </remarks>
    public int MakeRoom(string folder, long limitBytes, long aboutToAdd = 0)
    {
        if (limitBytes <= 0)
            return 0;

        var clips = List(folder);
        if (clips.Count == 0)
            return 0;

        long total = aboutToAdd;
        foreach (var clip in clips)
            total += clip.Bytes;

        var deleted = 0;

        // Oldest first, and never the last one standing.
        for (var index = 0; index < clips.Count - 1 && total > limitBytes; index++)
        {
            try
            {
                File.Delete(clips[index].Path);
                total -= clips[index].Bytes;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in a video player, most likely. Leave it and try the next one.
            }
        }

        return deleted;
    }

    /// <summary>
    /// What to call a clip saved now: the world, the instance number and the local date and time,
    /// joined with underscores — <c>The Black Cat_98874_2026-09-19 18-02-29.mp4</c>.
    /// </summary>
    /// <remarks>
    /// <para><strong>The world comes first because that is what a moderator remembers.</strong>
    /// They remember the world they were in and roughly when; they do not remember an instance
    /// number and they certainly do not remember <c>wrld_4cf554b4-430c-…</c>. So the name is the
    /// world's readable name when VRChat has said it, the world id when it has not, and neither
    /// when there is no instance at all — a clip saved in a world Modbot could not read is still a
    /// clip, and gets the moment on its own rather than nothing.</para>
    /// <para><strong>The instance number is taken, never checked.</strong> It is the part of the
    /// instance id before the first qualifier, because that is where VRChat puts it — and if there
    /// is nothing there, whatever the id is stands in. VRChat's ids follow no structure and are
    /// never validated for shape (foundation 3.1.1), so this takes what is in front of it and
    /// falls back rather than asserting anything.</para>
    /// <para><strong>The whole name is made safe, not the pieces.</strong> The template is built
    /// first and then run through <see cref="AsFileName"/> once, because it is the finished name
    /// that has to be a legal file name — a world name is whatever a person typed, an instance id
    /// carries brackets and tildes, and sanitising the halves separately leaves the joins to chance.
    /// A name that comes out of that empty falls back rather than producing a file called
    /// <c>.mp4</c>.</para>
    /// <para><strong>And it never lands on a clip that is already there.</strong> Two saves in the
    /// same second in the same instance would otherwise be one file; the second gets
    /// <c>(2)</c>.</para>
    /// </remarks>
    /// <param name="worldName">The world's readable name, as VRChat's log said it, or null.</param>
    /// <param name="worldId">The world's id, used when there is no readable name.</param>
    /// <param name="instanceId">The instance id, in full; the number is taken off the front.</param>
    /// <param name="folder">
    /// Where the clip is going, so an existing clip of the same name is not written over. Null
    /// skips that check, which is right when the caller has no folder to look in.
    /// </param>
    public string NameFor(
        string? worldName = null,
        string? worldId = null,
        string? instanceId = null,
        string? folder = null)
    {
        var world = Shorten(Blank(worldName) ? worldId : worldName, MostWorldCharacters);
        var instance = Shorten(InstanceNumber(instanceId), MostInstanceCharacters);
        var moment = _clock.UtcNow.ToLocalTime()
            .ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);

        var template = string.Join('_', new[] { world, instance, moment }.Where(part => part.Length > 0));

        var name = AsFileName(template);
        if (name.Length > MostNameCharacters)
            name = AsFileName(name[..MostNameCharacters]);

        return Unused(folder, name) + ClipExtension;
    }

    /// <summary>
    /// The instance number off the front of an instance id: <c>98874</c> out of
    /// <c>98874~group(grp_…)~region(use)</c>.
    /// </summary>
    /// <remarks>
    /// VRChat writes the number first and its qualifiers after a tilde, so the number is what is in
    /// front of the first tilde. Nothing here checks that what it found looks like a number, or
    /// like anything else: VRChat's ids follow no structure (foundation 3.1.1) and a group can set
    /// an instance id to any text it likes. An id that starts with a tilde leaves nothing in front
    /// of it, and then the whole id stands in rather than nothing.
    /// </remarks>
    public static string InstanceNumber(string? instanceId)
    {
        if (Blank(instanceId))
            return string.Empty;

        var whole = instanceId!.Trim();
        var qualifiers = whole.IndexOf('~', StringComparison.Ordinal);
        var front = (qualifiers < 0 ? whole : whole[..qualifiers]).Trim();

        return front.Length > 0 ? front : whole;
    }

    /// <summary>
    /// <paramref name="text"/> with everything a Windows file name cannot hold taken out.
    /// </summary>
    /// <remarks>
    /// <para>The list is written out rather than asked of the operating system, because the answer
    /// has to be the same everywhere: Linux calls almost everything legal, and a name built on a
    /// Linux machine that Windows then refuses would be a file nobody could save. Control
    /// characters go as well, and so do the invisible ones that reorder text — a world name is
    /// somebody else's typing, and a file whose name reads backwards in a folder is the kind of
    /// trick that is worth not allowing rather than worth explaining.</para>
    /// <para>Each one becomes an underscore rather than simply vanishing, so words do not run
    /// together; a run of them collapses to one, and a name is never left starting or ending with
    /// an underscore, a space or a dot. If nothing survives, the answer is <c>Clip</c> — an empty
    /// name would make a file called <c>.mp4</c>, which Windows hides and nobody finds.</para>
    /// </remarks>
    public static string AsFileName(string? text)
    {
        var built = new System.Text.StringBuilder(text?.Length ?? 0);

        foreach (var letter in text ?? string.Empty)
        {
            var allowed = !NeverInAFileName.Contains(letter)
                && !char.IsControl(letter)
                && char.GetUnicodeCategory(letter) is not System.Globalization.UnicodeCategory.Format;

            var next = allowed ? letter : '_';

            // One underscore, however many characters had to go.
            if (next == '_' && built.Length > 0 && built[^1] == '_')
                continue;

            built.Append(next);
        }

        var name = built.ToString().Trim(' ', '.', '_');
        return name.Length > 0 ? name : "Clip";
    }

    /// <summary>The longest a clip's name may be, before <c>.mp4</c> and before any <c>(2)</c>.</summary>
    public const int MostNameCharacters = 130;

    /// <summary>How much of a world's name a clip's name carries.</summary>
    private const int MostWorldCharacters = 60;

    /// <summary>How much of an instance number a clip's name carries.</summary>
    private const int MostInstanceCharacters = 40;

    /// <summary>
    /// What Windows will not take in a file name, plus the two that end a path. Written out so the
    /// answer does not change with the machine the name is built on.
    /// </summary>
    private const string NeverInAFileName = "<>:\"/\\|?*";

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    private static string Shorten(string? text, int most)
    {
        if (Blank(text))
            return string.Empty;

        var trimmed = text!.Trim();
        return trimmed.Length <= most ? trimmed : trimmed[..most].TrimEnd();
    }

    /// <summary>
    /// <paramref name="name"/>, or the first <c>name (2)</c>, <c>name (3)</c> … that is not already
    /// a clip in <paramref name="folder"/>.
    /// </summary>
    /// <remarks>
    /// Two saves in the same second, in the same instance, would otherwise be one file — and the
    /// one that would be lost is the first, which is the one the moderator pressed Save for.
    /// </remarks>
    private string Unused(string? folder, string name)
    {
        if (Blank(folder))
            return name;

        if (!Taken(name))
            return name;

        for (var another = 2; another <= 999; another++)
        {
            var candidate = $"{name} ({another})";
            if (!Taken(candidate))
                return candidate;
        }

        // A thousand clips of one world, one instance and one second. The moment's own
        // milliseconds part them; the clock is Modbot's, never the machine's.
        return $"{name} ({_clock.UtcNow.ToUnixTimeMilliseconds()})";

        bool Taken(string candidate) => File.Exists(Path.Combine(folder!, candidate + ClipExtension));
    }
}
