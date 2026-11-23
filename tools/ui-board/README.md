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

One tab per area, across the top:

| Tab | What | Where it is drawn |
|---|---|---|
| Overlay | The main panel: Instance list, Audit Log, Rank choices open, and the desktop window with Edit mode on (the lock and hand bar below the list) | `Surfaces.Vr.cs`, `Surfaces.Desktop.cs` |
| Button panel | The Hide and Show panel (headset only) and the Dictate button's states | `Surfaces.Vr.cs`, `Surfaces.Desktop.cs` |
| Notifications | The pop-ups | `Surfaces.Vr.cs`, `Surfaces.Desktop.cs` |
| Chat | The chat pop-up | `Surfaces.Vr.cs`, `Surfaces.Desktop.cs` |
| Companion app | The main window's pages and every Settings card | `Surfaces.Companion.cs` |

The desktop tiles are the real windows (`DesktopOverlayWindow` and the others), shown offscreen and
photographed, so their own top strip and frame are there. The companion tiles are the real
`MainWindow`, taken to a page by pressing its sidebar row.

## Using the page

- **Tabs** pick the area. One area shows at a time.
- **Look**: Modbot look, VRChat look (the start) or Both. In Both a surface's row holds both looks.
- **Size** scales the whole tab. Each tab starts at the share that fits it on one screen (`DefaultPercent`
  in `Names.cs`) and remembers where it was left.
- A surface's headset picture and desktop picture are side by side in one row, under the surface's name.
- Click a picture to see it at its own size; Escape or a click closes it.
- The tab, the look and each tab's size are kept in the browser and stay through the live refresh.

## Real size

Pictures are shown at their size relative to each other, not at their texture's pixels. The rule is
in one place, `Sizes.cs`, and the slider multiplies it:

- A headset panel is as wide as in the headset: its width in metres (read from the product's own
  constants: main panel 0.45 times 1.2, as it starts tall, button panel 0.28 (the card's 0.16 with its clear ground), pop-ups 0.41
  (the box's 0.35 with the bar's room), chat 0.55) times 1000 pixels per metre (`PixelsPerMetre`).
- A window is its own pixel size times 1 (`WindowScale`): its size on a 1920x1080 VRChat window.

So the Hide and Show panel comes out small next to the main panel, as it is in the headset.

## Add a surface

1. Add a `Tile` to the list in `Names.cs` (an id, which is its file name, and its plain name), inside a
   `Row` of the tab it belongs to. `Names.cs` is the only place a name is written down; a rename is
   one edit there.
2. Add what draws it in the `Surfaces.*.cs` file for its side: `draws["<id>"] = () => ...` returning a
   `RenderTargetBitmap`. The tool refuses to start drawing if a tile has no draw.
3. Data for the screens is made up in `Samples.cs` (the overlay) and `Surfaces.Companion.cs` (the
   companion's snapshot). Use fake names and ids.

A tile that throws shows its error on the board and the others still draw.

## Limits

- The data is made up. Nothing here reads a server, a log or VRChat.
- Moving Dictate states: the VR tiles are drawn part-way round (step 2 of 8); the desktop tiles show the window's own picture at its first step, so a moving state can differ between the two only by that step.
- The desktop Dictate tiles are the overlay window's title strip, cut where the strip ends.
- The Debug page and the companion's pop-ups, tray menu and command palette are not on the board.
- The headset's size on the page is a choice (1000 pixels per metre); only the sizes relative to each other are true.
- Settings cards are cut out of one tall window, so a card taller than the window (3000 pixels) would be cut off.
- Text times in the companion tiles use this PC's time zone.
