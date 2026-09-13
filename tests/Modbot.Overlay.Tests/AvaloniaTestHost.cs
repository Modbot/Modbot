using Avalonia;

namespace Modbot.Overlay.Tests;

/// <summary>
/// Brings Avalonia up once for the whole test assembly, offscreen and with no window.
/// </summary>
/// <remarks>
/// <para><c>UseHeadlessDrawing = false</c> is the point: the headless platform's own drawing is a
/// stub that records nothing, and these tests exist to check that real pixels come out of Skia and
/// arrive in a Direct3D texture. With the stub they would pass while rendering nothing.</para>
/// <para>Avalonia's platform can only be set up once per process, hence the <see cref="Lazy{T}"/>
/// rather than a fixture per class.</para>
/// </remarks>
internal static class AvaloniaTestHost
{
    private sealed class TestApp : Application;

    private static readonly Lazy<bool> Started = new(() =>
    {
        OverlayHost.ConfigureOffscreen<TestApp>().SetupWithoutStarting();

        return true;
    });

    public static void Ensure() => _ = Started.Value;
}
