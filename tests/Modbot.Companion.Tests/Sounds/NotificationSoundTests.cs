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
        Assert.False(await sound.PlayAsync(NotificationKind.FlaggedJoin, "Kai", TestContext.Current.CancellationToken));

        Assert.Single(_player.Played);
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

        public Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("no device");

            Played.Add(new PlayedClip(clip, device));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDevices : IOutputDevices
    {
        public IReadOnlyList<OutputDevice> List() => [new("{headset}", "Headset"), new("{speakers}", "Speakers")];

        public OutputDevice? Default() => new("{speakers}", "Speakers");
    }
}
