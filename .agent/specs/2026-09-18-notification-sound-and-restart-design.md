# Modbot — The notification bleep, the tray notice and Restart

- **Date:** 2026-09-18, with the sound itself rewritten on 2026-09-19 (§2.6, §2.7)
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
| The bleep | A short sound when the client has something worth telling: a flagged arrival, or a fault that has stopped reporting. Five of them since 2026-09-19 (§2.6), one family, told apart by how many notes there are and which way they go |
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
bleep is notes written into an array by arithmetic. Sample rate 48 kHz; the Windows player resamples
to whatever the device wants and OpenAL takes it as it is.

Two notes rather than one because one tone is a beep from anything — a UPS, a microwave, a dozen
programs. A pair a fifth apart is recognisable as *this* program's, and it is still under half a
second.

Generating them costs nothing to ship and nothing to license, and it means there is no audio file in
the repository whose provenance somebody has to check. `Sounds/Bleep.cs` is the whole of it.

**What the notes actually are has been rewritten twice and is §2.6.** This first said two bare sine
tones, 880 then 1245 Hz, switched on and off in 6 milliseconds; the listening design of 2026-09-19
§8 replaced that with two struck notes at 440 and 660 after a moderator called the old one a smoke
alarm; §2.6 below turns those two notes into a family of five. What has not changed through any of
it is this section's own decision: made in code, never shipped as a file.

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
- **A rush is still one bleep.** After a bleep has *finished*, nothing bleeps again for 2 seconds.
  Ten people arriving at once is one sound, not ten.
- **Something worse gets through the gap.** §2.7.
- **The Test button always sounds.** A person pressed it; refusing them a sound because of a rule
  they cannot see would read as broken. It still sets the gap for whatever comes next.

Unlike the voice's queue, a bleep that is refused is dropped rather than held: a sound played two
seconds late says nothing the sound played on time did not.

**Counted from the end, not from the start.** The gap used to be measured from the moment a sound
began, which was indistinguishable from measuring it from the end while every sound was 480
milliseconds long. The doubled alert (§2.6) is 1,180, and a gap measured from its start would let
the next sound begin while it was still playing. One line, and it holds for whatever the longest
sound turns out to be.

### 2.4 What bleeps, and which sound it gets

Five sounds and eight kinds of event, so the mapping is the decision (`Sounds/Tune.cs`). A
moderator's list is written in terms of what they care about; the client knows
`NotificationKind`. This is where one becomes the other:

| What the moderator asked for | Sound | What raises it in the client |
|---|---|---|
| Informational | Soft single chime | `Joined`, `AlreadyThere`, `Left`, `ChangedAvatar` — and a clip saved because somebody said "Modbot, clip that" |
| Flagged or problem user joined | Two-note alert | `FlaggedJoin` |
| Multiple flagged users | The two-note alert, twice | More than one **different** flagged arrival inside 10 seconds (§2.7) |
| High-priority moderation issue | Three-note, sharper | `Problem` — a server rejected this device, so reporting has stopped — and `LogStopped` — VRChat's log stopped growing, so the client can no longer see the instance |
| Resolved or dismissed | Quiet falling tone | **Nothing.** The sound is built and the Test button plays it; no event in the client raises it |

Two rows need saying plainly rather than being dressed up.

**"High priority" is only those two.** Nothing else the client knows about rises to it. A flagged
arrival is already the alert; an arrival, a departure and an avatar change are information. What
`Problem` and `LogStopped` share, and what nothing else shares, is that the client has stopped
doing the job it was left running to do and will not start again on its own. That is worth the
sharper sound, and inventing a third thing to keep it company would have been inventing an event to
justify a sound.

**Nothing resolves.** The client has no event today that means "this is over". A problem is never
told it has been put right, a flagged arrival is never withdrawn, and a card dismissed on the
overlay is dismissed there without anything coming back. So the falling tone exists, is reachable
from the Test button, and is raised by nothing — which is the honest answer, and better than either
leaving the row out or making up an event for it. `TunesTests` pins that down, so the day something
does resolve, the test that says nothing does will be the thing that fails.

The three ordinary kinds and `LogStopped` are still **off by default**, as the notification filters
design set them: giving them a gentler sound does not turn them on. A sound for every arrival is a
sound all evening, and the voice already says them for anybody who wants them said — but a
moderator who does tick them now gets something they can live with rather than the alert forty
times.

Pausing does not silence the bleep. Pausing means "stop watching what I do", and it does silence the
voice, which narrates the instance. A flagged-join alert comes from the server and the overlay draws
its card whether or not reporting is paused, so a sound pointing at a card that is on screen is
honest. Nothing about the moderator is observed to make it.

### 2.6 The five sounds

All five come out of `Sounds/Bleep.cs`, out of the same arithmetic: notes struck into an array, each
coming up along a quarter-cosine, decaying by a third every 90 milliseconds, taken to exactly zero
over the last 45, with three quiet overtones above each one dying faster the higher they are. What
differs between them is how many notes, how high, which way, how hard struck and how loud the whole
thing is made. **One instrument.** That is the whole reason a moderator can tell them apart without
being taught them: they are heard as the same thing saying different words.

| Sound | Notes | Made to | Struck over | Long |
|---|---|---|---|---|
| Chime | 660 Hz, one note | 0.35 | 26 ms | 340 ms |
| Alert | 440 Hz, then 660 at 140 ms | 0.70 | 18 ms | 480 ms |
| Alert twice | the alert, then the alert again from 700 ms | 0.70 | 18 ms | 1,180 ms |
| Urgent | 660, 880 at 100 ms, 1,100 at 200 ms | 0.70 | 8 ms | 540 ms |
| All clear | 660 Hz, then 440 at 160 ms | 0.40 | 26 ms | 500 ms |

Every pitch is the alert's own 440 or something simple above it: 660 is a fifth, 880 the octave,
1,100 a major third above that. The urgent one is therefore a chord climbing rather than three
pitches picked out of the air, and the all-clear is the alert's two notes the other way up.

- **The chime is half the height of the alert.** It is the one a moderator hears most, so it is the
  one that must never be the reason they switch the sound off. Quieter *and* slower to come up:
  gentle is not the same thing as quiet, and a quiet sound that still snaps on is still a snap.
- **The alert is untouched.** The sound a moderator already knows is still exactly the sound a
  flagged arrival makes. A family built by changing the one sound everybody had learnt would have
  been a worse family.
- **The doubled alert has 220 milliseconds of exact silence in the middle**, which is deliberately
  longer than the 140 between the two notes of one pair. That is what the ear uses to hear "the same
  thing twice" rather than "four notes". The second half is the first half sample for sample.
- **The urgent one is sharper, not louder.** Its peak is exactly the alert's. What makes it urgent
  is that it is higher, that its notes come 100 milliseconds apart rather than 140, that it reaches
  half its height in under 5 milliseconds where the alert takes 9, and that its overtones are
  half again as loud — brighter, harder struck, three of them climbing. Making it louder instead
  would have made a smoke alarm, which is the mistake this whole section exists to have fixed.
- **The all-clear falls.** Falling is what makes a sound an ending rather than a question, and it is
  quiet because news that something no longer needs attention is not a demand for any.

**Neither the person who asked for these nor the person who wrote them can hear them.** So every
claim above is a number, and `BleepTests` checks each one: every sound starts and ends on exactly
zero; every one is made to exactly its own height and never past it; none reaches more than a
fraction of its height in the first 2 milliseconds; the chime is exactly half the alert's height
and the urgent one is exactly equal to it; the chime's single note measures 660 Hz off its zero
crossings; the doubled alert's middle is exactly zero and its two halves are identical arrays; the
urgent one reaches half height sooner than the alert and the alert sooner than the two soft ones;
the all-clear crosses zero less often in its second half than its first while the alert does the
opposite; and no two of the five are the same array. Somebody with speakers still has to listen to
them.

### 2.7 More than one flagged arrival

**More than one means two different flagged people inside 10 seconds.** Counted by person, so the
same person noticed twice by two halves of the client is one of them; ten seconds because that is
how long a group takes to come through a door one after another, and because two unrelated arrivals
in a quiet evening should not be reported as a crowd.

That alone would be no use, because the second arrival usually lands inside the quiet gap the first
one left and would be dropped — the sound would say "one flagged person" and stop, which is the one
moment a moderator most needs the truth. So:

**A sound may begin inside the quiet gap if it is more serious than everything already heard in that
gap.** Seriousness runs chime and all-clear, then alert, then doubled alert, then urgent. The second
flagged arrival is therefore heard, as the doubled alert, right behind the first.

This cannot run away, for three reasons and it is worth naming all three:

1. **Seriousness only goes up.** Once the doubled alert has been heard in a gap, another doubled
   alert is not worse than it and is dropped. There are four steps, so a gap can hold at most four
   sounds, each strictly worse than the last, and then silence.
2. **Nothing interrupts a sound that is playing.** `NotificationSound` claims the player before it
   asks the rule, not after, so a cut-in can only ever start after the previous sound has finished.
   That also means the rule never records having sounded something nobody heard.
3. **The gap is counted from the end.** Each cut-in pushes the next gap out by its own length.

A hundred different flagged people arriving one every tenth of a second for ten solid seconds makes
**five** sounds: the alert, the doubled alert behind it, and then one doubled alert about every
three seconds. `BleepRuleTests` runs exactly that and holds it to at most six, with at most one pair
closer together than a quiet gap.

The alternative was to hold the second arrival and decide later, which the voice's queue does. It
was refused for the reason §2.3 already gives: a sound two seconds late says nothing the sound on
time did not, and a held sound needs a timer, which is a thing to get wrong in a feature whose
worst failure should be silence.

### 2.8 When it cannot play

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
| `sound` | A `.wav` of the moderator's own to play instead of all five of Modbot's; left out of the file when there is none (listening design 2026-09-19 §8.3, and §7 below on why one and not five) | absent |

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
| `src/Modbot.Companion/Sounds/Bleep.cs` | The five sounds, as samples |
| `src/Modbot.Companion/Sounds/Tune.cs` | The five by name; which kind gets which; which of two is the more serious |
| `src/Modbot.Companion/Sounds/BleepRule.cs` | One event one bleep; the kinds; more than one flagged arrival |
| `src/Modbot.Companion/Sounds/NotificationSound.cs` | Plays them, through the voice's output |
| `src/Modbot.Companion/Sounds/NotificationSettings.cs` | The `notifications` object, and the tray-notice count rule |
| `src/Modbot.Companion/Startup/CompanionRestart.cs` | The restart link, and waiting for the old copy |
| `src/Modbot.Companion.App/MainWindow.Notifications.cs` | The Notifications card and the Restart card |
| `src/Modbot.Companion.App/TrayNoticeWindow.cs` | The notice by the clock |

The Voice folders are touched in one place only: `VoiceHost` now exposes the player and the device
list it already owns, so the bleep can use them instead of opening a second audio output.

---

## 7. What was deliberately left

- **No bleep for joins and leaves unless they are ticked**, and they are not ticked to begin with.
  §2.4.
- **~~No third sound.~~ Reversed on 2026-09-19.** This said: one sound for "something wants you",
  not a sound per kind, because a moderator cannot learn a vocabulary of tones from a program they
  run once a week. A moderator asked for five anyway, and the reasoning was wrong in a way worth
  recording. It assumed the sounds would have to be *learnt* — that a moderator would have to
  remember which of five arbitrary noises meant what. They do not, because the five are one
  instrument getting longer, higher and harder struck as the news gets worse (§2.6): a sound with
  more notes in it, sooner, reads as more urgent without anybody being taught anything, and
  somebody who never notices the difference still hears a notification. What the old text got right
  is still respected: nothing here needs a legend, and nothing on the screen explains it.
- **One sound file, not five.** `notifications.sound` still points at one `.wav` and it replaces
  every one of the five. Five paths would be five boxes and five Save buttons on a card that has to
  stay readable, and somebody who brings their own sound has said they do not want Modbot's — not
  that they want four of Modbot's and one of theirs. The cost is real: a moderator who names a file
  hears one sound for everything and loses the difference between the five, which is exactly what
  the client did before there were five.
- **No sound interrupts one that is already playing.** Cutting a clip off mid-way is a click, and
  two sounds on top of each other are neither. Something worse cuts into the *silence* after a
  sound, never into the sound (§2.7).
- **No Windows toast.** §3.1.
- **No "don't show again".** §3.2.
- **No restart on a schedule, and none after an update.** An update is installed at the next start
  and never underneath a running session (M3 §9.2); this button is a person choosing the moment,
  which is the same rule.
