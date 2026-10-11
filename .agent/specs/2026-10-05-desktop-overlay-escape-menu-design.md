# Desktop overlay — Modbot's Escape Menu and a VRChat-only shortcut

**Date:** 2026-10-05
**Status:** draft — partly built. Built (2026-10-05, second commit): the window lookup moved out of
the recorder (§2.2), the one size rule that replaces "two known sizes" (§3.3.1), and the desktop
overlay's own placement beside VRChat's Esc menu (§3.6), and the bubble in VRChat's row, shown by
default with the overlay (§3.4). Not built: the VRChat-only shortcut poll and the settings fields
(including any switch for the bubble). Nobody has seen any of it on a live VRChat window.
**Touches (when built):** `src/Modbot.Companion/Presentation/DesktopOverlaySettings.cs`,
`src/Modbot.Companion.App/DesktopOverlayShortcut.cs`, `src/Modbot.Companion.App/ScreenRecording.cs`
(or a file split out of it), a new bubble window, `MainWindow.DesktopOverlay.cs`,
`tests/Modbot.Companion.Tests/Guards/CompanionSourceGuardTests.cs`.
Built so far also touches: `src/Modbot.Companion.App/VRChatWindow.cs` (new),
`src/Modbot.Companion/Presentation/VRChatHudLayout.cs` (new), `EscapeBubbleLayout.cs`,
`EscapeBubbleMetrics.cs` and `src/Modbot.Companion.App/DesktopOverlayWindow.cs`.

**Narrows:** `2026-09-18-desktop-overlay-design.md` §2.3 and §6, which refuse any shortcut with no
modifier. That rule stays the rule. This spec adds one named exception to it (§2) and says what
buys it.

---

## 1. What this is for

Two small things that belong together:

1. **A bare-key shortcut as an opt-in.** `F1` or `F9`, with no modifier, that works only while
   VRChat is the window in front.
2. **Modbot's Escape Menu.** A small bubble in VRChat's own HUD row, the slot after Y, drawn in the
   same style as VRChat's bubbles, that says which key opens the overlay.

Nothing here changes the default. The default shortcut stays `mod+alt+m` unless the moderator
picks something else (§5).

---

## 2. A bare key, only while VRChat is in front

The existing rule exists because `RegisterHotKey` is machine-wide: a bare `F1` would be taken from
every program, including the browser and Discord. The exception does not weaken that. It removes
the cause: **the key is registered only while VRChat's window is the one in front, and given back
the moment it is not.**

- It is **off unless chosen**. The Settings page gets one extra choice beside the shortcut field:
  "Only while VRChat is in front". A shortcut with no modifier is accepted only when that choice is
  on. With it off, §2.3 of the earlier spec applies unchanged and a bare key is refused.
- Which bare keys: function keys only (`F1`–`F12`). A bare letter or digit is still refused,
  because it would be typed into chat, and `escape` is still refused (§6 of the earlier spec).
- While registered, the key does nothing to VRChat that it did before: Windows delivers the press
  to Modbot instead, so **VRChat never sees it**. That is the cost, and it is why the key has to be
  one the moderator does not use in VRChat. The Settings page does not guess which those are.

### 2.1 How "in front" is found

**A poll, not a hook.** The client asks Windows which window is in front a few times a second and
compares it to VRChat's window. No `SetWinEventHook`, no keyboard hook, no `GetAsyncKeyState`. This
reads no key and sees no other program's content: the only question is "is that window VRChat's".

- Poll rate: 4 times a second. A press in the quarter second after VRChat comes to the front is
  not caught, and that is acceptable for a key that opens a panel.
- It runs only while the VRChat-only choice is on **and** the overlay is on. Otherwise nothing
  polls and nothing is registered.
- On "VRChat in front" the client registers the key on the shortcut thread (§2.2 of the earlier
  spec). On "something else in front" it releases it. The thread, the message loop and the failure
  rules (§2.4) are unchanged; this only decides when `Ask` and `Release` are called.
- If the key is already taken by another program when VRChat comes to the front, the state is
  `Taken` as before, and it is retried the next time VRChat comes to the front, not every poll.

### 2.2 One file may ask Windows about another window

`CompanionSourceGuardTests` allows exactly one file to call `FindWindowW`, `GetForegroundWindow`
and the rest (`AsksAboutAnotherWindow`), and today that file is `ScreenRecording.cs`. The poll
needs `FindWindowW` and `GetForegroundWindow` and nothing more.

Two ways to keep the rule true:

| | Move the lookup out | Allow a second file |
|---|---|---|
| Change | Split "find VRChat's window and say whether it is in front" out of `ScreenRecording.cs` into one small file; the recorder and the shortcut both call it | Add the shortcut file to the guard's allow list |
| The guard still says | "exactly one file" | "exactly two files" |
| Risk | A refactor of a large recorder file | The allow list grows, and the next feature points at it |

**Recommended: move it out.** The question is one question, so it is asked in one place, and the
guard stays at one file. The new file asks nothing the recorder did not already ask: VRChat's
window by name, and whether it is in front.

**Done 2026-10-05.** The lookup is now `VRChatWindow.cs`. It is the recorder's own code moved
across unchanged (the window by class and title, then by title alone; its picture's size and
where it starts on the desktop; whether it is in front; whether it is minimised; which process
drew it), and the recorder calls it. The guard now says that exactly one file asks and that file
is `VRChatWindow.cs`, that it names VRChat and enumerates nothing, and that it holds no keyboard
hook, no key-state call and no event hook. The desktop overlay's placement (§3.6) is its second
caller, so the rule is still "one file", not two.

**Changed 2026-10-10.** The lookup no longer falls back to the title alone: it asks for the Unity
window class and the title together, and a window it already holds is dropped when it is not of
that class. Steam's launch-options dialog for VRChat is titled "VRChat" too, and the bubble and the
overlay panel attached to it. Telling the two apart by class keeps the guard as it was: nothing asks
which program owns a window, and no process is looked at.

---

## 3. Modbot's Escape Menu

### 3.1 What the reference shows

Nine screenshots of VRChat's desktop UI were kept as reference (not committed). What matters:

- The HUD top-left is a row of round bubbles, each with an icon and its key under it: **Esc**
  (hamburger), **R** (circle, the radial menu), **Tab** (pointer), **Y** (chat). The active one
  lights green.
- The HUD top-right is a column: **V** (mic, green when open, red slash when muted), **F4** (face),
  **F5** (emote).
- Menus and world panels can cover both edges.
- **While Esc is open, VRChat lists hotkeys directly under the row** (`Ctrl+N Hide Player
  Nameplates`, `Ctrl+H Hide Desktop HUD`). A bubble under Esc would hide them. This is why the
  bubble is a fifth slot in the row, not a second row (decided 2026-10-05, between three options:
  under the row, after Y, and under the hints).
- Two HUD sizes were seen in pictures: the nine first screenshots, and a later one with Esc open in
  which the row is about 7% bigger and starts at the top of the picture (no title bar). Measuring
  on a real window afterwards (§3.3.1) showed the HUD follows the window's height by one rule, so
  those two pictures are two window sizes, not two HUD sizes.

### 3.2 What it is

One bubble, **the fifth slot of VRChat's row, directly after Y**, with the same icon size, label
pill, label text height and top as the others. It shows Modbot's face, in the grey of VRChat's
icons, and the current shortcut written the way VRChat writes its own labels (`F1`, or
`Ctrl+Alt+M`). It turns Modbot purple (the icon white) while the desktop overlay is open. Clicking
it opens and closes the overlay, the same as the key. It carries no sentence of text (`CLAUDE.md`:
controls, not explanations). The idle label pill is VRChat's near-black, `#0A040C` (about
rgb(10,4,12), practically opaque), not navy; lit, it is Modbot purple with white text.

**It has a backing panel of its own** (changed 2026-10-05, reversing the first draft's "no backing
panel", which feared looking like part of a panel that is not there; the owner's real VRChat draws
one behind its four bubbles, and a bubble without one looked out of place). It is the same look as
VRChat's, drawn as part of the bubble's one window, so there is one window and one click target and
the whole window takes the click. Measured on a real 1920 × 1080 client (S = 1.0704), VRChat's panel
is a rounded dark rectangle at x 26 to 301 and y 26 to 123, which at scale 1 is x 24.3 to 281.2 and
y 24.3 to 114.9. It is black at about 22% alpha (the pixel (57,31,15) became (45,24,10) inside it),
with a corner radius of about 10 px at S 1.07 (not measured closer: about 9.5 at scale 1, one
constant). Its padding at S 1.07 was left 17, right 12, top 17, bottom 19 around the bubbles.

Modbot's panel, all in `EscapeBubbleMetrics` and all times S: its top and bottom are VRChat's
(24.3 and 114.9, so the two panels line up within a pixel); its left edge is 3 past VRChat's right
edge (281.2 + 3 = 284.2, rounded up so the panels never overlap); the bubble keeps its place on
the row (the slot's pill left edge, 293 at scale 1), which leaves about 9 between the panels; the
padding right of the pill is 11. A long label grows the panel to the right and moves nothing else.
The window is the panel, so the bubble's window origin is the panel's top-left. Measured: VRChat's
panel at 1920 × 1080 only. Not measured: its corner radius to the pixel, or its look at any
other size. At 2560 × 1440 the numbers are the rule's, not seen.

The label has the height of VRChat's own (capitals 11 px at the smaller HUD, 15 px type). A label
too long for one slot (`Ctrl+Alt+M`) **widens the bubble** rather than shrinking the text, and
grows to the right from the slot's left edge, so it never reaches back into the Y bubble. A long
label is wide beside the others, which is the argument for a one-key default (§5).

### 3.3 It is a separate window

VRChat draws its HUD inside its own graphics device, and drawing inside that device is ruled out
(§3.4 of the earlier spec). So the bubble is **a second small always-on-top window** that sits over
VRChat's picture, not part of VRChat's HUD.

That has three honest limits:

1. **Position.** It follows VRChat's window. That needs the window's rectangle, which is a third
   question asked of Windows about another program's window, and it belongs in the same single
   file as §2.2. VRChat's HUD scale changes with the window size and the user's UI setting, so the
   bubble's position is worked out from the window size and may sit a little off on unusual
   settings. A moderator can nudge it; the offset is saved.
2. **Covering.** VRChat's own menus draw inside VRChat, so they are *under* an always-on-top
   window and the bubble would sit over them. Whether VRChat's Esc menu is open cannot be known
   from outside VRChat, so the bubble cannot hide itself then. It stays visible and small, and the
   moderator can turn it off. (The bubble sits in the row; the desktop overlay is placed in the
   free strip *beside* the menu, §3.6, so the two do not cover VRChat's menu.)
3. **Exclusive fullscreen.** It cannot show over it, same as the overlay itself (§3.4). Borderless
   works.

#### 3.3.1 Where it starts: one rule for every window size

**This reverses the first draft's "two known sizes only" decision** (and its 1.074 claim and its
two-size table, which are removed). On 2026-10-05 the owner's real VRChat was measured at two real
sizes, and one rule fits both: **VRChat's HUD scale follows the window's height.**

> **S = the client area's height / 1009** (the client area is the window without its title bar).

| Client area | S | What was measured there |
|---|---|---|
| 1920 × 1009 | 1.000 | Row icons centred at x 56, 120.5, 185.5, 249.5 (pitch 64.5); icon top y 41, icons 30–31 high. |
| 2560 × 1440 | 1.427 | Pitch 92.3, which is 64.5 × 1440 / 1009. The width does not come into it: 1.427 is the height's ratio, not the width's (1.333). |

Any other size gets the rule, with no list of known sizes and no minimum: a small window gets a
small bubble and a small overlay, the way VRChat's own menu shrinks (the owner chose this,
2026-10-05, over keeping a minimum size). Only an empty or nonsensical client area gives no place.

**The row at scale 1**, from the client area's top-left corner. All of it times S.

| Part | At scale 1 |
|---|---|
| Esc bubble's centre, x | 55.5 |
| Pitch between bubbles | 64.5 (so Modbot's fifth slot is centred at 313.5) |
| Icon top, y | 40 (30–31 high) |
| Label pill top, y | 78; 20 high; at least 41 wide |

**The Esc menu, the free strip, and what to keep out of.** All in the same client pixels, times S
where it is a distance.

| Part | Rule | Seen at |
|---|---|---|
| The Esc menu | Centred on the client width. Its right Wings panel ends at **W/2 + 492·S** (the left side is the same distance the other way) | 959 + 493 = 1452 at 1918 × 1008; 960 + 528 = 1488 in a second picture whose true window size is unknown (S ≈ 1.07) |
| The free strip on the right | From the menu's right edge to the client's: **W/2 − 492·S** wide | 467 at 1918 × 1008, 433 at 1920 × 1080, 578 at 2560 × 1440, 289 at 1280 × 720 (worked out from the rule, not seen) |
| The top-right column (V, F4, F5) | Right edge 23·S in from the client's right edge, left edge about 118·S in from it (x 1800 at W = 1918), bottom about 198·S down | 1918 × 1008 picture |
| The top-middle notification band | About y 0 to 175·S, x from W/2 − 300·S to W/2 + 200·S. **Approximate**, used only as a box to keep out of | Red-hand "Moderation" tiles in a picture, which are believed to be notifications |

**Which of this was measured, and what was not.**

- **Measured on the real window:** the HUD scale rule and the row's geometry at 1920 × 1009 and at
  2560 × 1440 (pitch, icon top). Good to about a pixel.
- **Read off pictures by eye, good to a few pixels:** the Esc menu's reach (492·S) at 1918 × 1008,
  the top-right column, the notification band, the label pill's top and size.
- **Only the Launch Pad page of the Esc menu was measured.** Other pages (and settings that widen
  the menu) may be wider than 492·S either side; the strip would then be narrower than the code
  thinks and the overlay would overlap the menu. Follow-up.
- **Not known:** the true window size of the second picture (960 + 528), whether 1009 is exactly
  right for the rule at every size, and how the HUD behaves at an unusual UI-size setting.
- **Nobody has seen the result on a live VRChat window.** The first run on one is the real check.

All of it is a named constant in `VRChatHudLayout` (the menu, strip, column, band and overlay) and
`EscapeBubbleMetrics` (the row), so a live window can correct a number in one place.

### 3.4 When it shows

Built 2026-10-05 and **on by default with the desktop overlay: there is no switch for it yet.**
The bubble is shown while all of these are true: the desktop overlay setting is on, VRChat's
window is found, it is not minimised, and its client area gives sizes (any size, §3.3.1). Otherwise
it is hidden and its window closed, so nothing is left behind. VRChat does **not** have to be the
window in front (the overlay does not need that either), which reverses the first draft's "only
while VRChat is in front". It is never given the keyboard: shown without activating, and a click
runs the shortcut's own action, which opens or closes the overlay. It says the overlay's current
shortcut (`Ctrl+Alt+M` by default) and changes when the shortcut changes, and it is Modbot purple
while the overlay is open and grey while it is closed. It looks at VRChat's window every 120 ms (about 8 times a second, so a resize catches up quickly)
and moves or resizes when VRChat does (`EscapeBubblePlan` decides, `EscapeBubbleHost` carries it
out). Its own timer runs while the overlay setting is on; the overlay's follow timer runs only while
the overlay is open, so they are two timers. Checked against the owner's real 1920 × 1080 window: the row's icons are
centred at x 60, 129, 198 and 267, so the fifth slot's centre is about 336, which is what the rule gives;
VRChat's faint backing panel ends at about x 301 and the bubble sits just outside it.
Not in a headset session.

**Reversed 2026-10-05 at the owner's request.** This spec first had the bubble off by default
(§4). The owner ran the build, expected the bubble in VRChat's row as soon as the overlay was on,
and did not find it, because only the Debug-page preview existed. The Debug page's Show escape
bubble button stays; it does nothing while the real bubble is showing, and a preview open when the
real one appears is closed, so there is never a second bubble.

### 3.5 Click-through

The bubble takes clicks only on itself. Everywhere else in its window is click-through, so it can
never block a click meant for VRChat.

### 3.6 Where the desktop overlay goes

Built 2026-10-05. The overlay used to be a fixed 520 × 720 window down the screen's right-hand
side, which covered VRChat's right Wings panel. It now fits in the free strip beside the Esc menu
and scales with VRChat's window (`VRChatHudLayout`, `DesktopOverlayWindow`).

- **Size.** Width = the strip's width less a gap of 12·S on each side. The scale k = width / 520,
  and the height is 720·k. If that is taller than the space from just under the corner column
  (198·S + 12·S down) to the client's bottom less 12·S, k shrinks until it fits. It is
  right-aligned with a 12·S margin and starts just under the corner column.
- **Worked out (rounded to whole pixels)**:

  | Client | S | Strip | Overlay (w × h, k) | Limited by |
  |---|---|---|---|---|
  | 1918 × 1008 | 0.999 | 467 | 443 × 613, 0.85 | the strip's width |
  | 1920 × 1009 | 1.000 | 468 | 444 × 614, 0.85 | the strip's width |
  | 1920 × 1080 | 1.070 | 433 | 407 × 563, 0.78 | the strip's width |
  | 2560 × 1440 | 1.427 | 578 | 543 × 751, 1.04 | the strip's width |
  | 1280 × 720 | 0.714 | 289 | 271 × 375, 0.52 | the strip's width |

  None overlaps the menu, the corner column or the notification band (tested).
- **How.** The panel is laid out at 520 × 720 and drawn through a scale transform, so layout,
  scrolling and clicking work at any size. The window is sized in the panel's own units, which are
  a screen's pixels over its scale, using the scale of the screen the overlay lands on (VRChat's
  rectangle and the window's position are both in desktop pixels).
- **When.** When the window is shown it asks `VRChatWindow` where VRChat is. While it stays
  shown it asks again 4 times a second, and moves and rescales the overlay only when VRChat's
  window has moved or been resized (so a moderator who drags the panel keeps it there until
  VRChat changes).
- **Fallback.** No VRChat window, a minimised one, or one with no room for a strip (narrower than
  the menu, for example a portrait window) gives today's placement exactly: 520 × 720 down the
  right-hand side of the screen Modbot's own window is on. This is why the overlay never refuses a
  size and never overlaps the menu: where it cannot do the first it does the old thing.
- **What it does not change:** the overlay's contents, its shortcut, its settings, how it is shown
  and hidden, or the keyboard: it is positioned and sized only, never activated again.
- **Not done:** `DesktopNotifyWindow` (Modbot's own corner notification) is not moved. The
  notification band's box is exposed by `VRChatHudLayout` so a later design can keep out of it.

---

## 4. The setting

`desktopOverlay` gains two fields; the rest is as in §6 of the earlier spec.

```json
"desktopOverlay": {
  "on": true,
  "shortcut": "f1",
  "onlyInVRChat": true,
  "bubble": true,
  "bubbleOffset": { "x": 0, "y": 0 }
}
```

| Field | Meaning | Default |
|---|---|---|
| `onlyInVRChat` | Register the shortcut only while VRChat is in front. Allows a bare function key. | `false` |
| `bubble` | Show Modbot's bubble in VRChat's row, after Y | `false` |
| `bubbleOffset` | Nudge from the worked-out position, in pixels | `0, 0` |

`onlyInVRChat` is **off by default**: it is a claim on the moderator's keyboard that is asked for,
not assumed. **`bubble` was meant to be off by default too, and that is reversed (2026-10-05, at the
owner's request): the bubble is shown whenever the desktop overlay is on, and neither `bubble` nor
`bubbleOffset` exists yet.** They are the switch and the nudge to add if a moderator wants the
bubble off or moved; until then the row above is the design, not the code. Reading an old
`settings.json` without the fields gives the defaults.

A shortcut that cannot be read, or has no modifier while `onlyInVRChat` is off, still falls back to
`mod+alt+m`.

---

## 5. The default shortcut

`Ctrl+Alt+M` stays. The reasons in §2.3 of the earlier spec still hold, and the only cost, AltGr
layouts, is why it is a setting.

If a moderator wants one key: `F1` or `F9` with the VRChat-only choice. **Neither is used by
VRChat on desktop** — stated by the project owner on 2026-10-05, who plays on desktop and sees
neither bound. It is not checked against a published VRChat key list, and the reference shows only
`Esc`, `R`, `Tab`, `Y`, `V`, `F4` and `F5`. The earlier spec lists `F5`–`F8` for the camera, which
does not match the screenshots, so that line of the earlier spec is out of date. If a VRChat
update binds either key, the registration still works and the key simply stops reaching VRChat, so
the choice stays with the moderator.

---

## 6. What was decided against

- **A keyboard hook, to see the press only in VRChat.** The earlier spec §2.1 rules it out, and it
  would be needed for nothing: a registration that exists only while VRChat is in front does the
  same job without reading any key.
- **A win-event hook for the focus change.** Not a keyboard hook, but it is a new kind of
  capability for a quarter second of latency. A poll is simpler and enough.
- **Bare keys machine-wide, by choice.** Same reason as before.
- **Drawing the bubble inside VRChat.** Ruled out by §3.4 of the earlier spec.
- **Changing the default to `F1`.** It would swallow `F1` in every program, and the moderator
  never chose that.

---

## 7. What is tested

- A bare function key is refused with `onlyInVRChat` off and accepted with it on; a bare letter,
  digit or `escape` is refused either way.
- Old settings with neither new field read as both off.
- The poll registers when VRChat comes to the front and releases when it leaves, with a fake that
  says which window is in front. Taken keys are retried once per coming-to-front, not per poll.
- Nothing polls when the overlay is off or the choice is off.
- The source guard: still exactly one file asks Windows about another window, and it is the new
  one; no hook and no key-state call appears anywhere.
- The bubble is in the slot after Y, at the row's height, left edge fixed at any width (growing right), and
  grows with the HUD scale (the client height over 1009, §3.3.1); an empty client area gives no place.
- The free strip and the overlay's rectangle at 1918 × 1008, 1920 × 1009, 1920 × 1080, 2560 × 1440
  and 1280 × 720: strip widths 467, 468, 433, 578 and 289; the overlay never overlaps the menu, the
  corner column or the notification band, and stays inside the window; with no VRChat window, a
  minimised one, or one narrower than its menu, the overlay is not placed by the maths and keeps
  today's placement.
- The bubble is drawn with the right label for a bare key and for a chord, and takes no clicks
  outside itself.

---

## 8. Open

- Whether the Esc menu's other pages are wider than the Launch Pad page's 492·S (§3.3.1). If any
  is, the strip is narrower there and the overlay overlaps it.
- The second picture's true window size (960 + 528), and the HUD at an unusual UI-size setting.
- Where the bubble and the overlay sit on a live window: nobody has seen either yet.
- Where Modbot's own corner notification goes beside VRChat's notification tiles (a better design
  waits for a reference from the owner).
- Whether the bubble should be hideable from the overlay's own strip as well as from Settings.

## 9. Handoff to a working environment

Written 2026-10-05 from a cloud container with no Windows, no VRChat and no GitHub write access,
so none of this was built or seen working. The next environment picks it up from here:

1. **Windows with VRChat running**, windowed, at more than one size. The cloud container's Xvfb
   cannot show VRChat, and `RegisterHotKey`, `FindWindowW` and `GetForegroundWindow` exist only on
   Windows (the client already returns `NotOnThisSystem` elsewhere).
2. **Check §3.3.1's numbers** against the live window (the rule and the row were measured on one
   real window at two sizes; the rest was read off pictures): put the bubble where the rule says,
   look at it beside the Y bubble, open the Esc menu and look at where the overlay sits beside it
   (on every page of the menu), resize VRChat's window and watch the overlay follow, try a monitor
   whose Windows scale is not 100%, and correct `VRChatHudLayout` and `EscapeBubbleMetrics`. Take
   a screenshot pair, with and without the bubble, for the next person.
3. **Confirm `F1` and `F9`** really do nothing in VRChat on that machine (§5).
4. **Build order:** the lookup move (§2.2) and its guard change first, then the poll and the
   `onlyInVRChat` setting, then the bubble. Each is useful without the next.
5. **Reference material not in the repository:** the nine VRChat desktop screenshots (kept in the
   cloud session's scratchpad, not committed). They are VRChat's own interface; if they are to be
   kept, a person decides where, and not this spec.
6. **This spec's own commit** was made locally and never pushed (the Claude GitHub App was not
   installed on the Modbot organization). Check `git log` on `staging` for it before writing it
   again.
