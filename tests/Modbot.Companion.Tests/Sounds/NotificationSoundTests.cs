using System.Buffers.Binary;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// Playing the bleep: through the device the moderator chose for the voice, at its own volume, on
/// its own switch, and never taking anything else down with it when it fails.
/// </summary>
public class NotificationSoundTests
{
    private readonly FakeClock _clock = new();
    private readonly FakePlayer _player = new();
    private readonly FakeDevices _devices = new();
    private readonly List<string> _log = [];

    private NotificationSettings _settings = NotificationSettings.Default;
    private string? _voiceDevice;

    private NotificationSound Sound() => new(
        _player, _devices, () => _settings, () => _voiceDevice, new BleepRule(_clock), _log.Add);

    [Fact]
    public async Task PlaysTheBleepThroughTheSystemDefaultWhenNothingWasChosen()
    {
        Assert.True(await Sound().PlayAsync(NotificationKind.FlaggedJoin, "Rin", TestContext.Current.CancellationToken));

        Assert.Single(_player.Played);
        Assert.Null(_player.Played[0].Device);
        Assert.Equal(Bleep.Make().Samples.Length, _player.Played[0].Clip.Samples.Length);
    }

    [Fact]
    public async Task PlaysThroughTheDeviceTheVoiceIsSetTo()
    {
        _voiceDevice = "{headset}";

        Assert.True(await Sound().PlayAsync(NotificationKind.FlaggedJoin, "Rin", TestContext.Current.CancellationToken));

        Assert.Equal("{headset}", _player.Played[0].Device?.Id);
    }

    [Fact]
    public async Task ADeviceThatIsNotPluggedInFallsBackToTheDefault()
    {
        _voiceDevice = "{dock left at home}";

        Assert.True(await Sound().PlayAsync(NotificationKind.Problem, "modbot.example", TestContext.Current.CancellationToken));

        Assert.Null(_player.Played[0].Device);
    }

    [Fact]
    public async Task SwitchedOffPlaysNothing()
    {
        _settings = _settings with { Bleep = false };

        Assert.False(await Sound().PlayAsync(NotificationKind.FlaggedJoin, "Rin", TestContext.Current.CancellationToken));
        Assert.Empty(_player.Played);
    }

    [Fact]
    public async Task TheTestButtonPlaysEvenWhenItIsSwitchedOff()
    {
        _settings = _settings with { Bleep = false };

        Assert.True(await Sound().PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(_player.Played);
    }

    [Fact]
    public async Task NoVolumePlaysNothingRatherThanOpeningADeviceForSilence()
    {
        _settings = _settings with { Volume = 0 };

        Assert.False(await Sound().PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(_player.Played);
    }

    [Fact]
    public async Task TheVolumeIsAppliedToTheSound()
    {
        _settings = _settings with { Volume = 50 };

        Assert.True(await Sound().PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(Bleep.Height * 0.5f, _player.Played[0].Clip.Samples.Max(Math.Abs), 2);
    }

    [Fact]
    public async Task TheRuleDecidesWhetherThereIsASoundAtAll()
    {
        var sound = Sound();

        Assert.True(await sound.PlayAsync(NotificationKind.FlaggedJoin, "Rin", TestContext.Current.CancellationToken));
        Assert.False(await sound.PlayAsync(NotificationKind.Joined, "Kai", TestContext.Current.CancellationToken));

        Assert.Single(_player.Played);
    }

    [Theory]
    [InlineData(NotificationKind.Joined, Tune.Chime)]
    [InlineData(NotificationKind.FlaggedJoin, Tune.Alert)]
    [InlineData(NotificationKind.Problem, Tune.Urgent)]
    public async Task EachKindIsPlayedWithItsOwnSound(NotificationKind kind, Tune tune)
    {
        Assert.True(await Sound().PlayAsync(kind, "Rin", TestContext.Current.CancellationToken));

        Assert.Equal(Bleep.Make(tune).Samples.Length, _player.Played[0].Clip.Samples.Length);
    }

    [Fact]
    public async Task MoreThanOneFlaggedArrivalAtOncePlaysTheAlertTwice()
    {
        var sound = Sound();

        Assert.True(await sound.PlayAsync(NotificationKind.FlaggedJoin, "Rin", TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(await sound.PlayAsync(NotificationKind.FlaggedJoin, "Kai", TestContext.Current.CancellationToken));

        Assert.Equal(2, _player.Played.Count);
        Assert.Equal(Bleep.Make(Tune.Alert).Samples.Length, _player.Played[0].Clip.Samples.Length);
        Assert.Equal(Bleep.Make(Tune.AlertTwice).Samples.Length, _player.Played[1].Clip.Samples.Length);
    }

    [Fact]
    public async Task ASoundSomebodyPressedForIsTheOneTheyPressed()
    {
        var sound = Sound();

        Assert.True(await sound.PlayAsync(Tune.AllClear, TestContext.Current.CancellationToken));

        Assert.Equal(Bleep.Make(Tune.AllClear).Samples.Length, _player.Played[0].Clip.Samples.Length);
    }

    [Fact]
    public async Task ASoundSomebodyPressedForPlaysEvenWhenTheSoundIsSwitchedOff()
    {
        _settings = _settings with { Bleep = false };

        Assert.True(await Sound().PlayAsync(Tune.Chime, TestContext.Current.CancellationToken));
        Assert.Single(_player.Played);
    }

    [Fact]
    public async Task NothingInterruptsASoundThatIsAlreadyPlaying()
    {
        var sound = Sound();
        _player.Hold = new TaskCompletionSource();

        var first = sound.PlayAsync(NotificationKind.FlaggedJoin, "Rin", TestContext.Current.CancellationToken);
        await _player.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Worse than what is playing, and it still waits: a sound that cut into another would be
        // two sounds on top of each other rather than one thing anybody could recognise.
        Assert.False(await sound.PlayAsync(NotificationKind.Problem, "modbot.example", TestContext.Current.CancellationToken));

        _player.Hold.SetResult();
        Assert.True(await first);
        Assert.Single(_player.Played);

        // And the rule never recorded the one nobody heard, so it is still there to be heard when
        // the quiet gap has run.
        _clock.Advance(Bleep.LengthOf(Tune.AlertTwice) + BleepRule.QuietGap + TimeSpan.FromSeconds(1));
        Assert.True(await sound.PlayAsync(NotificationKind.Problem, "modbot.example", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheModeratorsOwnFileIsPlayedForEveryOneOfTheFive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modbot-own-sound-{Guid.NewGuid():n}.wav");
        await File.WriteAllBytesAsync(path, Wav(), TestContext.Current.CancellationToken);

        try
        {
            _settings = _settings with { Sound = path };

            var sound = Sound();

            // One path in settings, so one sound for everything. The cost of that is real and is
            // the point of this test: somebody who brings their own file loses the difference
            // between the five, which is what the client did before there were five.
            foreach (var tune in Tunes.All)
                Assert.True(await sound.PlayAsync(tune, TestContext.Current.CancellationToken));

            Assert.Null(sound.LastProblem);
            Assert.Equal(Tunes.All.Count, _player.Played.Count);
            Assert.All(_player.Played, played => Assert.Equal(4_800, played.Clip.Samples.Length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A plain 16-bit one-channel WAV of a tenth of a second, which is all the reader needs.</summary>
    private static byte[] Wav(int sampleRate = 48_000, int frames = 4_800)
    {
        var data = new byte[frames * 2];
        for (var frame = 0; frame < frames; frame++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(frame * 2), (short)(frame % 2 == 0 ? 8_000 : -8_000));

        var file = new byte[44 + data.Length];
        var at = file.AsSpan();

        "RIFF"u8.CopyTo(at);
        BinaryPrimitives.WriteUInt32LittleEndian(at[4..], (uint)(36 + data.Length));
        "WAVE"u8.CopyTo(at[8..]);
        "fmt "u8.CopyTo(at[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(at[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(at[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(at[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(at[28..], (uint)(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(at[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(at[34..], 16);
        "data"u8.CopyTo(at[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[40..], (uint)data.Length);
        data.CopyTo(at[44..]);

        return file;
    }

    [Fact]
    public async Task APlayerThatFailsIsWrittenDownAndNothingIsThrown()
    {
        _player.Fail = true;

        Assert.False(await Sound().PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(_log);
        Assert.Contains("could not be played", _log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASoundFileThatIsNotThereFallsBackToModbotsOwnSoundAndSaysSo()
    {
        _settings = _settings with { Sound = Path.Combine(Path.GetTempPath(), "modbot-no-such-sound.wav") };

        var sound = Sound();

        Assert.True(await sound.PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));

        // It still made a sound -- the one the client makes itself.
        Assert.Single(_player.Played);
        Assert.Equal(Bleep.Make().Samples.Length, _player.Played[0].Clip.Samples.Length);

        // And it said why, once, for the settings screen and for the client's own log.
        Assert.NotNull(sound.LastProblem);
        Assert.Single(_log);
        Assert.Contains("Modbot's own sound", _log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASoundFileThatCannotBeReadFallsBackToo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modbot-not-a-sound-{Guid.NewGuid():n}.wav");
        await File.WriteAllTextAsync(path, "this is not a sound file at all", TestContext.Current.CancellationToken);

        try
        {
            _settings = _settings with { Sound = path };

            var sound = Sound();

            Assert.True(await sound.PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(Bleep.Make().Samples.Length, _player.Played[0].Clip.Samples.Length);
            Assert.NotNull(sound.LastProblem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NoSoundFileMeansNoProblemAndNoFileIsOpened()
    {
        var sound = Sound();

        Assert.True(await sound.PlayAsync(NotificationKind.Test, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(sound.LastProblem);
        Assert.Empty(_log);
    }

    private sealed record PlayedClip(VoiceClip Clip, OutputDevice? Device);

    private sealed class FakePlayer : IVoicePlayer
    {
        public List<PlayedClip> Played { get; } = [];

        public bool Fail { get; set; }

        /// <summary>Set to make a sound stay playing until the test lets it finish.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Completed once a sound has actually reached the player.</summary>
        public TaskCompletionSource Started { get; } = new();

        public async Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("no device");

            Played.Add(new PlayedClip(clip, device));
            Started.TrySetResult();

            if (Hold is { } held)
                await held.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FakeDevices : IOutputDevices
    {
        public IReadOnlyList<OutputDevice> List() => [new("{headset}", "Headset"), new("{speakers}", "Speakers")];

        public OutputDevice? Default() => new("{speakers}", "Speakers");
    }
}
