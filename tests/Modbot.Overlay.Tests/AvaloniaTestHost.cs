using Avalonia;
using Avalonia.Threading;

namespace Modbot.Overlay.Tests;

/// <summary>
/// Brings Avalonia up once for the whole test assembly, offscreen, on a thread of its own.
/// </summary>
/// <remarks>
/// <para><c>UseHeadlessDrawing = false</c> is the point: the headless platform's own drawing is a
/// stub that records nothing, and these tests exist to check that real pixels come out of Skia and
/// arrive in a Direct3D texture. With the stub they would pass while rendering nothing.</para>
/// <para><strong>A dedicated thread with a running dispatcher loop, not the test's own
/// thread.</strong> Avalonia binds its UI thread to whoever initialises it and refuses to
/// construct a control anywhere else, while xUnit hands each test whatever pool thread is free.
/// Initialising on the first test's thread appears to work and then fails whenever the scheduler
/// happens to place the next test elsewhere — which is the worst shape of flake, because it looks
/// like the renderer being unreliable rather than the harness being wrong.</para>
/// </remarks>
internal static class AvaloniaTestHost
{
    private sealed class TestApp : Application;

    private static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Runs <paramref name="work"/> on the Avalonia thread and returns what it produced.</summary>
    public static T Run<T>(Func<T> work) => Ui.Value.Invoke(work);

    public static void Run(Action work) => Ui.Value.Invoke(work);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            OverlayHost.ConfigureOffscreen<TestApp>().SetupWithoutStarting();
            ready.SetResult(Dispatcher.UIThread);

            // A real loop, so Invoke from a test thread is serviced rather than deadlocking. It
            // is never stopped: the process ends when the test run does.
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        {
            IsBackground = true,
            Name = "avalonia-test-ui",
        };

        // Single-threaded apartment on Windows, where some of what a UI stack touches expects it.
        // Not available elsewhere, and not needed there.
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);

        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }
}
