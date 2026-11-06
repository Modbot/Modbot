using Modbot.Companion.Dictation;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App.Tests;

/// <summary>The Dictate row on the show and hide button follows whether AI Chat is on and allowed.</summary>
public class DictateGatingTests
{
    [Fact]
    public void WithAIChatNotOfferedTheCardIsAsItWasAndNoControllerIsBuilt()
    {
        var built = 0;

        var screen = CompanionHost.WithDictateIfOffered(
            ButtonScreen.Shown,
            offered: false,
            () =>
            {
                built++;
                return Controller();
            });

        Assert.Equal(DictateState.Off, screen.Dictate);
        Assert.Equal(ButtonScreen.Shown, screen);
        Assert.Equal(0, built);
    }

    [Fact]
    public void WithAIChatOfferedTheRowShowsWhereTheControllerIs()
    {
        var screen = CompanionHost.WithDictateIfOffered(ButtonScreen.Shown, offered: true, Controller);

        Assert.Equal(DictateState.Idle, screen.Dictate);
    }

    private static DictationController Controller()
        => new(new SilentRecorder(), new BlankRecognizer(), () => true, _ => Task.FromResult<string?>(null), (_, _) => { });

    private sealed class SilentRecorder : IDictationRecorder
    {
        public event Action? LimitReached
        {
            add { }
            remove { }
        }

        public string? Start() => null;

        public float[] Stop() => [];
    }

    private sealed class BlankRecognizer : IDictationRecognizer
    {
        public string Recognize(float[] samples) => string.Empty;
    }
}
