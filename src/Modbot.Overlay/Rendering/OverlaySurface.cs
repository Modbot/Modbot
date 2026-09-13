using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Modbot.Overlay.Rendering;

/// <summary>
/// The texture the headset compositor draws, and the one way pixels get into it.
/// </summary>
/// <remarks>
/// An interface so the compositor can be tested without a GPU, and so the exact shape OpenVR
/// requires lives in one place with the reasons attached.
/// </remarks>
public interface IOverlaySurface : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>
    /// The native <c>ID3D11Texture2D*</c> that goes into <c>Texture_t.handle</c>. Zero before the
    /// surface is created.
    /// </summary>
    nint TextureHandle { get; }

    /// <summary>Uploads one frame of premultiplied BGRA, tightly packed, top row first.</summary>
    void Upload(ReadOnlySpan<byte> bgra);
}

/// <summary>
/// A shared Direct3D 11 texture of exactly the shape SteamVR's compositor will accept.
/// </summary>
/// <remarks>
/// <para><strong>Why each part of the description is what it is.</strong> The compositor runs in
/// its own process, <c>vrcompositor.exe</c>, and opens the submitted texture by its DXGI shared
/// handle; without <c>ResourceOptionFlags.Shared</c> there is nothing for it to open and
/// <c>SetOverlayTexture</c> cannot work at all. <c>B8G8R8A8_UNorm</c> is chosen because it is what
/// Avalonia's Skia backend already produces, so no channel swizzle stands between the renderer and
/// the headset. Shader-resource binding is what the compositor samples from.</para>
/// <para><strong>This program reads nothing and sends nothing.</strong> It moves pixels this
/// process drew into a texture this process owns. It touches no file, opens no socket, and never
/// reads the screen, the window list or any other application's output — the overlay draws
/// Modbot's own cached data and nothing that was captured from the machine.</para>
/// <para><strong>The upload is a CPU copy, and measurement says that is fine.</strong> A full
/// 1024×1024 frame — build, lay out, rasterise, read back and upload — measured about 1.3 ms on a
/// development machine, and an overlay texture is only resubmitted when its content changes rather
/// than every headset frame: the compositor re-projects whatever it was last given at the headset's
/// own rate without the application being involved. The 90 Hz round-trip cost that ruled out WPF's
/// <c>RenderTargetBitmap</c> in M3 6.0.1 does not arise, because nothing here runs at 90 Hz.</para>
/// </remarks>
public sealed class D3D11OverlaySurface : IOverlaySurface
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _texture;

    private D3D11OverlaySurface(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11Texture2D texture,
        int width,
        int height)
    {
        _device = device;
        _context = context;
        _texture = texture;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public nint TextureHandle => _texture.NativePointer;

    /// <summary>
    /// The DXGI handle the compositor opens the texture through. Exposed so that the handoff can
    /// be checked from a second, independent device — which is what the compositor does, and the
    /// only part of the contract that can be verified without SteamVR present.
    /// </summary>
    public nint SharedHandle
    {
        get
        {
            using var resource = _texture.QueryInterface<IDXGIResource>();
            return resource.SharedHandle;
        }
    }

    public static D3D11OverlaySurface Create(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // BgraSupport is required for the BGRA format Avalonia hands over; feature level 11_0 is
        // the floor SteamVR itself requires, so anything that can run VRChat can run this.
        D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var device,
            out _,
            out var context).CheckError();

        var texture = device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        });

        return new D3D11OverlaySurface(device, context!, texture, width, height);
    }

    public void Upload(ReadOnlySpan<byte> bgra)
    {
        var expected = Width * Height * 4;
        if (bgra.Length != expected)
        {
            throw new ArgumentException(
                $"Expected {expected} bytes of BGRA for {Width}×{Height}, got {bgra.Length}.",
                nameof(bgra));
        }

        unsafe
        {
            fixed (byte* source = bgra)
                _context.UpdateSubresource(_texture, 0u, null, (nint)source, (uint)(Width * 4), 0u);
        }

        // Submitted rather than queued: the compositor may sample this the moment the overlay is
        // told about it, and a half-written frame in a headset is worse than a late one.
        _context.Flush();
    }

    public void Dispose()
    {
        _texture.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
