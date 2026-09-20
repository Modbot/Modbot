using Modbot.Companion.Voice;

namespace Modbot.Companion.Sounds;

/// <summary>One note of a sound: how high it is, and how far into the sound it is struck.</summary>
/// <param name="Hertz">How high the note is.</param>
/// <param name="At">How far into the sound it is struck, from the beginning.</param>
public readonly record struct Note(double Hertz, TimeSpan At);

/// <summary>
/// The short sounds the client makes when it has something to tell the moderator: struck notes,
/// made from a formula rather than read from a file.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is read and nothing is sent.</strong> The samples are written into an
/// array by the arithmetic below, and played on this PC through the same output the voice uses.
/// There is no audio file in the client, so there is nothing whose licence or origin anybody has to
/// check, and nothing to go missing from an install. A moderator who wants a different sound points
/// the setting at a file of their own (<see cref="SoundFile"/>); the client still ships none.</para>
/// <para><strong>What changed on 2026-09-19, and why.</strong> This used to be two sine tones, 880
/// then 1245 hertz, each 6 milliseconds from silence to full height and 6 milliseconds back. That
/// is the recipe for a smoke alarm: high, bare, abrupt at both ends, and two pitches with no
/// musical relation to each other. A moderator hears this forty times in an evening and said so.
/// Four things are different now, and each is doing a job:</para>
/// <list type="bullet">
/// <item><description><strong>Lower.</strong> 440 hertz and 660, where it was 880 and 1245. The
/// old pair sat in the band the ear is most sensitive to and most quickly annoyed by.</description></item>
/// <item><description><strong>A musical interval.</strong> The second note is a perfect fifth above
/// the first — exactly three halves of its pitch — so the pair reads as two notes of one sound
/// rather than as two unrelated beeps.</description></item>
/// <item><description><strong>Struck, not switched on.</strong> Each note comes up over
/// <see cref="Attack"/> along a curve rather than a ramp, and then decays the way a struck thing
/// decays: fast at first, trailing off, with the second note beginning while the first is still
/// ringing.</description></item>
/// <item><description><strong>Overtones.</strong> A handful of quiet partials above each note, each
/// dying faster than the one below it, which is what separates the sound of something being hit
/// from the sound of a test tone.</description></item>
/// </list>
/// <para><strong>And what changed later the same day: there are five of them.</strong> A moderator
/// asked to be able to tell "somebody arrived" from "somebody flagged arrived" from "this needs you
/// now" without looking at the screen. The five are one family and not five noises: the same
/// instrument, the same curve up and the same decay, differing in how many notes there are, how
/// high they are, which way they go, and how loud the whole thing is made. See <see cref="Tune"/>.
/// The two-note alert is unchanged, so the sound a moderator already knows is still the sound a
/// flagged arrival makes.</para>
/// <para>They are still short — the longest is a little over a second, and only because it is the
/// two-note alert played twice — and still made of arithmetic that anybody can read.</para>
/// </remarks>
public static class Bleep
{
    /// <summary>The rate the samples are made at. The players resample if the device wants another.</summary>
    public const int SampleRate = 48_000;

    /// <summary>The two-note alert's first note: A above middle C.</summary>
    public const double FirstTone = 440;

    /// <summary>
    /// The two-note alert's second note: a perfect fifth above the first, which is three halves of
    /// its pitch. The simplest interval there is after the octave, and the reason the pair sounds
    /// like one thing.
    /// </summary>
    public const double SecondTone = FirstTone * 3 / 2;

    /// <summary>The octave above the alert's first note, and the middle note of the urgent one.</summary>
    public const double OctaveTone = FirstTone * 2;

    /// <summary>
    /// The top of the urgent one: a major third above the octave, so the three notes are a chord
    /// climbing rather than three unrelated pitches.
    /// </summary>
    public const double TopTone = FirstTone * 5 / 2;

    /// <summary>How long one note takes to fade to nothing.</summary>
    public static readonly TimeSpan NoteLength = TimeSpan.FromMilliseconds(340);

    /// <summary>
    /// When the two-note alert's second note is struck, measured from the first. Short enough that
    /// the first is still ringing under it, which is what makes them one sound rather than two.
    /// </summary>
    public static readonly TimeSpan SecondStartsAt = TimeSpan.FromMilliseconds(140);

    /// <summary>
    /// When the two-note alert starts again in <see cref="Tune.AlertTwice"/>, measured from the
    /// beginning.
    /// </summary>
    /// <remarks>
    /// The first pair is finished at 480 milliseconds, so this leaves 220 milliseconds of exact
    /// silence in the middle. That gap is deliberately longer than the 140 milliseconds between the
    /// two notes of a pair: what a moderator must hear is the same sound twice, not four notes run
    /// together, and the ear tells those apart by which gap is the big one.
    /// </remarks>
    public static readonly TimeSpan RepeatStartsAt = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// How long a note takes to reach its full height. Long enough that nothing starts with a
    /// click, short enough that the sound still reads as something struck rather than swelling.
    /// </summary>
    public static readonly TimeSpan Attack = TimeSpan.FromMilliseconds(18);

    /// <summary>
    /// How long a note takes to fall to about a third of its height, and then to a third of that,
    /// and so on. This is the number that makes it a decay with a tail rather than a fade.
    /// </summary>
    public static readonly TimeSpan Decay = TimeSpan.FromMilliseconds(90);

    /// <summary>
    /// The last stretch of a note, taken smoothly to exactly nothing. The decay above never quite
    /// reaches zero, and a buffer that ends on a number that is not zero ends with a click.
    /// </summary>
    public static readonly TimeSpan Release = TimeSpan.FromMilliseconds(45);

    /// <summary>
    /// How loud the alerts are made, before the moderator's own volume is applied. Short of the top
    /// so that a volume of 100 is still a clean sound rather than a clipped one.
    /// </summary>
    public const float Height = 0.7f;

    /// <summary>
    /// How loud the soft single chime is made: half the alerts, because it is the one a moderator
    /// hears most and the one that must never be the reason they switch the sound off.
    /// </summary>
    public const float QuietHeight = 0.35f;

    /// <summary>
    /// How loud the falling all-clear is made. Quiet, because news that something has stopped
    /// needing attention is not a demand for any.
    /// </summary>
    public const float FallingHeight = 0.40f;

    /// <summary>
    /// The overtones above each note, as a fraction of the note's own height, starting with the
    /// second partial.
    /// </summary>
    /// <remarks>
    /// Quiet on purpose. The first of them is under half the height of the note below it, which is
    /// the point at which a partial colours a note rather than becoming a note of its own — it is
    /// also what keeps the sound's pitch measurable by counting how often it crosses zero, which is
    /// how <c>BleepTests</c> checks it without a transform.
    /// </remarks>
    private static readonly float[] Overtones = [0.30f, 0.13f, 0.06f];

    /// <summary>
    /// The overtones the urgent sound gets instead: the same three, louder.
    /// </summary>
    /// <remarks>
    /// This is what "sharper" is made of, together with <see cref="SharpAttack"/>. Not a louder
    /// sound — the urgent one is made to exactly the same height as the ordinary alert — but a
    /// brighter and harder-struck one, which is what the ear reads as urgency. The first of them
    /// is still under half the height of the note below it, so the pitch of each note can still be
    /// counted off the zero crossings.
    /// </remarks>
    private static readonly float[] SharpOvertones = [0.42f, 0.22f, 0.12f];

    /// <summary>How fast the urgent sound's notes reach full height: struck hard rather than rung.</summary>
    public static readonly TimeSpan SharpAttack = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// How fast the quiet sounds reach full height: slower than the alert's, which is what makes
    /// them read as gentle rather than merely quiet.
    /// </summary>
    public static readonly TimeSpan SoftAttack = TimeSpan.FromMilliseconds(26);

    /// <summary>
    /// How much faster each partial above the first dies away. A struck thing loses its high
    /// overtones first, and a note whose partials all decayed together would sound like an organ.
    /// </summary>
    private const double OvertonesDieFaster = 0.55;

    /// <summary>How long the two-note alert lasts.</summary>
    public static TimeSpan Length => LengthOf(Tune.Alert);

    /// <summary>One sound, written out: what it is made of, and how loud and how hard it is struck.</summary>
    private sealed record Recipe(float Height, TimeSpan Attack, float[] Overtones, Note[] Notes);

    private static readonly Dictionary<Tune, Recipe> Recipes = new()
    {
        // One note, and the alert's upper note rather than its lower one: a single low note at a
        // low height turns to mush on the small speakers a lot of people have.
        [Tune.Chime] = new(QuietHeight, SoftAttack, Overtones, [new Note(SecondTone, TimeSpan.Zero)]),

        // Unchanged from the sound a moderator already knows.
        [Tune.Alert] = new(Height, Attack, Overtones,
        [
            new Note(FirstTone, TimeSpan.Zero),
            new Note(SecondTone, SecondStartsAt),
        ]),

        // The same pair, twice, with real silence in between.
        [Tune.AlertTwice] = new(Height, Attack, Overtones,
        [
            new Note(FirstTone, TimeSpan.Zero),
            new Note(SecondTone, SecondStartsAt),
            new Note(FirstTone, RepeatStartsAt),
            new Note(SecondTone, RepeatStartsAt + SecondStartsAt),
        ]),

        // Three notes climbing — the alert's upper note, the octave above its lower one, and a
        // major third above that — struck harder, closer together and brighter, but to exactly the
        // same height as the alert. Louder is not what makes something urgent; higher, faster and
        // more of it is.
        [Tune.Urgent] = new(Height, SharpAttack, SharpOvertones,
        [
            new Note(SecondTone, TimeSpan.Zero),
            new Note(OctaveTone, TimeSpan.FromMilliseconds(100)),
            new Note(TopTone, TimeSpan.FromMilliseconds(200)),
        ]),

        // The alert's two notes the other way up, quieter and softer struck. Falling is what makes
        // a sound read as an ending rather than as a question.
        [Tune.AllClear] = new(FallingHeight, SoftAttack, Overtones,
        [
            new Note(SecondTone, TimeSpan.Zero),
            new Note(FirstTone, TimeSpan.FromMilliseconds(160)),
        ]),
    };

    /// <summary>The notes one sound is made of, in the order they are struck.</summary>
    public static IReadOnlyList<Note> NotesOf(Tune tune) => Of(tune).Notes;

    /// <summary>How long one sound lasts, from the first note to the end of the last.</summary>
    public static TimeSpan LengthOf(Tune tune)
    {
        var last = TimeSpan.Zero;
        foreach (var note in Of(tune).Notes)
            last = note.At > last ? note.At : last;

        return last + NoteLength;
    }

    /// <summary>How loud one sound is made, before the moderator's own volume is applied.</summary>
    public static float HeightOf(Tune tune) => Of(tune).Height;

    /// <summary>How long one sound's notes take to reach full height.</summary>
    public static TimeSpan AttackOf(Tune tune) => Of(tune).Attack;

    /// <summary>
    /// One sound, ready to play. Cheap enough to call whenever, and held by the one object that
    /// plays it rather than remade per sound.
    /// </summary>
    public static VoiceClip Make(Tune tune = Tune.Alert, int sampleRate = SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);

        var recipe = Of(tune);
        var samples = new float[Samples(LengthOf(tune), sampleRate)];

        foreach (var note in recipe.Notes)
            Strike(samples, Samples(note.At, sampleRate), note.Hertz, sampleRate, recipe);

        // Made at whatever height the notes happened to add up to, then brought to the one height
        // the volume is applied to, so overlapping notes can never clip and the sound's loudness
        // does not depend on the numbers above.
        var loudest = 0f;
        foreach (var sample in samples)
            loudest = Math.Max(loudest, Math.Abs(sample));

        if (loudest > 0)
        {
            var scale = recipe.Height / loudest;
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= scale;
        }

        return new VoiceClip(samples, sampleRate);
    }

    /// <summary>The two-note alert at a rate of its own. Here so that <c>Make()</c> still means the alert.</summary>
    public static VoiceClip Make(int sampleRate) => Make(Tune.Alert, sampleRate);

    private static Recipe Of(Tune tune) => Recipes.TryGetValue(tune, out var recipe)
        ? recipe
        : Recipes[Tune.Alert];

    private static int Samples(TimeSpan length, int sampleRate)
        => (int)Math.Round(length.TotalSeconds * sampleRate);

    /// <summary>
    /// One note struck into the buffer, added to whatever is already there so a note that is still
    /// ringing is not cut off by the next one.
    /// </summary>
    private static void Strike(Span<float> into, int at, double hertz, int sampleRate, Recipe recipe)
    {
        var length = Math.Min(Samples(NoteLength, sampleRate), into.Length - at);
        if (length <= 0)
            return;

        var attack = Math.Max(1, Math.Min(Samples(recipe.Attack, sampleRate), length));
        var release = Math.Max(1, Math.Min(Samples(Release, sampleRate), length));
        var decay = Math.Max(1.0, Decay.TotalSeconds * sampleRate);

        for (var i = 0; i < length; i++)
        {
            // Up along the first quarter of a cosine rather than a straight line: a ramp still has
            // a corner at the top, and a corner is a click you can hear.
            var level = i < attack
                ? (float)(0.5 - (0.5 * Math.Cos(Math.PI * i / attack)))
                : 1f;

            // Down the way a struck thing goes down: quickly at first, then trailing.
            level *= (float)Math.Exp(-i / decay);

            // And taken to exactly nothing over the last stretch, because the line above never
            // quite gets there.
            var left = length - 1 - i;
            if (left < release)
                level *= (float)(0.5 - (0.5 * Math.Cos(Math.PI * left / release)));

            var value = (float)Math.Sin(2 * Math.PI * hertz * i / sampleRate);

            for (var overtone = 0; overtone < recipe.Overtones.Length; overtone++)
            {
                var partial = overtone + 2;
                var fades = Math.Exp(-i * OvertonesDieFaster * (partial - 1) / decay);
                value += (float)(recipe.Overtones[overtone] * fades * Math.Sin(2 * Math.PI * hertz * partial * i / sampleRate));
            }

            into[at + i] += level * value;
        }
    }
}
