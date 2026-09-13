using Modbot.Overlay.Views;

namespace Modbot.Overlay.Driving;

/// <summary>
/// Whatever the drive loop pushes a screen into.
/// </summary>
/// <remarks>
/// <para>An interface for one reason that matters: the loop's most important property is that it
/// <em>does not draw</em> when nothing has changed, and proving that needs a presenter that counts
/// rasterisations. Most machines running this client have no headset anyway, so a substitute
/// implementation is the ordinary case rather than a test-only convenience.</para>
/// <para><strong>Update returns whether it actually drew.</strong> That is the signal the loop and
/// its tests are about: a tick that returns false did no layout, no rasterisation and no texture
/// upload, which on a machine also running VRChat is the whole point.</para>
/// </remarks>
public interface IOverlayPresenter
{
    /// <summary>
    /// Shows a screen, drawing only if it would look different from the last one.
    /// </summary>
    /// <returns><c>true</c> when a frame was rendered and submitted.</returns>
    bool Update(OverlayScreen screen);

    void Show();

    void Hide();
}
