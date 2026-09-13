using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Overlay.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Modbot.Overlay.Tests.Rendering;

/// <summary>
/// The load-bearing question of the whole overlay, as a regression test: does Avalonia render
/// offscreen into a Direct3D 11 texture that SteamVR's compositor can use?
/// </summary>
/// <remarks>
/// <para><strong>What this can and cannot prove.</strong> It proves every link in the chain up to
/// Valve's own code: Avalonia rasterises a real visual tree offscreen, the pixels reach an
/// <c>ID3D11Texture2D</c> created with exactly the description <c>SetOverlayTexture</c> requires,
/// and a <em>second, independent</em> Direct3D device opens that texture through its DXGI shared
/// handle and reads back the same bytes. Opening it on another device is what
/// <c>vrcompositor.exe</c> does across the process boundary, so it is the closest check available
/// without SteamVR installed.</para>
/// <para><strong>The one link it does not cover</strong> is the call into
/// <c>IVROverlay::SetOverlayTexture</c> itself, which needs a SteamVR runtime. That is a real
/// remaining risk and is recorded as one rather than implied away.</para>
/// <para>Skipped where there is no Direct3D 11 hardware, which is most CI runners. A skipped test
/// is honest; a test that silently falls back to a software rasteriser and then claims the shared
/// handle works would not be.</para>
/// </remarks>
public class AvaloniaToSharedTextureTests
{
    private const int Size = 256;

    /// <summary>Whether this machine has a Direct3D 11 device at all.</summary>
    private static bool HasHardware()
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out var device, out _, out var context);

            context?.Dispose();
            device?.Dispose();
            return hr.Success;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>A coloured quad and a label: vector fill and text shaping in one frame.</summary>
    private static Control Sample(string text = "flagged user joined") => new Border
    {
        Width = Size,
        Height = Size,
        Background = DesignTokens.Vr.BackgroundBrush,
        Child = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Children =
            {
                new Rectangle
                {
                    Width = 120,
                    Height = 48,
                    Fill = DesignTokens.Vr.DangerBrush,
                    RadiusX = DesignTokens.Vr.Density.Radius,
                    RadiusY = DesignTokens.Vr.Density.Radius,
                },
                new TextBlock
                {
                    Text = text,
                    FontSize = DesignTokens.Vr.Density.TextBase,
                    Foreground = DesignTokens.Vr.TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        },
    };

    [Fact]
    public void AvaloniaRendersOffscreenWithNoWindowAndProducesRealPixels()
    {
        var pixels = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            return renderer.Render(Sample()).ToArray();
        });

        Assert.Equal(Size * Size * 4, pixels.Length);

        // Every pixel opaque, and more than one colour present: a stub renderer that recorded
        // nothing would give a uniform buffer, and this is the check that catches it.
        Assert.All(Enumerable.Range(0, Size * Size), i => Assert.Equal(255, pixels[(i * 4) + 3]));
        Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 2);
    }

    [Fact]
    public void DifferentContentProducesDifferentPixels()
    {
        var (first, second) = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            return (renderer.Render(Sample("one")).ToArray(), renderer.Render(Sample("two")).ToArray());
        });

        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void ThePixelsReachASharedTextureThatASecondDeviceCanOpenAndReadBack()
    {
        Assert.SkipUnless(HasHardware(), "No Direct3D 11 hardware on this machine.");

        var rendered = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            return renderer.Render(Sample()).ToArray();
        });

        using var surface = D3D11OverlaySurface.Create(Size, Size);
        surface.Upload(rendered);

        Assert.NotEqual(0, surface.TextureHandle);
        Assert.NotEqual(0, surface.SharedHandle);

        // A second device, with no relationship to the first, opens the texture by its shared
        // handle -- which is exactly what vrcompositor.exe does from its own process.
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var compositor, out _, out var compositorContext).CheckError();

        using (compositor)
        using (compositorContext)
        {
            using var opened = compositor!.OpenSharedResource<ID3D11Texture2D>(surface.SharedHandle);
            using var staging = compositor.CreateTexture2D(opened.Description with
            {
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });

            compositorContext!.CopyResource(staging, opened);
            compositorContext.Flush();

            var mapped = compositorContext.Map(staging, 0, MapMode.Read);
            var readBack = new byte[rendered.Length];
            unsafe
            {
                var source = (byte*)mapped.DataPointer;
                for (var y = 0; y < Size; y++)
                    Marshal.Copy((nint)(source + (y * mapped.RowPitch)), readBack, y * Size * 4, Size * 4);
            }

            compositorContext.Unmap(staging, 0);

            Assert.True(
                readBack.AsSpan().SequenceEqual(rendered),
                "The pixels Avalonia drew did not survive the trip into the shared texture.");
        }
    }

    [Fact]
    public void TheTextureIsCreatedWithTheDescriptionSteamVrRequires()
    {
        Assert.SkipUnless(HasHardware(), "No Direct3D 11 hardware on this machine.");

        using var surface = D3D11OverlaySurface.Create(Size, Size);

        // Reached through the same shared handle the compositor would use, so this asserts what
        // the compositor would actually see rather than what was asked for.
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var other, out _, out var otherContext).CheckError();

        using (other)
        using (otherContext)
        {
            using var opened = other!.OpenSharedResource<ID3D11Texture2D>(surface.SharedHandle);
            var description = opened.Description;

            Assert.Equal(Vortice.DXGI.Format.B8G8R8A8_UNorm, description.Format);
            Assert.Equal((uint)Size, description.Width);
            Assert.Equal(1u, description.MipLevels);
            Assert.Equal(1u, description.SampleDescription.Count);
            Assert.True(description.BindFlags.HasFlag(BindFlags.ShaderResource));
        }
    }

    [Fact]
    public void AFrameOfTheWrongSizeIsRefusedRatherThanUploadedTorn()
    {
        Assert.SkipUnless(HasHardware(), "No Direct3D 11 hardware on this machine.");

        using var surface = D3D11OverlaySurface.Create(Size, Size);

        Assert.Throws<ArgumentException>(() => surface.Upload(new byte[16]));
    }
}
