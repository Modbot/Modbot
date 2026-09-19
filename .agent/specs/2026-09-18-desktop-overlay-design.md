# Desktop overlay — design

**Date:** 2026-09-18
**Status:** built
**Touches:** `src/Modbot.Companion/Presentation/DesktopOverlaySettings.cs`,
`src/Modbot.Companion/Presentation/CompanionSettings.cs`,
`src/Modbot.Companion.App/DesktopOverlayWindow.cs`,
`src/Modbot.Companion.App/DesktopOverlayShortcut.cs`,
`src/Modbot.Companion.App/OverlayScreens.cs`,
`src/Modbot.Companion.App/MainWindow.DesktopOverlay.cs`,
`src/Modbot.Companion.App/Program.cs`

---

## 1. What this is

A moderator who plays VRChat on a monitor rather than in a headset gets nothing from the headset
panel. The same information — who is in this instance, what Modbot has seen, one person's card —
has to reach them where they are, which is in front of a game window.

So: **a desktop overlay**. A small always-on-top window that sits over VRChat, comes up and goes
away on one keyboard shortcut that works while VRChat has the keyboard, and can be clicked and
typed into. Not a picture of the headset panel — the real panel, in a window, with a mouse instead
of a controller.

It shows only what the client already holds. It makes no request of its own, and no server can tell
it to appear, to hide, or to show anything. Everything on it came from the overlay's local cache
(the same cache the headset panel draws from) and from the client's own record of what it has done.

---

## 2. The shortcut

### 2.1 Why a system-wide shortcut and not a key handler

The whole point is that it works **while VRChat has focus**. Avalonia only sees key events for a
window that already has the keyboard, which is exactly the case this feature exists to cover. So
the shortcut has to be registered with the operating system.

On Windows there are two ways to do that:

| | `RegisterHotKey` | A low-level keyboard hook (`WH_KEYBOARD_LL`) |
|---|---|---|
| What it sees | Only the one combination it asked for | **Every key on the machine**, in every program |
| How it reads to antivirus | An ordinary window API | The signature move of a keylogger |
| Failure mode | Refuses at registration, visibly | Silently throttled or dropped by Windows |
| Anti-cheat | Uninteresting | Frequently flagged |

`RegisterHotKey` is the only acceptable answer. A client that is already shaped like spyware —
background, unattended, watching a file, posting what it sees — does not get to install a global
keyboard hook as well. `CompanionSourceGuardTests` already bans `GetAsyncKeyState` for the same
reason, and the same argument applies here with more force: a hook would give this program the
ability to read every keystroke on the machine, and no amount of "but it only looks at one key"
in a comment is worth that.

**It never reads the keyboard.** `RegisterHotKey` is a claim on one combination; Windows delivers
one message when that combination is pressed, and the client is told nothing about any other key.

### 2.2 How it is registered

`RegisterHotKey(NULL, …)` posts `WM_HOTKEY` to a **thread's** message queue rather than to a
window. Avalonia's own message loop calls `DispatchMessage`, which drops a message with no window,
so the client cannot see it there without reaching inside Avalonia.

Instead the client runs the registration on **one dedicated thread of its own** that does nothing
else: it registers the combination, sits in `GetMessage`, and when a `WM_HOTKEY` arrives it posts
the press to the UI thread and goes back to waiting. Quitting posts `WM_QUIT` to that thread, which
unregisters and ends. No window, no subclassing, no hook. `MOD_NOREPEAT` is set, so holding the
keys down summons the window once rather than a hundred times.

Changing the shortcut in settings stops that thread and starts another. It happens when somebody
presses a button, so the cost does not matter.

### 2.3 The default, and why

**`Ctrl+Alt+M`** — written `mod+alt+m` in the same token spelling the window's own keyboard uses
(`KeyTokens`, `Shortcuts.cs`). M for Modbot.

Why that one:

- **VRChat's desktop bindings do not use Ctrl+Alt.** VRChat's own keys are bare letters and
  function keys — `V` for voice, `R`, `Z`, `Esc` and `Tab` for menus, the number row for the quick
  menu, `F5`–`F8` around the camera. Nothing there is a Ctrl+Alt combination.
- **Windows does not own it.** Windows reserves Win-key combinations and `Ctrl+Alt+Del`; plain
  `Ctrl+Alt+letter` is free.
- **It survives the token spelling.** `KeyTokens.Token` deliberately drops Shift for a printable
  key, because `?` is what the key produced and `shift+/` is what one particular keyboard did to
  produce it. That makes `Ctrl+Shift+M` unwritable in this vocabulary, while `Ctrl+Alt+M` is
  written exactly. Rather than invent a second spelling for key combinations, the default uses the
  one the existing spelling can say.

The known cost: on keyboard layouts with an AltGr key (German, Polish, and others) AltGr *is*
Ctrl+Alt, so a moderator on such a layout may find Ctrl+Alt+M produces a character in other
programs. That is why the shortcut is a setting and not a constant.

### 2.4 When registration fails

Another program can already own the combination, and then `RegisterHotKey` refuses. Three rules:

1. **The client carries on.** Presence reporting, the log reader, the headset panel and the voice
   are untouched. A shortcut that would not register is a shortcut that would not register.
2. **The settings screen says so**, in one line, next to the control: *"Ctrl Alt M is already
   taken by another program."* That is an error saying what failed, which is the only explanatory
   text a screen is allowed.
3. **It is written to the client log** once, at the level of a warning, with the combination in it.

The desktop overlay can still be opened from the window while the shortcut is refused; the
shortcut is how it is reached quickly, not the only way it is reached.

---

## 3. The window

| Property | Value | Why |
|---|---|---|
| `SystemDecorations` | `None` | Borderless. A title bar over a game is wasted height and the wrong shape. |
| `Topmost` | `true` | The point of it. |
| `ShowInTaskbar` | `false` | It is summoned and dismissed, not alt-tabbed to. The client already has a taskbar button and a tray icon, so it is not hiding. |
| `ShowActivated` | `true` | It is summoned to be used, so it takes the keyboard. |
| `CanResize` | `false` | Fixed width; the panel is laid out for one column. |
| `Background` | The overlay's own ground, with the configured opacity in its alpha | See §3.3. |
| Hit testing | Ordinary | **Not click-through.** It is interactable; a ghost would be useless. |

It is created once, hidden, and shown and hidden from then on — not built and destroyed each time,
because rebuilding it would lose the scroll position and cost a layout pass while a game is
running.

### 3.1 Summoned, dismissed, and focus

- The shortcut **toggles**: visible becomes hidden, hidden becomes visible.
- **Escape** hides it. So does clicking Modbot's tray icon (which brings the main window up
  instead).
- Showing it calls `Activate()` and then Windows' `SetForegroundWindow`. Windows normally refuses
  to let a background process take the foreground, but it explicitly allows it for *"a process
  processing a hotkey"* — which is exactly the case here, since the shortcut is what asked. If
  Windows refuses anyway, the window is still on top and still visible; it just does not have the
  keyboard, and one click gives it the keyboard.
- **Until it is summoned it never touches VRChat's focus.** It is created hidden and
  `ShowActivated` only matters when `Show()` is called.
- Hiding it does **not** hand focus back to VRChat explicitly. Windows gives the foreground to the
  next window in the Z-order, which is the game. Trying to force it would mean finding and
  activating another program's window, and that is a capability this client should not have.

### 3.2 Where it goes

Right-hand side of the screen, vertically centred, with a margin. The right edge is chosen because
VRChat's desktop HUD, its menus and its nameplates all live in the middle.

The moderator can move it: the strip along the top is a drag handle (`BeginMoveDrag`). Where they
leave it is **not** saved — the position is per-session, because a saved position on a monitor that
is no longer plugged in is a window nobody can find.

**Multi-monitor.** The screen is worked out each time it is summoned, from **the screen Modbot's
own window is on**. That is as close to "the monitor the moderator is using" as this client can get
without going looking at other programs' windows, which is a capability it has no other use for and
should not acquire for this. A moderator with two monitors moves Modbot's window to the one they
want and the overlay follows. If no screen can be worked out, it falls back to the primary screen.

### 3.3 Opacity

A number from 20 to 100, default 90, written as `opacity` in the settings object.

It is applied as the **alpha of the window's background**, with
`TransparencyLevelHint = Transparent`, rather than as `Window.Opacity`. Two reasons: the text stays
fully opaque and therefore readable while the ground behind it thins out, which is what a moderator
actually wants from a panel over a game; and a whole-window opacity is a compositor feature that
some setups simply do not have, and losing it would make the text unreadable rather than the ground
opaque. Where transparency is not available the window renders with an opaque ground and everything
still works.

### 3.4 Fullscreen — the real limitation

**Borderless-windowed VRChat is the case that works, and it is the case VRChat ships as the
default.** A topmost window draws over a borderless-windowed game exactly as it draws over any
other window, because there is no difference: a borderless-windowed game is a normal window that
happens to cover the screen.

**Exclusive fullscreen is different and cannot be fixed from here.** In exclusive fullscreen the
game owns the display's swap chain, the desktop compositor is out of the path, and there is nothing
for another window to be drawn over. What actually happens on Windows 10 and 11 is one of two
things, and which one depends on the driver and on whether fullscreen optimisations are on for
VRChat:

1. **Fullscreen optimisations on (the Windows default).** Windows silently runs the game as
   borderless-windowed behind the scenes, and the overlay appears normally. This is the common
   case.
2. **True exclusive fullscreen.** Showing a topmost window forces the game to give up exclusive
   mode — the screen flickers, the game minimises, or both. This is bad, and it is the behaviour
   of every window that is not a hooked in-game overlay.

Modbot does not hook the game. Drawing inside another program's Direct3D device means injecting
code into it, which is what anti-cheat exists to detect and what this client must never do — the
same argument as §2.1, one layer down. A moderator who wants the desktop overlay and is in true
exclusive fullscreen sets VRChat to borderless-windowed, which is one setting in VRChat's own
menu and what most people already run.

**This limitation is written here and not on the screen.** A caption under a switch explaining
Direct3D swap chains to a volunteer moderator is exactly the explanatory text the project bans.

---

## 4. What it shows, and why it is the panel rather than a second screen

`OverlayView.Build(OverlayScreen)` returns an **Avalonia control tree**, not a bitmap. The headset
path rasterises that tree into a texture; nothing about the tree needs a headset. So the desktop
overlay hosts the very same controls in a window and gets, for free:

- the roster, sorted and coloured by standing, with flags and trust ranks
- the flagged-join alert card
- a person's card, with everything the headset shows about them
- the health banner
- and, because `OverlayView` tags its controls with `OverlayTarget`, **hit testing that already
  exists**: `OverlayTargets.At(root, point)` is what a controller ray uses to find what it is
  pointing at, and a mouse position goes through the same call to the same targets.

Tapping a roster row opens that person, tapping the card closes it, tapping the alert dismisses it
— through `OverlayDriver.Tap`, which is the same method the controller calls. The wheel scrolls the
roster through `OverlayDriver.ScrollRoster`, which is what the controller's thumbstick calls. **No
second set of interactions was written.**

The person's card carries the actions the client already has, which today is: open this person,
close this person, dismiss this alert. The client has no ban button and this does not add one —
acting on a person is done in Modbot's web interface, by a human, in a browser.

Below the panel the window shows the **last few events** the client handled, drawn with
`MainWindow`'s own `EventRow` — the same row the Events page draws, with the same time column, the
same sentence and the same per-destination pills. That row builder changed from `private static` to
`internal static` and nothing else.

### 4.1 What was decided against

- **A bitmap of the headset frame.** `OverlayPreviewWindow` already does that and it is the right
  answer for the Debug page, where the question is *"what did SteamVR actually get"*. It is the
  wrong answer here: it is a picture, so it cannot be clicked, its text is rasterised at the
  headset's scale, and it only exists while a frame has been drawn.
- **A new screen built from `Ui.*`.** It would have been a second, drifting copy of the roster and
  the person card. The overlay's view is already the agreed shape for "the smallest useful thing a
  moderator needs mid-instance", and two of them would disagree within a month.
- **Docking it to VRChat's window.** Finding and tracking another program's window means walking
  the window list, which is a capability with no other use in this client and a poor one to have.

---

## 5. Where the data comes from

The drive loop — `OverlayDriver` — polls the current server's roster, holds the live link open, and
pushes an `OverlayScreen` into an `IOverlayPresenter` whenever the screen would look different.
There was one presenter: the headset host.

Now there are two, behind `OverlayScreens`, a presenter that hands the screen to each in turn and
reports that it drew if either of them did. The headset host is still one of them, and is still
only built when the headset panel is switched on. The desktop overlay is the other.

### 5.1 One switch, two panels

`OverlaySwitch` still owns whether the overlay exists at all, and it is still the only thing that
builds it — `CompanionSourceGuardTests.TheOverlayIsOnlyEverBuiltThroughItsOnOffSwitch` holds that,
and the reason behind it is unchanged: the driver opens a live connection to a paired server, and a
moderator who wants no overlay must not get one by a path that forgot to ask.

What changes is the **question the switch is asked**. It used to be "is the headset panel on"; it
is now "**does either panel want to run**" — the headset panel's `overlayOn`, or the desktop
overlay's `desktopOverlay.on`. Turning either one on or off rebuilds the overlay so the halves that
are built are the halves that are wanted:

| `overlayOn` | `desktopOverlay.on` | Driver and live link | SteamVR texture, controllers |
|---|---|---|---|
| off | off | not built | not built |
| on | off | built | built |
| off | on | built | **not built** |
| on | on | built | built |

The third row is the new one, and it is the moderator asking for it in as many words. Nothing here
starts reading from a server that the person running the client did not switch on.

### 5.2 Nothing is commanded

The desktop overlay is a presenter: something is pushed into it. It has no client of its own, no
address, no socket. It cannot be told to appear, and a server that wished it could has nowhere to
say so. The same sentence that is true of the headset panel is true of this, for the same reason —
it is the same loop.

---

## 6. The setting

One new object in `settings.json`, written and read whole by
`CompanionSettings.SaveDesktopOverlay`, which rewrites `desktopOverlay` and leaves every other
field exactly as it found it — the same rule as `SaveOverlay` and `SaveVoice`:

```json
"desktopOverlay": {
  "on": false,
  "shortcut": "mod+alt+m",
  "opacity": 90
}
```

| Field | Meaning | Default |
|---|---|---|
| `on` | Whether the overlay window and its shortcut exist | `false` |
| `shortcut` | The combination, in `KeyTokens` spelling | `mod+alt+m` |
| `opacity` | 20–100; the ground's alpha, never the text's | `90` |

**Off by default**, unlike the headset panel. A system-wide shortcut is a claim on every other
program's keyboard, and that is a thing to be asked for rather than assumed. It is one switch on
the Settings page.

A shortcut that cannot be read — an empty string, a key nobody has, no modifier at all — falls back
to `mod+alt+m` rather than leaving the moderator with nothing. **A shortcut with no modifier is
refused on purpose**: a bare letter registered system-wide would swallow that letter in every
program on the machine, including the game.

---

## 7. What is tested

Everything that does not need a window, in `tests/Modbot.Companion.Tests/`:

- reading a shortcut token into the modifiers and key Windows wants, for letters, digits, function
  keys and named keys
- refusing a token with no modifier, an unknown key, `escape` (which dismisses and so cannot also
  summon), and rubbish
- opacity clamped into 20–100, in the file and in the record
- `desktopOverlay` round-tripping through the file, including that writing it leaves `voice`,
  `overlay` and the rest alone, and that a file that is not JSON is not overwritten
- the show/hide rule: the shortcut toggles, Escape only hides, and a press while the overlay is
  switched off does nothing

The window itself, `RegisterHotKey`, and what a compositor does with a topmost window are checked
by hand, on a machine with VRChat on it.
