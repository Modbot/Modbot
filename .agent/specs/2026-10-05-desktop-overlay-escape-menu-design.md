# Desktop overlay — Modbot's Escape Menu and a VRChat-only shortcut

**Date:** 2026-10-05
**Status:** draft — not built
**Touches (when built):** `src/Modbot.Companion/Presentation/DesktopOverlaySettings.cs`,
`src/Modbot.Companion.App/DesktopOverlayShortcut.cs`, `src/Modbot.Companion.App/ScreenRecording.cs`
(or a file split out of it), a new bubble window, `MainWindow.DesktopOverlay.cs`,
`tests/Modbot.Companion.Tests/Guards/CompanionSourceGuardTests.cs`

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
- Two HUD sizes were seen: the nine first screenshots, and a later one with Esc open in which the
  row is about 7% bigger and starts at the top of the picture (no title bar).

### 3.2 What it is

One bubble, **the fifth slot of VRChat's row, directly after Y**, with the same icon size, label
pill, label text height and top as the others. It shows Modbot's face, in the grey of VRChat's
icons, and the current shortcut written the way VRChat writes its own labels (`F1`, or
`Ctrl+Alt+M`). It turns Modbot purple (the icon white) while the desktop overlay is open. Clicking
it opens and closes the overlay, the same as the key. It carries no sentence of text (`CLAUDE.md`:
controls, not explanations), and no backing panel of its own: VRChat's faint panel is not always
drawn, and Modbot's bubble should not look like part of a panel that is not there.

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
   moderator can turn it off.
3. **Exclusive fullscreen.** It cannot show over it, same as the overlay itself (§3.4). Borderless
   works.

#### 3.3.1 Where it starts: two known HUD sizes

The first version knows **two client areas** (VRChat's window without its title bar) and refuses
every other, so a bubble is never put in the wrong place:

| Client area | HUD scale | Where seen |
|---|---|---|
| 1918 × 1008 | 1.00 | The first nine screenshots, 1918 × 1030 *including* a 22 px title bar. **The 1008 is inferred from that and has not been checked** (§9). |
| 1918 × 1030 | 1.074 | The Esc-open screenshot, which has no title bar. |

The row at scale 1, measured from the client area's top-left: Esc centre x ≈ 55.5, the bubbles
64.5 apart (so Y is at 249 and Modbot's slot is at 313.5), icons start y ≈ 40, label pills start
y ≈ 78 and are 20 high and 41 wide at least. Everything grows with the scale. The 1.074 comes from
the distance between bubbles in the two sets of screenshots; it is **not** the ratio of the window
heights (1.022), so HUD size is not simply the window's height, and a third size cannot be guessed
from these two.

These are read off pictures by eye, good to a few pixels. They live in one place in the code
(`EscapeBubbleMetrics`) so the first run in a real window can correct them.

### 3.4 When it shows

Only while VRChat is in front and the desktop overlay is on, and (first version) only while
VRChat's client area is one of the two known sizes (§3.3.1). Not in a headset session, not while another
program is in front.

### 3.5 Click-through

The bubble takes clicks only on itself. Everywhere else in its window is click-through, so it can
never block a click meant for VRChat.

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

Both new switches are **off by default**: each is a claim on the moderator's screen or keyboard
that is asked for, not assumed. Reading an old `settings.json` without them gives the defaults.

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
  grows with the HUD scale; any unknown client area gives no place.
- The bubble is drawn with the right label for a bare key and for a chord, and takes no clicks
  outside itself.

---

## 8. Open

- Where the bubble sits at any other window size and UI setting. Only two HUD sizes are known (§3.3.1).
- Whether the bubble should be hideable from the overlay's own strip as well as from Settings.

## 9. Handoff to a working environment

Written 2026-10-05 from a cloud container with no Windows, no VRChat and no GitHub write access,
so none of this was built or seen working. The next environment picks it up from here:

1. **Windows with VRChat running**, windowed at 1918 × 1030. The cloud container's Xvfb cannot show
   VRChat, and `RegisterHotKey`, `FindWindowW` and `GetForegroundWindow` exist only on Windows
   (the client already returns `NotOnThisSystem` elsewhere).
2. **Check §3.3.1's numbers** against the live window: read VRChat's client area with
   `GetClientRect` (and say whether 1008 was right for the first set), put the bubble where the
   table says, look at it beside the Y bubble, and correct `EscapeBubbleMetrics`. Take a
   screenshot pair, with and without the bubble, for the next person.
3. **Confirm `F1` and `F9`** really do nothing in VRChat on that machine (§5).
4. **Build order:** the lookup move (§2.2) and its guard change first, then the poll and the
   `onlyInVRChat` setting, then the bubble. Each is useful without the next.
5. **Reference material not in the repository:** the nine VRChat desktop screenshots (kept in the
   cloud session's scratchpad, not committed). They are VRChat's own interface; if they are to be
   kept, a person decides where, and not this spec.
6. **This spec's own commit** was made locally and never pushed (the Claude GitHub App was not
   installed on the Modbot organization). Check `git log` on `staging` for it before writing it
   again.
