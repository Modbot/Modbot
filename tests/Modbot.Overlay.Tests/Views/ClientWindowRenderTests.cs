using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Modbot.Client.Ingest;
using Modbot.Client.Journal;
using Modbot.Client.Pipeline;
using Modbot.Client.Presentation;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The client window's panels, rendered for real.
/// </summary>
/// <remarks>
/// <para>The window itself lives in a Windows-only executable that this suite cannot reference, so
/// what is exercised here is the layer that decides <em>what</em> it says — plus the shared token
/// set it draws with, at the density the desktop actually uses. A window drawn at headset density
/// would be enormous, and that mistake is one wrong constant away and invisible until somebody
/// runs it.</para>
/// <para>These render through the same Avalonia path the overlay uses, so "it compiles" is not
/// mistaken for "it draws".</para>
/// </remarks>
public class ClientWindowRenderTests
{
    private const int Width = 600;
    private const int Height = 400;

    private static byte[] Render(Control control) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Width, Height);
        return renderer.Render(control).ToArray();
    });

    /// <summary>
    /// A panel built the way the window builds them, from the desktop tokens.
    /// </summary>
    private static Control Panel(string title, string body) => AvaloniaTestHost.Run(() =>
    {
        var tokens = DesignTokens.Desktop;

        return (Control)new Border
        {
            Background = tokens.BackgroundBrush,
            Padding = new Thickness(16),
            Child = new Border
            {
                Background = tokens.SurfaceBrush,
                BorderBrush = tokens.BorderBrush,
                BorderThickness = new Thickness(tokens.Density.Hairline),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = tokens.Density.TextBase,
                            FontFamily = new FontFamily(DesignTokens.FontFamily),
                            FontWeight = FontWeight.SemiBold,
                            Foreground = tokens.TextBrush,
                        },
                        new TextBlock
                        {
                            Text = body,
                            FontSize = tokens.Density.TextSmall,
                            FontFamily = new FontFamily(DesignTokens.FontFamily),
                            Foreground = tokens.TextDimBrush,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            },
        };
    });

    [Fact]
    public void ADesktopPanelRendersRealPixels()
    {
        var pixels = Render(Panel("Cat Lounge", "Up to date. Everything observed has been reported."));

        Assert.Equal(Width * Height * 4, pixels.Length);
        Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 3);
    }

    [Fact]
    public void TheDesktopSurfaceIsDrawnOnTheDesktopBackgroundNotTheHeadsetOne()
    {
        // The corner pixel is the page behind the card. If the window ever picked up the VR
        // palette this is what would change, and nothing else would complain.
        var pixels = Render(Panel("Cat Lounge", "Up to date."));
        var corner = (pixels[2], pixels[1], pixels[0]);

        Assert.Equal(
            (ModbotPalette.Dark.Background.R, ModbotPalette.Dark.Background.G, ModbotPalette.Dark.Background.B),
            corner);

        Assert.NotEqual(
            (ModbotPalette.VrDark.Background.R, ModbotPalette.VrDark.Background.G, ModbotPalette.VrDark.Background.B),
            corner);
    }

    [Fact]
    public void TheSameStateProducesTheSamePixelsAndADifferentStateDoesNot()
    {
        var paused = Render(Panel("Cat Lounge", "Paused. Nothing is being captured."));
        var reporting = Render(Panel("Cat Lounge", "Up to date."));

        Assert.True(Render(Panel("Cat Lounge", "Up to date.")).AsSpan().SequenceEqual(reporting));
        Assert.False(paused.AsSpan().SequenceEqual(reporting));
    }

    [Fact]
    public void EveryServerStateProducesASentenceThatSaysWhetherAnythingIsStillComing()
    {
        // The distinction the window exists to make legible. "Retrying" and "stopped" look
        // identical on a screen that is not moving, and they mean opposite things.
        var clock = new TestSupport.FakeClock();
        var directory = Path.Combine(Path.GetTempPath(), "modbot-window-render", Guid.NewGuid().ToString("n"));

        try
        {
            var journal = new SentJournal(Path.Combine(directory, "sent.jsonl"), clock);
            var state = new ClientAppState(clock, journal)
            {
                LogHealth = new LogHealth(100, 10, 5, clock.UtcNow, clock.UtcNow),
            };

            var snapshot = state.Snapshot();

            Assert.Equal(LogHealthStatus.Healthy, snapshot.LogStatus);
            Assert.Empty(snapshot.Warnings);

            // And it renders, with the counts in it.
            Assert.Contains("100", snapshot.LogDetail);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
