# Modbot — The notification bleep, the tray notice and Restart

- **Date:** 2026-09-18
- **Status:** Implemented with this document
- **Covers:** the short sound the client plays when it has something to tell the moderator; the
  notice shown when the window is closed to the tray; the **Restart Modbot Companion** button on the
  Settings page; the `notifications` object in `settings.json`
- **Related:** M3 §9 (the client), the client protocol design (pairing links and the single copy
  rule), the notifications design of the same date (the *server's* notification pipeline — unrelated
  to this, which is sound on one PC and never leaves it)

---

## 1. What this adds

Three things a moderator asked for, all of them on the machine the client runs on:

| | What it is |
|---|---|
| The bleep | A short two-tone sound when the client has something worth telling: a flagged arrival, or a fault that has stopped reporting |
| The tray notice | A small panel by the clock saying the client is still running, shown the first few times the window is closed with the X |
| Restart | A button on the Settings page that stops this copy cleanly and brings up a fresh one |

**Nothing here leaves the machine and no server is told any of it.** The sound is made from a
formula in code, played on this PC. The notice is a window on this desktop. The restart starts
another copy of the same program on the same PC. None of the three is reported anywhere, and the
counts and switches they keep live in `settings.json` beside every other client setting.

---

## 2. The bleep

### 2.1 Made in code, not shipped as a file

The voice stack already works in `float[]` samples (`VoiceClip` in `Voice/VoiceOutput.cs`), so the
bleep is two sine tones written into an array: **880 Hz for 80 ms, then 1245 Hz for 110 ms**, each
with a 6 ms fade in and out so neither tone starts or ends on a click. Sample rate 48 kHz; the
Windows player resamples to whatever the device wants and OpenAL takes it as it is.

Two tones rather than one because one tone is a beep from anything — a UPS, a microwave, a dozen
programs. A rising pair is recognisable as *this* program's, and it is still under a fifth of a
second.

Generating it costs nothing to ship and nothing to license, and it means there is no audio file in
the repository whose provenance somebody has to check. `Sounds/Bleep.cs` is the whole of it.

### 2.2 Through the voice's device, on its own switch

The bleep plays through **the same output device the moderator chose for the voice** — the same
`OutputDeviceChoice.Resolve` rule, so a chosen headset that is not plugged in falls back to the
system default rather than to silence — and through the same `IVoicePlayer`, so there is one audio
output object in the process rather than two enumerators fighting over the same device list.

Everything else about it is its own: **its own on switch and its own volume**, both in the
`notifications` object and neither touched by the Voice card. A moderator who wants a sound but not
a talking PC — which is most of them — turns the voice off and leaves the bleep on.

The bleep is **on by default**, where the voice is off by default. The reasoning that makes the
voice off by default is that a PC which starts talking, on whatever speakers happen to be on, is a
surprise nobody asked for. A fifth of a second of tone when somebody flagged walks in is not that:
it is the shortest possible form of the thing the client exists to tell you, and a moderator who
does not want it has one switch to find.

### 2.3 One event, one bleep

`Sounds/BleepRule.cs` holds the same kind of rule the voice's `AnnouncementQueue` holds, for the
same reason: events are not paced like sounds.

- **The same thing does not bleep twice.** The same kind about the same person inside 30 seconds is
  one bleep. The overlay and the reading half can both notice the same rejected token; the moderator
  hears it once.
- **A rush is still one bleep.** After a bleep, nothing bleeps again for 2 seconds. Ten people
  arriving at once is one sound, not ten.
- **The Test button always sounds.** A person pressed it; refusing them a sound because of a rule
  they cannot see would read as broken. It still sets the 2-second gap for whatever comes next.

Unlike the voice's queue, a bleep that is refused is dropped rather than held: a sound played two
seconds late says nothing the sound played on time did not.

### 2.4 What bleeps

The two kinds the voice treats as alerts, and the test:

| Kind | When |
|---|---|
| Flagged join | The paired server raised a flagged-join alert for the instance the moderator is in — the same moment the overlay draws its card |
| Problem | A server rejected this device, so reporting to it has stopped and will not restart on its own |
| Test | The **Test** button on the Notifications card |

Joins and leaves do not bleep. They are the ordinary traffic of a busy instance and a sound for each
would be a sound all evening; the voice already says them for anybody who wants them said.

Pausing does not silence the bleep. Pausing means "stop watching what I do", and it does silence the
voice, which narrates the instance. A flagged-join alert comes from the server and the overlay draws
its card whether or not reporting is paused, so a sound pointing at a card that is on screen is
honest. Nothing about the moderator is observed to make it.

### 2.5 When it cannot play

No output device, a device that vanished mid-clip, an audio library that will not load: the failure
is written to the client's log and everything else carries on. The bleep is the least important
thing the client does.

---

## 3. "Modbot is minimised to the tray"

### 3.1 Why a window and not a balloon

Avalonia's `TrayIcon` (11.3) has an icon, a tooltip, a menu and a click. It has **no balloon or
notification call at all** — the type carries nothing of the sort. The alternatives were a Windows
toast, which means another dependency and an app-identity registration for one sentence, or drawing
the notice ourselves.

So the client draws it: a small borderless window at the bottom right of the working area, above
the tray, showing Modbot's mark and one line. It does not take focus, it is not in the taskbar, it
closes itself after four seconds, and clicking it closes it at once. `TrayNoticeWindow.cs` is the
whole of it, and on a desktop where it cannot be placed it simply is not shown.

### 3.2 Shown three times, then never

**A count, not a "don't show again" box.** A moderator who closes the window twenty times a day must
not be told twenty times, and the two ways to prevent that are a checkbox or a count.

The checkbox loses. It is a control that exists only to explain the thing it is attached to, on a
notice whose entire purpose is to be read once and never thought about again — and the
no-explanatory-text rule in `CLAUDE.md` says a label names a control and that is all. A count needs
no control, no text and no decision from the person reading it: the first three times the window is
closed to the tray, the notice appears; after that the moderator knows, and it never appears again.

The count is `trayNoticesShown` in the `notifications` object, so it survives a restart. Deleting
`settings.json` brings the notice back, which is the right answer for a machine that has been reset.

---

## 4. Restart Modbot Companion

### 4.1 What the button does

On the Settings page, in its own card, as an ordinary settings action — not a red alarm, because
restarting is a normal thing to do after changing a device or installing a new voice; and not a
single click either, because it interrupts reporting for a few seconds. The first press arms it and
the caption becomes **Restart now**; the second press does it. It disarms itself after five seconds
if nothing else is pressed.

The sequence is:

1. Ask Windows to open `modbot-companion://restart`.
2. If Windows could not, say so on the card and **carry on running**. Nothing has been stopped yet.
3. Otherwise stop what the tray's **Quit** stops, in the same order — the reading loop, the overlay
   loop, the controller loop, the voice loop, the update checks, the link inbox, the event backup,
   the overlay and its host, the voice — and shut the application down.

Stopping first and starting afterwards is impossible: there would be nothing left running to do the
starting. So the fresh copy is started first and is made to wait, which §4.3 covers.

### 4.2 Why a link and not "start this program"

The client must never launch a process. `CompanionSourceGuardTests` fails the build if
`System.Diagnostics.Process` appears anywhere in either client project, and that ban is one of the
things that separates this program from the infostealer it is shaped like. A restart button that
started `Environment.ProcessPath` itself would have to break it.

It does not need to. The client already has a way of being started by Windows: the
`modbot-companion://` scheme it registers for itself on every start, which is how pairing from a
browser reaches it. Opening `modbot-companion://restart` hands the address to Windows exactly the
way "Pair with a server" hands it `https://my.modbot.co/…`; Windows reads its own registration and
starts `Modbot.exe`. The client starts nothing, inspects nothing and attaches to nothing.

It also fails in the right direction. On a machine where the registry refused the registration there
is no handler, the launch returns false, and the moderator is told the restart did not happen while
the client keeps reporting — rather than being left with nothing running at all.

### 4.3 The fresh copy waits for the old one

The client is single-instance: one mutex per Windows account, one tray icon, one log reader. A
second copy that finds the mutex taken hands its link to the running copy and exits. A fresh copy
started by the restart link would do exactly that — hand "restart" over and leave, and nothing would
have restarted.

So the restart link is recognised in `Main` **before** the single-copy check, and a copy started with
it waits for the mutex instead of handing anything over:

```
old copy                              fresh copy
 |  opens modbot-companion://restart
 |                                     started by Windows, recognises the link
 |  stops its loops                    waits for the mutex (up to 30 seconds)
 |  shuts down, process exits
 |  mutex released, pipe closed  ───▶  takes the mutex, starts normally
```

This ordering is the point of the wait, and it is not only about the mutex. The pairing link inbox
is a named pipe with one server instance: the fresh copy can only open it once the old copy's pipe
is gone, and a copy that started too early would run with no inbox for the rest of its life —
pairing from a browser would silently stop working until the next restart. Waiting for the old
copy's mutex waits for its process to end, which frees both.

If the old copy has not gone after 30 seconds the fresh copy gives up the wait and does what any
second copy does: asks the running one to show its window, and exits. Two copies never run.

`CompanionRestart` holds the link, the check and the wait, in the library, so the waiting rule is
tested without starting a process. `AbandonedMutexException` — the old copy died holding the mutex
rather than releasing it — counts as the old copy being gone, because it is.

---

## 5. `notifications` in `settings.json`

One new object, written whole by `CompanionSettings.SaveNotifications`, which leaves every other
field in the file exactly as it was — the same rule as `SaveVoice` and `SaveOverlay`, including the
one that a file which cannot be read as JSON is never overwritten.

```json
"notifications": {
  "bleep": true,
  "volume": 70,
  "trayNoticesShown": 2
}
```

| Field | Means | Default |
|---|---|---|
| `bleep` | Play the sound | `true` |
| `volume` | 0 to 100, its own, not the voice's | `70` |
| `trayNoticesShown` | How many times the tray notice has been shown; at 3 it stops | `0` |

A missing object, a missing field or a field of the wrong shape takes the default, and the volume is
clamped on the way in and on the way out. No server is told any of it, and nothing here is sent
anywhere.

Which output device the bleep uses is deliberately **not** in this object: it is the voice's
`outputDevice`, read at the moment of playing. One choice for one thing — a moderator who moves the
voice to their headset has moved the bleep with it.

---

## 6. Where the code is

| Path | What |
|---|---|
| `src/Modbot.Companion/Sounds/Bleep.cs` | The two tones, as samples |
| `src/Modbot.Companion/Sounds/BleepRule.cs` | One event one bleep; the kinds |
| `src/Modbot.Companion/Sounds/NotificationSound.cs` | Plays it, through the voice's output |
| `src/Modbot.Companion/Sounds/NotificationSettings.cs` | The `notifications` object, and the tray-notice count rule |
| `src/Modbot.Companion/Startup/CompanionRestart.cs` | The restart link, and waiting for the old copy |
| `src/Modbot.Companion.App/MainWindow.Notifications.cs` | The Notifications card and the Restart card |
| `src/Modbot.Companion.App/TrayNoticeWindow.cs` | The notice by the clock |

The Voice folders are touched in one place only: `VoiceHost` now exposes the player and the device
list it already owns, so the bleep can use them instead of opening a second audio output.

---

## 7. What was deliberately left

- **No bleep for joins and leaves.** §2.4.
- **No third sound.** One sound for "something wants you", not a sound per kind: a moderator cannot
  learn a vocabulary of tones from a program they run once a week, and the screen is two feet away.
- **No Windows toast.** §3.1.
- **No "don't show again".** §3.2.
- **No restart on a schedule, and none after an update.** An update is installed at the next start
  and never underneath a running session (M3 §9.2); this button is a person choosing the moment,
  which is the same rule.
