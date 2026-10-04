// vr-shot: one picture of what the headset is showing, overlays included, written as a PNG.
//
//   dotnet run --project tools/vr-shot -- <output.png> [--eye left|right]
//
// It joins a SteamVR that is already running as a background application (never starts it, never
// takes the headset, never becomes the scene app), asks the compositor for its mirror texture of
// one eye (the composited image before lens distortion: game plus every overlay), copies it once,
// gives everything back and exits. It changes no SteamVR or Modbot setting. See README.md.

using Modbot.VrShot;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: vr-shot <output.png> [--eye left|right]");
    return args.Length == 0 ? 64 : 0;
}

var output = Path.GetFullPath(args[0]);
var eye = 0;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--eye" && i + 1 < args.Length)
    {
        eye = args[++i] switch
        {
            "left" => 0,
            "right" => 1,
            _ => -1,
        };
    }
    else
    {
        eye = -1;
    }

    if (eye < 0)
    {
        Console.Error.WriteLine("usage: vr-shot <output.png> [--eye left|right]");
        return 64;
    }
}

try
{
    if (!OpenVr.IsRuntimeInstalled())
    {
        Console.WriteLine("SteamVR is not installed. Nothing taken.");
        return 2;
    }
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine("openvr_api.dll is missing beside the program.");
    return 1;
}

OpenVr.InitInternal(out var initError, OpenVr.ApplicationTypeBackground);
if (initError != 0)
{
    if (initError is OpenVr.InitNoServerForBackgroundApp or OpenVr.InitHmdNotFound)
    {
        Console.WriteLine($"SteamVR is not running ({OpenVr.InitErrorName(initError)}). Nothing taken.");
        return 2;
    }

    Console.Error.WriteLine($"SteamVR refused: {OpenVr.InitErrorName(initError)}.");
    return 1;
}

try
{
    var compositor = OpenVr.GetGenericInterface(OpenVr.CompositorInterfaceVersion, out var interfaceError);
    if (compositor == 0 || interfaceError != 0)
    {
        Console.Error.WriteLine($"SteamVR does not offer {OpenVr.CompositorInterfaceVersion}: {OpenVr.InitErrorName(interfaceError)}.");
        return 1;
    }

    // The default adapter. On a PC with more than one GPU it must be the one the headset is on,
    // or the shared texture cannot be opened (SteamVR then answers an error, below).
    D3D11.D3D11CreateDevice(
        null,
        DriverType.Hardware,
        DeviceCreationFlags.BgraSupport,
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
        out ID3D11Device? device,
        out ID3D11DeviceContext? context).CheckError();

    using (device)
    using (context)
    {
        var error = OpenVr.GetMirrorTextureD3D11(compositor, eye, device!.NativePointer, out var view);
        if (error != 0 || view == 0)
        {
            Console.Error.WriteLine($"SteamVR did not hand over the mirror image (EVRCompositorError {error}).");
            return 1;
        }

        try
        {
            // The view is SteamVR's to release, so it is wrapped without taking ownership: only the
            // resource it points at (which GetResource adds a reference to) is released here.
            var srv = new ID3D11ShaderResourceView(view);
            using var resource = srv.Resource;
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var description = texture.Description;
            var width = (int)description.Width;
            var height = (int)description.Height;

            using var staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = description.Width,
                Height = description.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = description.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            if (description.SampleDescription.Count > 1)
            {
                using var resolved = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = description.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.None,
                });
                context!.ResolveSubresource(resolved, 0, texture, 0, description.Format);
                context.CopyResource(staging, resolved);
            }
            else
            {
                context!.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0);
            }

            var rgb = new byte[width * height * 3];
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                if (!Pixels.ToRgb(description.Format, mapped.DataPointer, (int)mapped.RowPitch, width, height, rgb))
                {
                    Console.Error.WriteLine($"The mirror image is in {description.Format}, which this tool does not read yet.");
                    return 1;
                }
            }
            finally
            {
                context.Unmap(staging, 0);
            }

            var folder = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            Png.WriteRgb(output, width, height, rgb);
            Console.WriteLine($"{output} {width}x{height} {(eye == 0 ? "left" : "right")} eye ({description.Format})");
            return 0;
        }
        finally
        {
            OpenVr.ReleaseMirrorTextureD3D11(compositor, view);
        }
    }
}
finally
{
    OpenVr.ShutdownInternal();
}
