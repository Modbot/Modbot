# Clips: keeping the last few minutes on a moderator's PC

**Status:** built, 2026-09-19. Changed the same day; see below.
**Reverses:** M3 client and overlay design §3.1.1 ("Screen capture — forbidden, permanently") and
§10, and evidence storage design §19 ("No automatic capture of anything").

## What changed on 2026-09-19, later the same day

This spec was written for a recorder that duplicated a whole monitor and had one control, on the
settings page. Three things were asked for straight afterwards, and three sections of it are now
wrong where they stand. They have been rewritten in place rather than left next to a contradiction.

| What changed | Where | Why |
|---|---|---|
| **A clip is VRChat's window, not the monitor.** | §3.1, §3.3, §5 | The monitor was named in §3.3 as the design's weak point and as the next thing to do to it. It is done. A moderator's monitor has their private messages on it; a clip made to show what somebody did in VRChat has no business carrying those. |
| **Linux was looked at properly and left alone.** | new §3.6 | The old answer was "Windows only" with no reasoning behind it. The reasoning is now written down, because "we did not try" and "we tried and it cannot be done well" are different claims and only one of them is true. |
| **A clip can be saved from inside VR.** | new §11, §10 | §10 called the missing control *"the biggest gap"* and said the feature was awkward until it was closed. The overlay panel already had a tap path; it now has a control on it. |

Everything §2 says about what the client promises, and everything §6 says about nothing leaving the
machine, is unchanged. Recording a window rather than a monitor narrows what is captured; it does
not widen anything.

---

## 1. What was asked for, and what this is

A moderator asked for a rolling recording of the last two to five minutes, kept while VRChat is
running, saved as a clip when something happens, off unless switched on, and efficient — their frame
rate matters more than the clip's quality. The folder is configurable and defaults to the machine's
own Videos folder plus `Modbot Clips`.

That is what this builds. It is not an addition; it is a reversal, and §2 says so plainly because
leaving the old sentence standing next to code that contradicts it would be worse than never having
written it.

---

## 2. The promise this narrows, and why

Until today the client said, in its own source and on its own documentation site, that it **never
captured the screen, by any route, for any reason**. That was not a passing remark. It was one of
four claims the client's whole argument for being trustworthy rested on, beside "it never reads the
keyboard", "it never records sound" and "it reads one folder". The argument runs: this program is,
feature for feature, shaped like an infostealer — it runs unattended on a personal machine, watches a
file, and posts what it sees to a server — and what separates it is that its behaviour is **bounded
and checkable**. Screen capture was the capability that would have completed the resemblance.

M3 §3.1.1 said the forbidden thing was *"a program that takes images off someone's machine without
them choosing each one."* Read closely, that sentence is doing two jobs: forbidding capture, and
forbidding **taking images off the machine**. This change keeps the second job entirely and narrows
the first.

What survives, unchanged:

- **Nothing recorded ever leaves the PC.** No clip, no frame, and no fact that a clip exists is sent
  to a paired server, to Modbot Cloud, or anywhere else. The client has no upload path and did not
  gain one (§6).
- **Attaching evidence to a case is still a deliberate human action** taken in Modbot's web
  interface, in a browser, by choosing a file.
- **VRChat's screenshot folder, and Pictures, Documents and the Desktop, stay out of bounds.** The
  client still never *reads* an image it did not make.
- **Sound is still never recorded, at all.** See §5.
- **The keyboard, the clipboard and the process list are all still untouched.**

What changes: **with a switch a person turned on themselves, while VRChat is running, the client
keeps the last few minutes of VRChat's own window, on that person's own disk.**

The narrowing is shaped so it stays answerable. "Show me where this program records" has a one-word
answer — `ScreenRecording.cs` — and `CompanionSourceGuardTests` fails the build if any second file
the client ships learns to. That is the same shape as the existing carve-outs for the installer
(`Updates.cs`), the link registration (`UrlSchemeRegistration.cs`) and the startup entry
(`StartupRegistration.cs`).

---

## 3. How it records, and what was rejected

### 3.1 Chosen: DXGI desktop duplication, cut down to VRChat's window, into Windows' own H.264 encoder

- **Capture.** `IDXGIOutput1::DuplicateOutput` on the monitor VRChat's window is on. Windows hands
  over a copy of a frame the desktop compositor has already drawn, on the GPU, only when something
  changed. Nothing is drawn, and no window list is enumerated.
- **Which monitor.** The one whose rectangle holds the middle of VRChat's window, rather than the
  first output on the adapter. That closes the "may record the wrong monitor" gap this spec used to
  list under §10.
- **Which part of it.** VRChat's window. `FindWindowW` by class and title gives one named window;
  `GetClientRect` and `ClientToScreen` give where its picture is; the copy off the duplicated
  desktop is that box and nothing else. The box is clamped to the monitor's own picture before it
  is used, because the numbers come from Windows about another program's window and a box running
  past the end of a texture is a crash on somebody's PC rather than a wrong pixel.
- **Shrinking.** The window's box is copied into a texture with a mip chain, the graphics card
  generates the chain itself (`GenerateMips`), and the first level no wider than 1280 pixels is the
  one used. A 2560×1440 window records at 1280×720; a 1920×1080 one at 960×540. Odd sizes are
  trimmed to even because H.264 will not take odd ones.
- **Encoding and writing.** Media Foundation's sink writer, with
  `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS` set, so the graphics card's own encoder (Quick Sync,
  NVENC, AMF) is used when the machine has one and Microsoft's software encoder when it does not.
  BGRA goes in; H.264 in an `.mp4` comes out; Media Foundation inserts the colour conversion itself.
  15 frames a second, 1.5 Mbit/s.
- **The rolling window.** Two files, staggered by half the chosen length. Each runs for the full
  length and is then replaced by a fresh one; whichever has been running longer therefore always
  holds between half and all of the chosen length, ending now. **Save a clip** closes that one, moves
  it into the clips folder, and starts it again.

Why two files rather than one buffer: a single file that rotates gives a clip of anywhere between
zero and the full length, and zero is the case a moderator gets when they press Save just after a
rotation — which is the one moment they are most likely to press it. Two staggered files remove that
case with no container surgery and no concatenation. The cost is that each frame is encoded twice,
which at 960×540 and 15 frames a second is small enough to be the right trade (§4).

Saving twice inside a minute gives a shorter second clip, because the first save is what started
that file. That is stated on the documentation page rather than hidden.

#### 3.1.1 What a crop can and cannot keep out, honestly

A crop takes a rectangle out of a picture the desktop already drew. Whatever the desktop drew
*inside* that rectangle is in the clip. So:

- **Outside VRChat's window: never in the clip.** A second monitor, a Discord window beside the
  game, the taskbar, a browser on the other half of the screen — none of it, at any time.
- **Drawn on top of VRChat while VRChat is in front: in the clip.** A chat program's in-game
  overlay, Steam's overlay, a Windows notification, a window somebody pinned always-on-top, and
  Modbot's own panel over the game. These are a small and mostly deliberate set.
- **While VRChat is not the window in front: nothing new is copied at all.** The last picture of
  VRChat is written again, at the same rate, until VRChat is back.

That third rule is what makes the crop worth having rather than merely cheaper. VRChat's own
default is borderless fullscreen, so its window covers the whole monitor, and a plain crop would
therefore record the whole monitor any time the moderator alt-tabbed — which is constantly, and is
exactly when their private messages are on the screen. Holding the last frame instead costs a few
frozen seconds in a clip and removes the entire category.

The cost of holding is stated rather than hidden: a moderator who is in Discord the whole time gets
a clip that is a still picture. That is a bad clip, and it is not somebody else's messages.

**What a crop still cannot do that window capture could**: an overlay drawn over VRChat is in the
clip. §3.3 says why that was not worth the price this time.

### 3.2 Rejected: a bundled ffmpeg run as a process

`CompanionSourceGuardTests` bans `System.Diagnostics.Process` anywhere the client ships, and that ban
is one of the things separating this program from the thing it is shaped like. Widening it to run a
bundled encoder would have meant the client could start a program again — a far larger reversal than
the one being made, and reached for convenience rather than need. It would also have meant shipping
an LGPL/GPL binary in a Velopack installer and carrying its licensing.

**The Process ban is untouched by this change.**

### 3.3 Rejected, twice: Windows.Graphics.Capture

`Windows.Graphics.Capture` captures a window itself rather than the part of the screen it sits in,
so an overlay drawn over VRChat is *not* in the result. It is the only route that is correct under
that case, and it was looked at again on 2026-09-19 with the crop in hand. It is still not here,
for a different and better-understood reason than the first time.

**The target framework is the whole of it.** Reaching a WinRT API from C# needs a Windows-version
target framework — `net10.0-windows10.0.19041.0` or later. `Modbot.Companion.App` is a plain
`net10.0` project because the same project publishes the Linux client. The three ways out each
fail on something concrete:

- **Multi-target, or switch the framework on the runtime identifier.** The release workflow does
  build Windows and Linux as separate jobs, so a `RuntimeIdentifier`-conditional framework would
  produce the right thing in CI. It would also mean that a plain `dotnet build -c Release` — the
  check every agent runs before pushing, and the one a contributor runs on their own machine —
  builds the *Linux* flavour, with every line of capture code behind `#if WINDOWS` and therefore
  never compiled. Code that the ordinary build does not compile is code nobody finds out about
  until a release, and this is code that touches COM and a graphics device.
- **A separate Windows-only assembly.** A `net10.0` executable cannot reference a
  `net10.0-windows` one at all, so this is the previous option with an extra project. It also puts
  the recording outside `Modbot.Companion` and `Modbot.Companion.App`, which is precisely the two
  directories `CompanionSourceGuardTests` scans — the guard that makes "show me where this program
  records" have a one-word answer. Moving the capability outside the guard to get the capability is
  the wrong trade.
- **Raw COM: `RoGetActivationFactory`, `IGraphicsCaptureItemInterop`, `Direct3D11CaptureFramePool`,
  `IDirect3DDxgiInterfaceAccess`, hand-written vtables.** Several hundred lines where a wrong slot
  is memory corruption at run time rather than a compile error, and none of it can be run once
  before shipping, because there is no screen in CI.

Against that: the crop is about forty lines of ordinary Win32 on top of the duplication that was
already working, its rules are arithmetic that `ClipWindowRuleTests` checks without a screen, and
the one case it gets wrong — something drawn over VRChat — is a small, mostly deliberate set
(§3.1.1).

**It stays the better answer and it stays written down.** If the client ever needs a Windows-only
assembly for another reason, this moves into it.

### 3.4 Rejected: one encoder feeding a keyframe-aware buffer in memory

The tidiest design on paper: encode once, keep the encoded frames in memory in a ring bounded by
time and bytes, and on save hand them to a sink writer that re-muxes without re-encoding. One encode
instead of two, and nothing on disk at all until a clip is saved — the strongest privacy story of
any option here.

It needs the encoder driven as a Media Foundation transform by hand, keyframe positions tracked so
the ring can be trimmed to a decodable start, and a second pass-through writer for saving. That is
several hundred more lines of COM interop that cannot be run once before shipping. The two-file
design reaches the same product behaviour with a typed API and an obvious failure mode.

Worth revisiting alongside §3.3, since both are the same kind of work.

### 3.5 Rejected: a managed encoder

There is no usable pure-managed H.264 encoder for .NET. The alternatives are motion-JPEG in an AVI —
roughly ten times the size for worse pictures, and all of it on the CPU — or OpenH264, which is a
native library with its own binary-distribution licensing. Windows already has an encoder, usually on
the GPU.

### 3.6 Rejected: recording on Linux

Recording is Windows-only, and this section exists because "we did not try" and "we tried and it
cannot be done well" are different claims. The second one is the true one.

**Capturing is possible.** VRChat runs under Proton, so its window is X11 or XWayland, and there
are two real routes: `XComposite` plus `XGetImage` or the shared-memory extension for one window
on X11, and the `org.freedesktop.portal.ScreenCast` portal over D-Bus, feeding a PipeWire stream,
on Wayland. Both are work — two display servers, two code paths, a permission dialog on one of them
that has no equivalent anywhere else in this client — but neither is the blocker.

**Encoding is the blocker, and it has no way out.** Windows was easy because Windows *has* an
encoder: Media Foundation's sink writer is part of the operating system, is reached through a typed
managed API, and uses the graphics card's own encoder when there is one. Linux has no such thing
within reach:

- **Running ffmpeg as a process** is what everybody does, and this client may not.
  `CompanionSourceGuardTests` bans `System.Diagnostics.Process` across every file the client ships,
  with no exception — the one carve-out is that Velopack's library starts Modbot's own updater, and
  even there the client's own code never names `Process`. That ban is one of the handful of things
  separating this program from what it is shaped like (§3.2). It was not widened for Windows and it
  is not being widened for Linux.
- **Linking libavcodec, or libva, by hand** avoids the process ban and buys three new problems:
  shipping a GPL or LGPL native library in the Linux package and carrying its licensing, several
  hundred lines of hand-written interop against a C API with no compile-time safety, and a
  dependency on whatever those libraries happen to be on the machine.
- **A managed encoder** does not exist (§3.5), and that answer is not platform-specific.

**So: whole screen instead?** No. The encoder problem is the same whether one window or one screen
is being encoded, so "whole screen if you cannot do window-only" does not help — it was never the
capture half that was blocking.

**The decision.** Linux keeps saying *"Keeping the last few minutes only works on Windows."* A
half-working recorder that produced corrupt files, or one that silently did nothing, would be worse
than one honest sentence, and everything else on the Linux client works exactly as it does on
Windows. `RecordingIsWindowsOnlyAndSaysSoRatherThanFailingQuietly` keeps that sentence in place and
keeps `ffmpeg` and `libavcodec` out of the client.

**What it would take, if somebody wants it later**, in order of what has to be decided rather than
written:

1. A decision about the encoder that is not "start a process". Realistically: a small vendored
   native library with its own licence review and its own entry in `THIRD-PARTY-NOTICES.md`, plus
   interop that nothing in CI can run.
2. An X11 path, XComposite for a single window, which covers Proton on X11 and XWayland.
3. A Wayland path through the screencast portal, including what the client does about a permission
   dialog the first time — a thing no other part of this client has ever needed.
4. A machine with a headset, VRChat under Proton, and somebody willing to watch the frame rate.

Item 1 is the one that decides it, and nothing about it has changed since this spec was written.

---

## 4. What it costs while a VR game is running

This is the part the request was most specific about, so it is the part with the most conservative
numbers. **None of these have been measured on a machine running VRChat**; they are estimates from
the shape of the work, and measuring them is the first thing to do when somebody has a headset and a
build.

| Work | Estimate |
|---|---|
| Two H.264 encodes at 960×540, 15 fps, 1.5 Mbit/s, on a GPU encoder | ~1–2% of the encoder block; no effect on the 3D queue |
| The same two on Microsoft's software encoder (no GPU encoder present) | ~2–5% of one core |
| Copying the frame off the GPU and into memory, 15 times a second | ~31 MB/s of copying, ~1–2% of one core |
| Mip generation on the GPU | a fraction of a millisecond per frame |
| Writing to disk | ~380 KB/s across both files |
| Memory | one frame buffer, about 2 MB |
| Disk while recording | two files, at most about 112 MB together at five minutes |

Four things keep this low, and each is a deliberate choice rather than a default:

1. **15 frames a second, not 60.** Enough to see what somebody did; a quarter of the work.
2. **At most 1280 pixels wide.** The shrinking happens on the GPU before anything is read back, so
   the expensive part — copying pixels into main memory — is done at the small size.
3. **The graphics card's own encoder when there is one.** That is the difference between a percent
   and several.
4. **Its own thread, below normal priority.** Nothing here can hold up the loop that reads VRChat's
   log and reports presence, which is the job that cannot be filled in later.

The honest risk is the hardware encoder session limit. A machine already running OBS and Discord
screen-share may have no encoder session left, in which case Media Foundation falls back to software
and the cost moves to the CPU column above. It does not fail.

---

## 5. What is recorded, and what is not

**Recorded:** VRChat's own window, at most 1280 pixels wide, 15 frames a second, while VRChat is
running, while the switch is on, and while VRChat is the window in front. Anything drawn on top of
VRChat while that is true is inside its window and is therefore in the clip; §3.1.1 is the honest
account of that, and the documentation page says the same thing in a moderator's words.

**Not recorded, and each for a reason:**

- **Nothing outside VRChat's window.** Not another monitor, not a window beside the game, not the
  taskbar. And nothing at all while the moderator is working in another program: the last picture
  of VRChat is written again instead, so a clip cannot pick up whatever they alt-tabbed to. This is
  the thing that changed on 2026-09-19, and the reason it matters is that VRChat's own default is
  borderless fullscreen, which means "the window" and "the monitor" would otherwise be the same
  rectangle at exactly the wrong moment.
- **No sound, at all.** What a moderator asked for was to be able to show what happened; a recording
  of everybody's voice in an instance is a different and much larger thing to take off a PC.
  `TheOnlyFileThatCanListenIsPhraseListeningCs` passes over every file the client ships, this one
  included, so nothing in a clip is ever a recording of anybody's voice.

  This sentence used to read *"the ban on every microphone, line-in and loopback API is untouched
  and still total"*, and that stopped being true later the same day: the listening design
  (2026-09-19) narrowed it by one file so a moderator in a headset can say "Modbot, clip that". What
  survives, and is what this bullet is actually about, is that **a clip records no sound** and that
  **nothing anywhere in the client can keep or send a recording of a voice** — the one file allowed
  to open a microphone cannot write a file and cannot reach the network, and a guard fails the build
  if that changes. The recorder is untouched by any of it.
- **No keyboard**, no clipboard, no list of other programs and no list of their windows. Finding
  VRChat's window is one named ask for one named window — `FindWindowW` with VRChat's class and
  title — and never a walk over what else is open.
  `TheOnlyFileThatAsksWindowsAboutVRChatsWindowIsScreenRecordingCs` holds that to one file, the same
  shape as the recording itself.
- **Nothing read out of any folder.** VRChat's screenshot folder, Pictures, Documents and the Desktop
  are still banned everywhere in the client, including inside the file that records.
- **Nothing while VRChat is not running.** The client knows VRChat is running because lines are
  arriving in VRChat's own log (`LogHealth`) — never by looking for a running program, which would
  mean taking the one capability that is still banned outright in order to learn something already
  known. VRChat closing stops the recording and deletes the two rolling files.

A log the client has stopped understanding still counts as VRChat running. Lines are arriving, so the
moderator is in a world, and somebody whose client needs updating should not also quietly lose the
recording they switched on.

### 5.1 What a window that will not stay still does

Recording a window rather than a monitor brings four cases a monitor never had. None of them may
end as a corrupt file, because a clip nobody can open is worse than no clip.

| What happens | What the recorder does |
|---|---|
| **VRChat is not running yet.** The log has started moving but no window exists. | Nothing is recorded and no encoder is opened. The settings screen and the overlay both say *Waiting for VRChat's window*, and **Save a clip** cannot be pressed. It is its own state rather than a failure, because the recorder is the thing that asks Windows for the window, and taking it down for not having found one would rebuild it once a second for as long as VRChat took to draw. |
| **The window closes part way through.** | The last picture is written again. The recorder is not stopped by this; VRChat's log going quiet is what stops it, which then deletes the two rolling files. One rule about the recorder's life, in the place that already had it. |
| **It is minimised, or dragged onto another screen.** | The last picture again. A minimised window has no picture, and a window on another monitor is not in the duplication being read. |
| **Its size changes** — alt-tab, a resolution change, a window dragged bigger. | The clip keeps its size and the picture is shrunk to fit inside it, with whatever is left over black. The encoder is told a frame size once and a video file cannot change size part way through, so the alternative was closing both files and opening two new ones, which throws away the minutes a moderator is about to want. A resize costs a smaller picture; it never costs the file. |

`ClipWindowRuleTests` checks all of this without a screen, which is the reason the decisions live in
`ClipWindowRule` rather than inside the capture loop.

**One thing that is not handled, and cannot be from here.** Windows reports a window's size in the
DPI context of the asking program. Modbot's window is per-monitor DPI aware, as Avalonia's Windows
backend sets up, so the numbers and the duplicated desktop are both in real pixels — but if that
ever stopped being true on some machine, the box would be the wrong size. It would be a wrong crop
and never a crash, because the box is clamped to the monitor before it is used (§3.1).

---

## 6. When anything leaves the machine: never

**No clip, no frame, and no fact that a clip exists is sent anywhere.** Not to a paired server, not
to Modbot Cloud. The client's eight declared outbound senders are unchanged and no ninth was added;
`OnlyTheEightDeclaredPlacesMakeOutboundRequests` still passes.

This was a decision, not an oversight, and the request did ask for clips to be "sent up to server".
Three things decided it:

1. **The companion's credential cannot do it.** A paired device's token is ingest-scoped: it can
   submit presence facts and nothing else. Evidence uploads (`/api/evidence/uploads`) require a
   signed-in moderator holding `ModbotPermissions.UploadEvidence`. There is no endpoint a companion
   could call.
2. **Giving it one would be a real escalation.** A device token that can push video into a group's
   evidence store makes a stolen token far more dangerous than one that can only say who joined an
   instance. That is a change to the client's security model, not a feature of a recorder.
3. **A server may have no evidence store configured at all**, in which case there is nowhere for it
   to go.

So a clip is a file on the moderator's PC. Attaching it to a case is what it always was: a person, in
Modbot's web interface, in a browser, choosing a file. `video/mp4` is already an accepted evidence
type, so a saved clip attaches with no server change at all.

**Tying a clip to a ban or a kick is not built, and could not be from the client as it stands.** No
ban or kick reaches the companion. The live events a paired server pushes are presence only — joined,
left, already here, flagged join, watch stopped — and there is no ban control anywhere in the client
or its overlay. Building the tie would need a new live event kind pushed to companions *and* the
upload credential of point 2. What is built instead: the moderator presses **Save a clip**, the file
is named after the moment and the instance so the right one is findable afterwards, and the save is
written into the client's own journal so the Events page shows it happened. The seam for an automatic
save is one call.

---

## 7. The folder, and what stops it growing forever

**Default:** the machine's own Videos folder, asked of the operating system rather than spelled out,
plus `Modbot Clips`. Asking Windows means a profile on another drive, or a Windows in another
language, lands in the right place. A machine with no Videos folder — ordinary on a server-style
install and on Linux — falls back to Modbot's own folder under the user profile.

**Configurable** on the settings screen. Blank means the usual place, and the usual place is stored
by storing nothing, so a path is never pinned into `settings.json` where it would stop following the
machine.

**A folder that cannot be written to is a sentence on the settings screen, never a crash.** The check
creates the folder and writes and deletes one empty file, because the answer that matters is whether
a file lands — a permissions check that says yes on a full or read-only drive is the wrong answer
arriving too late. A read-only drive, a removed USB stick or a typo all come back the same way, and
everything else in the client carries on.

**What limits growth**, in three separate places:

1. **The rolling files are not clips.** Exactly two exist, each replaced as it fills, both deleted
   when recording stops, when VRChat closes, and when the client quits. They live under
   `%APPDATA%\Modbot\clips` and never in the moderator's own folder.
2. **Saved clips have a limit on the folder**, `keepGigabytes`, five by default. Before a clip is
   saved, the oldest clips are deleted until the folder plus the new clip fits. Only files Modbot
   wrote — `.mp4` files in that folder — are ever counted or deleted; somebody else's videos sitting
   in the same folder are not Modbot's to touch.
3. **The newest clip is never deleted to make room.** A limit smaller than one clip would otherwise
   delete the thing that was just saved, which is the one outcome somebody who pressed Save must not
   get.

`keepGigabytes` has no control on the settings screen — the screen has the switch, the minutes and
the folder, and a fourth number would be a knob most people never touch. It is in `settings.json` and
on the documentation page for somebody with a small disk.

---

## 8. Off by default

`clips.on` is `false`, a missing `clips` object means off, and with it off **no recorder object, no
Direct3D device, no encoder and no thread is built at all** — the same shape as the three overlay
switches, and for the same reason: off is a person saying they do not want this, so the honest answer
is to do none of the work.

A client updated into a version that can record therefore does not start recording. A recorder that
started on first run would be precisely the surprise the old promise existed to prevent, and
`TheRecorderIsOnlyEverBuiltInOnePlaceAndIsOffUntilItIsSwitchedOn` fails the build if the default ever
moves.

---

## 9. Every place the old promise appeared, and what it says now

| Where | Kind | What it said | What it says now |
|---|---|---|---|
| `src/Modbot.Companion.App/Program.cs` §"It never captures the screen" | class doc | "It never captures the screen. Not the desktop, not a window…" | Rewritten: what can be recorded, that it is off unless switched on, what is recorded and what is not, where it is written, and that nothing leaves the machine |
| `src/Modbot.Companion.App/Program.cs` "What it writes to your disk" | class doc | listed three things plus the voice | also names the two rolling files under `clips` and that they are deleted |
| `src/Modbot.Companion.App/Program.cs` "What leaves the machine" | class doc | "Never chat, screenshots, keystrokes…" | "never a recorded clip or any other picture of your screen" — still never |
| `src/Modbot.Companion.App/MainWindow.cs` class doc | class doc | "There is no screen capture… and will not get" | Rewritten: what the Clips card does, that no clip is uploaded, that attaching is still a file chosen in a browser |
| `src/Modbot.Companion.App/MainWindow.cs` "What it does not read" card | **UI text** | "It does not capture the screen, read your screenshots folder…" | Says it records VRChat's window and whatever is drawn over it, only with Clips on, only while VRChat runs, never the sound, and that nothing leaves the PC |
| `tests/…/Guards/CompanionSourceGuardTests.cs` `NothingTheClientShipsCanCaptureAScreen` | test | total ban | Replaced by `TheOnlyFileThatCanRecordIsScreenRecordingCs`: exactly one file, and the ban list grew to cover the routes that file uses |
| `tests/…/Guards/CompanionSourceGuardTests.cs` `ScreenshotFolders` | test | banned `MyVideos` with Pictures, Documents, Desktop | `MyVideos` moved to its own rule, `TheOnlyFileThatNamesYourVideosFolderIsClipsFolderCs`; the rest still banned everywhere |
| `tests/…/Guards/CompanionSourceGuardTests.cs` `NothingTheClientShipsCanRecordSound` | test | total ban | Unchanged by *this* spec. Narrowed later the same day by the listening design (2026-09-19) to `TheOnlyFileThatCanListenIsPhraseListeningCs`: one file may open a microphone, nothing may keep or send what it hears, and a clip still records no sound |
| `docs/content/docs/companion/install.mdx` | docs | "It does not read chat, your friends list, the screen…" | Names Clips, off by default, and links the new page |
| `docs/content/docs/companion/clips.mdx` | docs | — | New page: the whole of it, for a suspicious reader |
| `docs/content/docs/companion/settings.mdx` | docs | — | The `clips` rows added at the end of the settings-file table |
| `docs/content/docs/privacy.mdx` | docs | companion section | Says what is recorded, that it is off by default and that no clip leaves the PC |
| `docs/content/docs/security.mdx` | docs | companion section | Same, framed as what a stolen device token cannot reach |
| `PRIVACY_POLICY.md` | policy | had no screen claim at all | Gains one that is true |
| `.agent/specs/2026-09-10-m3-client-overlay-design.md` §3.1.1, §10 | spec | "forbidden, permanently" | Marked as narrowed by this spec, with the date and what survives |
| `.agent/specs/2026-09-13-evidence-storage-design.md` §19 | spec | "No automatic capture of anything" | Same, and the half that is still true — nothing uploaded — called out |
| `src/Modbot.Landing/` | marketing | made the "reads only the log" claim, never the capture one | Left alone: it never made the claim, and it is still true that the companion reads only the log |

The landing site is the one row where the answer is "no change", and it is in the table so that
somebody checking can see it was looked at rather than missed.

### 9.1 And what the window change moved again, the same day

| Where | What it says now |
|---|---|
| `src/Modbot.Companion.App/ScreenRecording.cs` class doc | Records VRChat's window; names what can still end up in one — things drawn over VRChat — and says the last picture is held while the moderator is in another program |
| `src/Modbot.Companion.App/Program.cs` §"It can record VRChat's window" | The same, in the program's own front-door disclosure, including that no list of programs or of their windows is ever asked for |
| `src/Modbot.Companion.App/MainWindow.cs` class doc and the "What it does not read" card | The same again, in a moderator's words |
| `tests/…/Guards/CompanionSourceGuardTests.cs` | New: `TheOnlyFileThatAsksWindowsAboutVRChatsWindowIsScreenRecordingCs`, and `RecordingIsWindowsOnlyAndSaysSoRatherThanFailingQuietly`, which also keeps `ffmpeg` and `libavcodec` out of the client |
| `docs/content/docs/companion/clips.mdx` | Rewritten: what is recorded on each platform, what can still end up in a clip, and how to save one from inside VR |
| `docs/content/docs/companion/install.mdx`, `privacy.mdx`, `security.mdx`, `PRIVACY_POLICY.md` | "One of your monitors" becomes "VRChat's window", everywhere it appeared |
| `docs/content/docs/not-built-yet.mdx` | Three rows removed — saving from inside VR, choosing the monitor, and recording the window — because all three are built |

---

## 10. What is not built

- **Window capture proper** (§3.3): a clip is a crop of the screen where VRChat's window is, so
  something drawn over the game is in it. §3.1.1 is honest about that and so is the documentation
  page.
- **Recording on Linux** (§3.6). Decided rather than pending: the encoder has no route that does
  not break the ban on starting a process or ship a native library the client has no business
  carrying.
- **The in-memory buffer** (§3.4).
- **Any tie to a ban or kick** (§6).
- **A second control for the notification overlay.** Save a clip is on the main panel and on the
  window over VRChat; the pop-up overlay takes no input at all and was not given any.
- **Any measurement at all.** §4 is estimates. Nothing in this feature has been run — the window
  crop included, which is new code against a graphics API and has never met a real machine.

---

## 11. Saving a clip from inside VR

§10 used to say this was the biggest gap. The button lived on the settings page, and a moderator in
a headset cannot reach a settings page — which meant the feature worked for exactly the people who
were least likely to need it.

### 11.1 Where the control is, and why there

**On the overlay panel, under the tabs, above whichever screen is showing.** It is drawn on the
headset panel and on the window over VRChat, because both are the same controls built by
`OverlayView` and both hand taps to the same drive loop.

Under the tabs rather than on a page of its own, because a moderator reaches for this in the middle
of something happening and that is the worst possible moment to have to navigate. From the roster,
from the events list, from a person's card, it is one press. `SaveClipControlTests` checks that on
each screen, checks it is big enough for a hand in a headset, and checks it is near the top of the
panel rather than under a list that can grow past the edge.

**It is also drawn when the panel is otherwise blank.** Outside a group instance the panel shows
nothing at all, deliberately — a card reading "not in a group instance" would be in a moderator's
face for most of their VRChat time. Save a clip is the one exception, because the recorder runs
wherever VRChat does and a moment worth keeping can happen in a public instance as easily as a
group one. It is only ever there because somebody switched Clips on themselves.

### 11.2 No second keyboard shortcut

The client asks Windows for exactly one keyboard combination, by name, and says so in its own
documentation as part of the argument that it does not read the keyboard. A second one would be a
small reversal of a real promise, bought for a control that already exists on a panel the moderator
is looking at. Not worth it, and not built.

### 11.3 It never looks like it worked when it did not

This is most of the work. Inside a headset there is no settings screen, no file explorer and no
notification; a control that appeared to work and quietly did nothing would leave a moderator
believing they had kept a moment, and finding out an hour later is the whole failure.

So `ClipButtonRule` turns the Clips card into one caption and one yes-or-no:

| When | What it says | Can it be pressed |
|---|---|---|
| Clips is off | nothing is drawn at all | — |
| Keeping the last few minutes | **Save a clip** | yes |
| VRChat is not running | Waiting for VRChat | no |
| VRChat is running, its window not found yet | Waiting for VRChat's window | no |
| The folder cannot be written to | The folder cannot be used | no |
| This machine cannot record | Not available on this machine | no |
| The recorder failed | Recording stopped | no |
| A clip just landed | **Clip saved**, for eight seconds | yes |
| The save produced no file | **Clip not saved**, for eight seconds | yes |

Two things about that table are load-bearing. The first is that a state that cannot save carries
**no tap target at all** — the control is drawn, but nothing in it can be hit, so a press lands on
nothing rather than on a control that has to decide to ignore it. The drive loop refuses it a
second time anyway (`OverlayDriverClipTests`), because the panel redraws a few times a second and a
tap can arrive from a frame drawn just before VRChat closed.

The second is the confirmation, and that it can be a **no**. The recorder writes the file on its own
thread on its next frame, so the answer arrives a moment after the press; the companion watches for
a new file name, for a complaint the recorder had not made before, and — if neither arrives within
ten seconds — calls it a failure. Ten seconds is generous on purpose: saying "Clip not saved" about
one that did land is the worse of the two mistakes.

The words are the same words the Clips card on the settings screen uses, so one thing has one name
in both places.

### 11.4 The overlay still cannot be told what to do

The tap goes to `OverlayDriver`, which raises `SaveClipAsked` and does nothing else. It owns no
recorder, no folder and no limit, and there is no path from a server to that event. The companion
— the half that owns all three — does the saving, through the same `SaveClip` that the settings
button calls, so there is one rule about making room, one naming scheme and one line in the
client's own journal however it was asked for.

That keeps the overlay what it has always been: it shows things, it reports taps, and the one thing
a tap can now cause is a file being written on the moderator's own disk.
