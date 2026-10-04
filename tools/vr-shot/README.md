# vr-shot

A scratch tool, not part of Modbot: takes one picture of what the headset is showing, overlays
included, and writes it as a PNG. Made for checking the VR panels while playtesting.

```
dotnet run --project tools/vr-shot -c Release -- tools/vr-shot/out/shot.png
dotnet run --project tools/vr-shot -c Release -- tools/vr-shot/out/shot.png --eye right
```

It prints the file, the size and the eye, e.g. `...\shot.png 3024x3360 left eye (R8G8B8A8_Typeless)`.
`tools/vr-shot/out/` is gitignored; the pictures can show other players, so keep them out of git.

## What it needs

- Windows, SteamVR already running. If it is not, the tool says so and takes nothing (exit 2).
- A D3D11 GPU. On a PC with more than one GPU the default one must be the headset's.
- `openvr_api.dll` is the one `src/Modbot.Overlay/native/win-x64` vendors, copied beside the exe.

## How it works

It joins SteamVR as a **background application**: SteamVR answers that only when it is already
running, never starts for it, never gives it the headset and never makes it the scene app. It
asks the compositor for its mirror texture of one eye (`IVRCompositor::GetMirrorTextureD3D11`), the
image SteamVR's own VR View shows: the game and every overlay on top, before lens distortion, at
the eye's full render size. It copies that once, gives the texture back
(`ReleaseMirrorTextureD3D11`), shuts its OpenVR connection and exits. It changes no setting.

## Limits

- One eye at a time. Edges outside the lens are black (SteamVR's hidden-area mask).
- The size is the render size, so it follows SteamVR's resolution setting; at a high setting a
  picture is several megabytes.
- Reads 8-bit RGBA/BGRA and 16-bit float textures; any other format is named and refused.
- Not used: `IVRScreenshots` (`TakeStereoScreenshot`, `RequestScreenshot`). They capture the game's
  own eye images without overlays, and `SubmitScreenshot` puts the file into Steam's screenshot
  library for the running game. `CompositorDumpImages` writes debug files into SteamVR's own folder.
