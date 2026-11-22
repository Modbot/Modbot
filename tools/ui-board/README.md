# UI board

A developer tool, not part of Modbot. One local web page that shows every Modbot client screen side
by side, drawn from made-up data, and redraws itself a few seconds after a source file is saved.

## Start it

From the repo root:

```
dotnet run --project tools/ui-board -c Release -- --watch
```

Then open <http://localhost:5190/>. `--port <number>` picks another port. Compile errors print in the
console where the tool runs; the pictures stay as they were until the next build works.

One draw without watching or serving:

```
dotnet run --project tools/ui-board -c Release
```

It writes a picture per tile, `manifest.json` and `board.html` into `tools/ui-board/out/` (gitignored;
`--out <folder>` writes somewhere else). Agents can open the board with the `ui-board` entry in
`.claude/launch.json`.

## What it shows

| Group | What | Where it is drawn |
|---|---|---|
| VR and desktop | The overlay (Instance list, Audit Log), the button panel (Hide, Show, Dictate), notifications, the chat pop-up, in the headset and in VRChat's desktop window, each in the Modbot look and VRChat's look, in one row | `Surfaces.Vr.cs`, `Surfaces.Desktop.cs` |
| Companion app | The main window's pages and every Settings card | `Surfaces.Companion.cs` |

The desktop tiles are the real windows (`DesktopOverlayWindow` and the others), shown offscreen and
photographed, so their own top strip and frame are there. The companion tiles are the real
`MainWindow`, taken to a page by pressing its sidebar row.

## Add a surface

1. Add a `Tile` to the list in `Names.cs` (an id, which is its file name, and its plain name), inside a
   `Row` of the group it belongs to. `Names.cs` is the only place a name is written down; a rename is
   one edit there.
2. Add what draws it in the `Surfaces.*.cs` file for its side: `draws["<id>"] = () => ...` returning a
   `RenderTargetBitmap`. The tool refuses to start drawing if a tile has no draw.
3. Data for the screens is made up in `Samples.cs` (the overlay) and `Surfaces.Companion.cs` (the
   companion's snapshot). Use fake names and ids.

A tile that throws shows its error on the board and the others still draw.

## Limits

- The data is made up. Nothing here reads a server, a log or VRChat.
- Moving Dictate states: the VR tiles are drawn part-way round (step 2 of 8); the desktop tiles show the window's own picture at its first step, so a moving state can differ between the two only by that step.
- The desktop Dictate tiles are the overlay window's top 64 pixels, where the button is.
- The Debug page and the companion's pop-ups, tray menu and command palette are not on the board.
- Pictures are one scale; the page shows them at most 480 pixels wide, a click opens the full size.
- Settings cards are cut out of one tall window, so a card taller than the window (3000 pixels) would be cut off.
- Text times in the companion tiles use this PC's time zone.
