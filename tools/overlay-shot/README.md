# overlay-shot

A scratch tool, not part of Modbot: draws the overlay's panel and notification in every look (the
headset in its Modbot look and in VRChat's look, and the desktop window's VRChat look) from the same
made-up data, so they can be compared side by side with no VRChat, SteamVR, server or sign-in. Nothing is read from the network.

```
dotnet run --project tools/overlay-shot -c Release
dotnet run --project tools/overlay-shot -c Release -- --out some/folder
dotnet run --project tools/overlay-shot -c Release -- --palette-user <VRChat user id>
```

It writes into `tools/overlay-shot/out/` (gitignored):

| File | What |
|---|---|
| `instance-headset.png`, `instance-headset-vrchat.png`, `instance-desktop.png` | The Instance list: seven people of mixed rank, 18+ marks, one flagged, the moderator themself, one who just left |
| `auditlog-headset.png`, `auditlog-headset-vrchat.png`, `auditlog-desktop.png` | The Audit Log: Joined, Left and here-before rows |
| `notification-headset.png`, `notification-desktop.png` | A flagged join; the desktop one drawn as `DesktopNotifyWindow` does (`DesignTokens.Desktop`) |
| `compare.png` | Each set on a row (headset in the Modbot look, headset in VRChat's look, desktop), labelled with its size |

All names are made up. Pictures sit on a flat dark grey, standing in for the world or the game behind
a panel, so a see-through background shows as grey rather than as nothing.

## Sizes

- Headset main panel: 1024x1024, the overlay's texture. Notification: 300x300.
- Desktop panel: the size `VRChatHudLayout` gives the overlay for a 1920x1080 VRChat window
  (407x563 today), drawn from the window's 520x720 layout at that scale. Notification: 340 wide, as
  tall as its cards.

## Palette

By default the "Thy Kingdom" palette (highlights `#C53B48`, icons `#F66229`, buttons `#934226`,
backgrounds `#67171E`, text `#FFE07B`, subtext `#D86049`), turned into the desktop look's colours by
`OverlayColours.From`, as the window does. `--palette-user` reads the palette VRChat has selected for
that user id from this PC's registry the same way the window does (Windows only; stops if none is found).

## Limits

- The desktop panel is drawn from the real screen view inside a copy of the window's frame and title
  strip (group name only, no Close or lock buttons). Its list is cut at the window's height, with no
  scrolling, so a long list shows only its top.
- Not the live window: no hover, no cursor, no transparency of the window itself. The headset bar is
  drawn as it is when no controller points at the panel.
- Times on the Audit Log rows are shown in this PC's time zone.
