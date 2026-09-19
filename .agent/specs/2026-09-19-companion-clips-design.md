# Clips: keeping the last few minutes on a moderator's PC

**Status:** built, 2026-09-19.
**Reverses:** M3 client and overlay design §3.1.1 ("Screen capture — forbidden, permanently") and
§10, and evidence storage design §19 ("No automatic capture of anything").

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
keeps the last few minutes of the picture on one monitor, on that person's own disk.**

The narrowing is shaped so it stays answerable. "Show me where this program records" has a one-word
answer — `ScreenRecording.cs` — and `CompanionSourceGuardTests` fails the build if any second file
the client ships learns to. That is the same shape as the existing carve-outs for the installer
(`Updates.cs`), the link registration (`UrlSchemeRegistration.cs`) and the startup entry
(`StartupRegistration.cs`).

---

## 3. How it records, and what was rejected

### 3.1 Chosen: DXGI desktop duplication, into Windows' own H.264 encoder

- **Capture.** `IDXGIOutput1::DuplicateOutput` on the monitor the client's graphics adapter drives.
  Windows hands over a copy of a frame the desktop compositor has already drawn, on the GPU, only
  when something changed. Nothing is drawn, no other program's window is read or asked about, and no
  window list is enumerated.
- **Shrinking.** The frame is copied into a texture with a mip chain, the graphics card generates
  the chain itself (`GenerateMips`), and the first level no wider than 1280 pixels is the one used.
  A 2560×1440 monitor records at 1280×720; a 1920×1080 one at 960×540. Odd sizes are trimmed to even
  because H.264 will not take odd ones.
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

### 3.2 Rejected: a bundled ffmpeg run as a process

`CompanionSourceGuardTests` bans `System.Diagnostics.Process` anywhere the client ships, and that ban
is one of the things separating this program from the thing it is shaped like. Widening it to run a
bundled encoder would have meant the client could start a program again — a far larger reversal than
the one being made, and reached for convenience rather than need. It would also have meant shipping
an LGPL/GPL binary in a Velopack installer and carrying its licensing.

**The Process ban is untouched by this change.**

### 3.3 Rejected: Windows.Graphics.Capture, capturing VRChat's window only

This is the better answer on privacy, and it is not here yet. Window capture would record VRChat and
nothing else on the screen, rather than the whole monitor.

Two things stopped it. First, `Windows.Graphics.Capture` is a WinRT API and reaching it from C#
needs a Windows-version target framework (`net10.0-windows10.0.x`). `Modbot.Companion.App` is a plain
`net10.0` project on purpose, because the same project publishes the Linux client. Second, reaching
it through raw COM instead — activation factories, `IGraphicsCaptureItemInterop`,
`IDirect3DDxgiInterfaceAccess`, hand-written vtables — is several hundred lines of interop where a
wrong slot is memory corruption at run time rather than a compile error, and none of it can be tested
without a screen.

Desktop duplication is roughly eighty lines against a typed, managed API that is already in the
repository for the SteamVR overlay.

**This is the next thing to do to this feature**, and the reason is worth stating: a moderator's
monitor has their private messages on it. What bounds the current design instead is that the
recording only runs while VRChat is running, that it is off until switched on, and that nothing
recorded ever leaves the PC.

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

**Recorded:** the picture on one monitor, at most 1280 pixels wide, 15 frames a second, while VRChat
is running and while the switch is on.

**Not recorded, and each for a reason:**

- **No sound, at all.** The ban on every microphone, line-in and loopback API is untouched and still
  total. What a moderator asked for was to be able to show what happened; a recording of everybody's
  voice in an instance is a different and much larger thing to take off a PC, and keeping that ban
  total is most of what keeps this one narrow. `NothingTheClientShipsCanRecordSound` still passes
  over every file the client ships, this one included.
- **No keyboard**, no clipboard, no list of other programs.
- **Nothing read out of any folder.** VRChat's screenshot folder, Pictures, Documents and the Desktop
  are still banned everywhere in the client, including inside the file that records.
- **Nothing while VRChat is not running.** The client knows VRChat is running because lines are
  arriving in VRChat's own log (`LogHealth`) — never by looking for a running program, which would
  mean taking the one capability that is still banned outright in order to learn something already
  known. VRChat closing stops the recording and deletes the two rolling files.

A log the client has stopped understanding still counts as VRChat running. Lines are arriving, so the
moderator is in a world, and somebody whose client needs updating should not also quietly lose the
recording they switched on.

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
| `src/Modbot.Companion.App/MainWindow.cs` "What it does not read" card | **UI text** | "It does not capture the screen, read your screenshots folder…" | Says it records one monitor only with Clips on, only while VRChat runs, never the sound, and that nothing leaves the PC |
| `tests/…/Guards/CompanionSourceGuardTests.cs` `NothingTheClientShipsCanCaptureAScreen` | test | total ban | Replaced by `TheOnlyFileThatCanRecordIsScreenRecordingCs`: exactly one file, and the ban list grew to cover the routes that file uses |
| `tests/…/Guards/CompanionSourceGuardTests.cs` `ScreenshotFolders` | test | banned `MyVideos` with Pictures, Documents, Desktop | `MyVideos` moved to its own rule, `TheOnlyFileThatNamesYourVideosFolderIsClipsFolderCs`; the rest still banned everywhere |
| `tests/…/Guards/CompanionSourceGuardTests.cs` `NothingTheClientShipsCanRecordSound` | test | total ban | **unchanged**, with a comment saying why it did not move when the other did |
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

---

## 10. What is not built

- **Saving a clip from inside VR.** The button is on the Settings page. A moderator in a headset
  cannot reach it. The two ways in are a control on the overlay panel or a second global keyboard
  combination; the second would reverse a smaller promise of its own ("the one keyboard combination
  the client asks Windows for"), and both deserve their own decision rather than riding along here.
  **This is the biggest gap and the feature is awkward until it is closed.**
- **Window-only capture** (§3.3) and **the in-memory buffer** (§3.4).
- **Any tie to a ban or kick** (§6).
- **Choosing which monitor.** The first output on the client's graphics adapter. A machine with
  several monitors may record the wrong one, and there is no way to say which.
- **Any measurement at all.** §4 is estimates. Nothing in this feature has been run.
