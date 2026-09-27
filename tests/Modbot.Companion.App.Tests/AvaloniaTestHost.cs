using Avalonia;
using Avalonia.Threading;
using Modbot.Overlay;

namespace Modbot.Companion.App.Tests;

/// <summary>
/// Brings Avalonia up once for the whole test assembly, offscreen, on a thread of its own, with
/// the same theme and fonts the companion starts with.
/// </summary>
/// <remarks>
/// <para>A dedicated thread with a running dispatcher loop, for the reason the overlay's tests give:
/// Avalonia binds its UI thread to whoever starts it, and xUnit hands each test whatever pool
/// thread is free.</para>
/// <para>Not the companion's own application class. Starting that one starts the whole client —
/// the log reader, the pairings, the settings file — and these tests want the window and nothing
/// behind it.</para>
/// </remarks>
internal static class AvaloniaTestHost
{
    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            // The same theme as the companion: without one a templated control has no template,
            // and a slider with no template is not the slider the window really shows.
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }
    }

    private static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Runs <paramref name="work"/> on the Avalonia thread.</summary>
    public static void Run(Action work) => Ui.Value.Invoke(work);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            OverlayHost.ConfigureOffscreen<TestApp>()
                .ConfigureFonts(Brand.RegisterFonts)
                .SetupWithoutStarting();
            ready.SetResult(Dispatcher.UIThread);

            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        {
            IsBackground = true,
            Name = "avalonia-test-ui",
        };

        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);

        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }
}
