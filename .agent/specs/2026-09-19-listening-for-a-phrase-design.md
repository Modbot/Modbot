# Listening for a phrase, and a notification sound somebody can live with

**Status:** built, 2026-09-19.
**Narrows:** M3 client and overlay design §10 ("sound — never recorded") and the clips design
(2026-09-19) §5, which said the ban on every microphone, line-in and loopback API was "untouched
and still total".
**Also covers:** the notification sound, rewritten the same day
(2026-09-18 notification sound design §2.1).

---

## 1. What was asked for, and what this is

Two things, from the same person, in the same sentence:

> *"Add a non-mic blocking speech STT engine or something else that's simple that can detect
> 'Modbot, clip that' and announce back with voice or notification sound that the clip was made.
> Also work on the notification sound it's sooo annoying and tone generated"*

The first is a real gap. The clips design closed one gap on 2026-09-19 by putting **Save a clip** on
the overlay panel (§11), which is reachable in a headset — but it still has to be found and pointed
at, in the middle of the thing that made somebody want it. Saying it out loud is one step.

The second is a complaint, and it is correct. §8 is the sound.

---

## 2. The engine, and what was rejected

### 2.1 Chosen: keyword spotting, from the library the client already has

`org.k2fsa.sherpa.onnx` 1.13.8 is already referenced for the voice. Beside the `OfflineTts` the
voice uses, the same package exposes `KeywordSpotter` — a streaming transducer whose decoding is
constrained to a list of phrases it is handed. It answers one question, *was one of those just
said*, and it cannot produce a transcript of anything else.

**No new dependency**, no new native library, and the pinned-download machinery already exists
(`Voice/VoiceModel.cs`, `Voice/VoiceDownload.cs`), which `Listening/PhraseModel.cs` and
`Listening/PhraseDownload.cs` follow exactly.

The model is pinned the same way the voice is — one address, one size, one SHA-256, verified before
anything is unpacked:

```
https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01.tar.bz2
17,626,723 bytes
SHA-256 f170013b4716e41b62b9bfd809687c207cef798ef9bc6534d524e17af9b6561a
```

**These numbers are real.** The file was fetched and hashed while this was written; they are not
placeholders. To check them:

```
curl -sL -o kws.tar.bz2 <the address above>
stat -c %s kws.tar.bz2      # 17626723
sha256sum kws.tar.bz2       # f170013b…
```

It is 17 MB against the voice's 305 MB, and 3.3 million parameters against Kokoro's 82 million.
The full-size encoder, decoder and joiner are used rather than the 8-bit copies in the same archive:
the whole of the model is 13 MB either way, and the voice engine design (2026-09-18) already
measured that this engine has no fast 8-bit path and emulates one — which would cost processor time
on a machine running a VR game, which is the number that matters here.

Only five files are written out of the archive: the three model parts, the vocabulary, and the
phrase list. The spare 8-bit copies, the sample recordings, the readme and the vocabulary builder —
about 7 MB — are never written at all. A folder where every file has a reason to be there is one
somebody can check.

### 2.2 Rejected: the full streaming recogniser from the same library

`OnlineRecognizer`, in the same package, transcribes everything. It would have made the phrase
configurable (§5) and would have needed no phrase-encoding work at all.

**It is the wrong thing to put on a volunteer's personal machine.** This client's entire argument
is that what it does is bounded and checkable, and "it has a speech recogniser running, but trust us
about what it does with the text" is not a bounded claim. Keyword spotting is: the model is handed
four phrases and there is no code path in the client that could ask it anything else. It is also
cheaper — a 3.3M model rather than a 30–100M one — and more accurate at the one job, because the
decoding is constrained to the phrases rather than having to beat every other English sentence.

The difference is the difference between a program that can hear one thing and a program that can
hear everything and promises not to. Only one of those survives a suspicious reader.

### 2.3 Rejected: Windows' own speech recognition

`System.Speech` and `Windows.Media.SpeechRecognition` both need a Windows-version target
framework — `net10.0-windows10.0.19041.0` or later — and `Modbot.Companion.App` is a plain
`net10.0` project because the same project publishes the Linux client.

**The clips design already rejected exactly this route, for exactly this reason, on the same day**
(clips §3.3), and nothing about it has changed. The three ways out each fail on something concrete:
multi-targeting means a plain `dotnet build -c Release` compiles the Linux flavour and never
compiles the listening code at all; a separate Windows-only assembly cannot be referenced by a
`net10.0` executable and would move the capability outside the two directories
`CompanionSourceGuardTests` scans, which is the guard that makes "show me where this program
listens" have a one-word answer; and raw COM is hundreds of lines that nothing in CI can run.

Against that, the chosen engine is a package that is already in the project and already works on
both platforms.

### 2.4 Rejected: a voice activity detector in front of the matcher

The same library also has `VoiceActivityDetector` and `CircularBuffer`, and running the matcher only
while somebody is speaking would cut the processor cost.

It needs a second pinned model (Silero VAD) with its own download, its own hash and its own entry in
the notices, to save a fraction of a percent of one core on a model this small. And a circular
buffer of sound — held so that the start of an utterance is not lost — is the one thing in this
design that would be a *recording*, however short. Not worth either.

### 2.5 Rejected: a keyboard shortcut instead

Already rejected by the clips design (§11.2): the client asks Windows for exactly one keyboard
combination, by name, and says so as part of the argument that it does not read the keyboard. This
did not change it.

---

## 3. Not blocking the microphone

This is the request's first requirement and the one that ruins the feature if it is wrong. VRChat
must keep working normally.

### 3.1 Shared mode, and how that is known

On Windows, `NAudio.Wasapi` 3.1.0's `WasapiRecorderBuilder` is used with `.WithSharedMode()`.

WASAPI has exactly two modes. **Shared** is what the Windows audio engine mixes: every program that
asks for a device in shared mode is handed the same sound, and none of them excludes any other. It
is what VRChat, Discord, OBS and every voice chat program ask for. **Exclusive** takes the device
and locks everybody else out, and is used by audio workstations chasing latency.

So "it does not block the microphone" is not a hope about behaviour; it is which of two constants is
passed. The one line that decides it is `.WithSharedMode()` in `PhraseListening.cs`, and:

- `TheOnlyFileThatCanListenIsPhraseListeningCs` asserts that line is present, and asserts
  `WithExclusiveMode` and `AudioClientShareMode.Exclusive` appear nowhere in that file.
- The same test asserts `Loopback` appears nowhere in it, so this listens to a microphone and never
  to what the PC is playing.

The client's other use of WASAPI, `WindowsVoiceOutput`, has used `.WithSharedMode()` for playback
since the voice was built, for the same reason.

### 3.2 What happens when somebody else took it exclusively first

`Build()` fails — WASAPI returns `AUDCLNT_E_DEVICE_IN_USE` — and the listener catches it, writes one
sentence into `LastProblem`, logs it, and returns false. The Listening card says the microphone could
not be opened and that another program may have taken it. The recorder, the log reader and the
reporting loop are all untouched.

Because `ListeningRule.ShouldListen` is true for `NoMicrophone` as well as `Listening`, the listener
stays up and is not rebuilt once a second while that lasts; when the other program lets go, the next
start succeeds.

### 3.3 The default device, and no list

`.WithDefaultDeviceStreamRouting()` asks Windows to route whichever device is the default
microphone, rather than asking Windows which microphones exist. That keeps a property worth keeping:
**the client still never enumerates capture endpoints**, the same way it never enumerates windows or
processes. `TheOnlyEndpointsAskedForAreOutputs` did not have to move, and says so in its own
comment.

The cost is that there is no microphone picker. A moderator changes their microphone in Windows and
this follows it. Listed in §10 as not built.

### 3.4 Linux

**Windows only**, and this one is easy: saying "Modbot, clip that" saves a clip, and recording is
Windows only (clips §3.6), so there are no clips on Linux for a phrase to save. OpenAL's capture API
would have worked and is shared, and if recording ever comes to Linux this follows it. The Linux
client says *"Listening for a phrase only works on Windows."*

### 3.5 What it costs

Not measured on a machine running VRChat, like everything else in the clips design (§4). Estimates
from the shape of the work:

| Work | Estimate |
|---|---|
| The matcher, one thread, 16 kHz, 3.3M parameters | ~1–2% of one core |
| Folding to mono and resampling 48 kHz → 16 kHz | a fraction of a percent |
| Memory | the model, about 13 MB, plus a few seconds of samples |
| Shared-mode capture | what any voice chat program costs |

Its own thread, below normal priority, doing nothing else — the same rule the recorder follows, for
the same reason: nothing here may hold up the loop that reads VRChat's log.

---

## 4. The trust question

Listening to a microphone is a bigger step than anything this client has done. The rules are
therefore stricter than the recorder's, not looser.

### 4.1 Off by default, and off means nothing exists

`listenForPhrase.on` is `false`, a missing object means off, and with it off **no listener object,
no phrase matcher, no thread and nothing at all asked of Windows' audio system**. A client updated
into a version that can listen does not open a microphone.
`ListeningIsOnlyEverBuiltInOnePlaceAndIsOffUntilItIsSwitchedOn` fails the build if the default ever
moves, and asserts that `new PhraseListening(` appears in exactly one file.

### 4.2 Nothing is recorded, kept or sent

Sound arrives in fifths of a second, is folded to mono, resampled, handed to the matcher and
overwritten. At most five seconds of samples sit in memory, and only because the matcher's thread
takes them in batches.

`NothingTheClientShipsCanKeepOrSendWhatAMicrophoneHeard` asserts that the one file allowed to open a
microphone contains none of `File.`, `FileStream`, `StreamWriter`, `Directory.`, `HttpClient`,
`ClientWebSocket` or `WaveFileWriter`. It cannot write a file and it cannot reach the network, and
everything else in the client is barred from a microphone entirely, so that is the whole of the
surface.

This is said in the class documentation, on the documentation page, in the privacy policy and in the
security page.

### 4.3 Only while VRChat is running

The same rule the recorder uses, from the same source: `LogHealth`, because lines are either
arriving in VRChat's own log or they are not. Never the process list, which is still banned
outright.

This is the rule that makes the difference between "a microphone that is open while you are playing
VRChat" and "a microphone that is open whenever this program is in your tray". VRChat closing closes
it within a second; quitting the client closes it.

### 4.4 It says it is listening, on every page

`CompanionAppState.Warnings` yields an `Info` warning while `Listening.IsListening`, so the banner
is at the top of every page of the window for exactly as long as the microphone is open, and gone by
itself when it closes. The Listening card's own caption reads **Listening**.

Nothing else in this client needs that. This does: a program listening quietly is the thing a
moderator is right to be afraid of, so it is never quiet about it.

### 4.5 Every place a promise was edited

| Where | Kind | What it said | What it says now |
|---|---|---|---|
| `tests/…/Guards/CompanionSourceGuardTests.cs` `NothingTheClientShipsCanRecordSound` | test | total ban on every recording API | Replaced by `TheOnlyFileThatCanListenIsPhraseListeningCs`: exactly one file, and the ban list grew to cover the routes that file uses (`WasapiRecorder`, `WasapiRecorderBuilder`, `CaptureDataAvailableHandler`, `CaptureBufferLease`, `StartRecording`, `KeywordSpotter`, `OnlineRecognizer`, `VoiceActivityDetector`, `AcceptWaveform`, …) |
| the same file | test | — | New: `NothingTheClientShipsCanKeepOrSendWhatAMicrophoneHeard` (§4.2) and `ListeningIsOnlyEverBuiltInOnePlaceAndIsOffUntilItIsSwitchedOn` (§4.1) |
| the same file, `TheOnlyEndpointsAskedForAreOutputs` | test | every audio endpoint ask names Render | **unchanged**, with a comment saying why it did not have to move (§3.3) |
| the same file, `OnlyTheEightDeclaredPlacesMakeOutboundRequests` | test | eight senders | Nine: `PhraseDownload.cs` added, renamed `OnlyTheNineDeclaredPlacesMakeOutboundRequests` |
| `src/Modbot.Companion.App/Program.cs` §"What it writes to your disk" | class doc | voices and clips | also names the phrase model under `phrases`, and that no sound from a microphone is written anywhere |
| `src/Modbot.Companion.App/Program.cs` §"What leaves the machine" | class doc | "never a recorded clip…" | also "never a recording of anything your microphone heard", and the one phrase-model download |
| `src/Modbot.Companion.App/Program.cs` | class doc | — | New §"It can listen for one phrase, and only when you switch that on" |
| `src/Modbot.Companion.App/ScreenRecording.cs` | class doc | "the ban on every recording API … stands untouched" | left as it is: a **clip** still records no sound, which is what that sentence is about, and §4.2 keeps it true |
| `docs/content/docs/companion/listening.mdx` | docs | — | New page: the whole of it, for a suspicious reader |
| `docs/content/docs/companion/install.mdx` | docs | listed Clips as the one thing it can do | names Listening as the other, off by default, nothing kept or sent |
| `docs/content/docs/companion/clips.mdx` | docs | Save a clip from inside VR | gains "Or say it", pointing at the new page |
| `docs/content/docs/companion/settings.mdx` | docs | — | `listenForPhrase.on` and `notifications.sound` rows; the sound's own section rewritten (§8) |
| `docs/content/docs/privacy.mdx` | docs | "What stays on a moderator's own PC" listed Clips | gains Listening, in the same shape |
| `docs/content/docs/security.mdx` | docs | what a stolen device token cannot reach | gains listening: cannot switch it on, cannot hear anything, cannot learn it is on |
| `docs/content/docs/not-built-yet.mdx` | docs | — | Five rows: the phrase cannot be changed, no microphone picker, no Linux, no spoken reason without the voice |
| `PRIVACY_POLICY.md` "Voice presence, not voice" | policy | **"There is no code in it that touches a microphone"** | Narrowed: nothing records or transcribes audio, nothing keeps or sends a recording, and the one microphone Modbot can open is described under the companion's section |
| `PRIVACY_POLICY.md` "What does the companion send?" | policy | screen recording paragraph | gains a microphone paragraph in the same shape |
| `THIRD-PARTY-NOTICES.md` | notices | "Playback only; the client references nothing that records" | Narrowed to one file and one shared-mode microphone; the phrase model's address, size and hash added beside the voice's |
| `src/Modbot.Landing/` | marketing | never made a sound claim | **Left alone**, checked rather than missed: it says the companion reads the log and nothing about microphones |

---

## 5. The phrase, and why it cannot be changed

### 5.1 What is listened for

Four spellings of one request:

| Said | Pieces the matcher is given |
|---|---|
| Modbot, clip that | `▁MO D B O T ▁C LI P ▁THAT` |
| Mod bot, clip that | `▁MO D ▁BO T ▁C LI P ▁THAT` |
| Modbot, clip this | `▁MO D B O T ▁C LI P ▁THIS` |
| Mod bot, clip this | `▁MO D ▁BO T ▁C LI P ▁THIS` |

Four rather than one because the matcher hears sounds rather than spellings: "Modbot" is not a word
this model was trained on, so it can come out as one run of pieces or as "mod" and "bot" separately;
and somebody saying "clip this" means what somebody saying "clip that" means. The matcher is given
all four at once and matches whichever arrives, so four costs nothing.

Each is at least three words long. A one-word phrase, in a voice chat with people talking, is a clip
saved every few minutes for no reason.

### 5.2 Why a moderator cannot type their own

**It is not cheap with this engine, and the failure mode is severe.** The matcher is not given
words. It is given the pieces its own 500-entry vocabulary is built from, and turning "Modbot, clip
that" into `▁MO D B O T ▁C LI P ▁THAT` is the model's own SentencePiece tokenizer — a second native
library, with its own binary, its own licence and its own entry in the notices, shipped so that a
moderator can rename a phrase.

And a piece the vocabulary does not contain is not a warning and not a refusal:
`sherpa-onnx/csrc/utils.cc` logs it and calls `SHERPA_ONNX_EXIT(-1)`, which ends the process. A
typed phrase that produced one unknown piece would kill the client.

So the four above were worked out once, from this model's own vocabulary builder, with every piece
looked up in `tokens.txt` before being written down, and they are constants in `PhraseModel.cs`.
`PhraseModelTests` holds their shape — upper-case letters, apostrophes and the word-start mark, at
least three words, one phrase a line — without needing the model on disk.

There is a third reason, and it is the better one: a phrase somebody can type is a phrase this
client would have to encode at run time, which is a piece of general speech machinery sitting in a
program whose whole argument is that it has none.

### 5.3 The phrase file

The engine takes phrases only as a file path, so `PhraseDownload` writes `phrases.txt` beside the
model from `PhraseModel.PhrasesText()` — one line of pieces per phrase and nothing else. The
model's own format allows a plain-English label after an `@`, which would have made the file
readable, but it takes **one whitespace-delimited word** rather than a sentence, and the rest of the
line would then be looked up as pieces and end the process. Not worth it for a label; the English is
in `PhraseModel.cs` beside every line of pieces.

The phrase list goes into the marker file too, so a client whose phrase list has changed rewrites
the folder rather than listening for the old list forever.

### 5.4 Firing once

Two guards, on either side of the event:

1. **The matcher is reset after a match** (`KeywordSpotter.Reset`), which is what stops one
   utterance matching over and over as the rest of it arrives.
2. **`PhraseHeard`** refuses a second phrase within **6 seconds**, whichever phrase it was. That
   covers the matcher offering the same run of sound twice and covers somebody repeating themselves
   because they were not sure it heard. Six seconds is far short of the two-to-five minutes a clip
   covers, so a second real moment is never swallowed.

A refused match is dropped rather than held. A clip saved five seconds late is not the clip that was
asked for.

---

## 6. What happens when it fires

`HeardAPhrase` in `Program.cs`, on the window's thread.

1. **The fire-once rule** (§5.4). A refused match does nothing at all.
2. **If a clip can be saved** — `ClipsStatus.CanSave`, which is the same value the overlay's **Save
   a clip** control reads — one line goes into the client's own journal and `SaveClip()` runs. That
   is the same `SaveClip` the settings button and the overlay control call, so there is one rule
   about making room, one naming scheme and one journal line however it was asked for (clips §11.4).
3. **If it cannot**, nothing is saved, the reason goes into the journal, and the moderator is told
   which of six reasons it was.

### 6.1 The answer, and that it can be a no

This is most of the work, for the same reason it was most of the work for the overlay button (clips
§11.3): inside a headset there is no settings screen and no file explorer, and a phrase that quietly
did nothing would leave somebody believing they had kept a moment.

The answer arrives through the same wait the button uses — the recorder writes the file on its own
thread, `AnswerTheSave` watches for a new file name or a fresh complaint, and calls it a failure
after ten seconds if neither arrives.

**With the voice on**, the answer is spoken. `VoiceAnnouncer.Answer` is new: a line that is said
before anything else waiting, because somebody is standing there. It is **not** filtered by the
Notifications card — those filters are about what the client volunteers, and this is a reply to a
person who just spoke, the same reasoning that makes the Test button always speak. It still needs
the voice switched on, and is still silent while reporting is paused, because both of those are the
moderator asking for a quiet PC.

| When | What it says |
|---|---|
| A clip was saved | Clip saved. |
| Clips is off | Clips are switched off, so there was nothing to save. |
| VRChat is not running | VRChat is not running, so there was nothing to save. |
| VRChat's window not found yet | Modbot has not found VRChat's window yet, so there was nothing to save. |
| The folder cannot be used | The clips folder cannot be used, so nothing was saved. |
| This PC cannot record | This PC cannot record, so nothing was saved. |
| The recorder had stopped | Recording has stopped, so nothing was saved. |

**With the voice off**, a saved clip plays the notification sound — as long as the sound itself is
switched on — and a clip that was **not** saved plays nothing.

That last choice is deliberate and is the weakest part of this design. One short sound cannot say
which of six reasons it was, and a sound that meant both "kept" and "not kept" would be worse than
silence, because a moderator would learn to hear it as "kept". What silence costs is that "it did
not hear me" and "it heard me and could not save" sound the same. What they do not share is
everything else: the reason is on the Clips card, on the overlay's own **Save a clip** control for
eight seconds — the same `_clipSave` the button sets — and in the Events page either way. §10 lists
it as not built.

### 6.2 Every firing is in the journal

Including the ones where nothing was saved. "Show me every time this thing acted on my microphone"
has an answer on the Events page, which is the shape every other capability in this client has.

---

## 7. Where the code is

| Path | What |
|---|---|
| `src/Modbot.Companion/Listening/ListeningSettings.cs` | The switch. Off. |
| `src/Modbot.Companion/Listening/PhraseModel.cs` | The pinned model, and the four phrases as pieces and as English |
| `src/Modbot.Companion/Listening/PhraseDownload.cs` | Fetch, check, unpack five files, write the phrase list |
| `src/Modbot.Companion/Listening/ListeningRule.cs` | When it listens, and what the card shows |
| `src/Modbot.Companion/Listening/PhraseHeard.cs` | One sentence, one clip |
| `src/Modbot.Companion.App/Listening/PhraseListening.cs` | **The one file that can open a microphone** |
| `src/Modbot.Companion.App/MainWindow.Listening.cs` | The Listening card |
| `src/Modbot.Companion.App/Program.cs` | `ApplyListening`, `HeardAPhrase`, `AnswerOutLoud`, `SetListening` |
| `src/Modbot.Companion/Voice/VoiceAnnouncer.cs` | `Answer`, and `AnnouncementKind.Answer` ahead of everything else |

---

## 8. The notification sound

### 8.1 What was wrong with it

`Sounds/Bleep.cs` was two sine tones — 880 Hz for 80 ms, then 1245 Hz for 110 ms, each with a 6 ms
fade at both ends. Read as a description rather than as code, that is: two bare tones with no
musical relation to each other, both in the band the ear is most sensitive to and most quickly
annoyed by, each switched on and off almost instantly, with no decay at all. It is a smoke alarm.
The moderator who hears it forty times an evening was right.

### 8.2 What it is now

Two **struck notes**, still made from a formula, still under half a second:

| | |
|---|---|
| First note | **440 Hz** (A above middle C), where it was 880 |
| Second note | **660 Hz** — exactly three halves of the first, a perfect fifth |
| Second note struck at | **140 ms**, while the first is still ringing |
| Each note lasts | **340 ms** |
| Attack | **18 ms**, along a quarter-cosine rather than a straight line |
| Decay | exponential, falling to about a third every **90 ms** |
| Release | the last **45 ms** taken smoothly to exactly zero |
| Overtones | the 2nd, 3rd and 4th partials at 0.30, 0.13 and 0.06 of the note, each dying faster than the one below |
| Whole sound | **480 ms** |

Each choice is doing a job. Lower, because the old pair sat where annoyance lives. A perfect fifth,
because it is the simplest interval after the octave and is why the pair reads as one sound rather
than two unrelated beeps. A curved attack rather than a ramp, because a ramp still has a corner at
the top and a corner is a click. An exponential decay with a real tail, because that is what a
struck thing does and a 6 ms fade is what a switch does. Overtones that die faster the higher they
are, because that is the difference between an instrument and a test tone. And the second note
struck while the first is still ringing, so they overlap rather than queue.

The notes are added together and the whole thing is then scaled so its peak is exactly `Height`
(0.7), which means overlapping notes can never clip and the loudness does not depend on how many
overtones there happen to be.

**Neither of us can hear this.** It was written from the physics and checked numerically: the peak
is exactly 0.7, the RMS is 0.186 (it is plainly not silence), the first and last samples are exactly
zero, the first two milliseconds are under a fifth of full height, each half crosses zero at
twice the pitch it was asked for, and there is still audible level four fifths of the way through.
`BleepTests` checks every one of those. Somebody with speakers still has to listen to it.

**Later the same day this pair became a family of five**, and the pair itself was not touched: a
flagged arrival still makes exactly the sound described above. What was added around it is a softer
single note for ordinary arrivals, the pair played twice for more than one flagged arrival, a
sharper three-note climb for a fault, and a quiet falling pair for an ending. The notification sound
design (2026-09-18) §2.6 is the whole of it.

### 8.3 Still generated, and now escapable

The 2026-09-18 decision to generate rather than ship a file stands, and the reasoning holds: nothing
to license, nothing whose origin somebody has to check, nothing to go missing from an install.

But a sound somebody cannot change is a sound they end up switching off altogether, and the whole
point of this section is that one person's "pleasant" is another person's "annoying". So:
`notifications.sound` points at a **`.wav` file of their own**, with **Save** and **Use Modbot's
sound** on the Notifications card.

- **`.wav` only.** A WAV is a header and the samples — about sixty lines, no library. Every other
  format is a decoder: a dependency, a licence, and a parser to be wrong in, for half a second of
  sound. Any program on the machine saves a WAV.
- Plain PCM at 8, 16, 24 or 32 bits, or 32-bit float, mono or stereo, any sample rate, with the
  extended header read as what it says it really is. An MP3 renamed `.wav` is refused rather than
  played as noise.
- At most **10 seconds** is played and files over **16 MB** are refused, so a path typed by mistake
  is refused rather than read into memory.
- **A missing or unreadable file plays Modbot's own sound** and puts one sentence on the
  Notifications card. It is read once and then held, so forty sounds in an evening is one read; a
  path that failed is not tried again until it changes.

`SoundFileTests` covers each of those.

---

## 9. Settings

Two changes, both written whole by their own writer, both leaving every other field in
`settings.json` exactly as it was:

```json
"notifications": {
  "bleep": true,
  "volume": 70,
  "trayNoticesShown": 0,
  "sound": "C:\\Sounds\\ping.wav"
},
"listenForPhrase": { "on": false }
```

| Field | Means | Default |
|---|---|---|
| `notifications.sound` | A `.wav` of the moderator's own, or absent for Modbot's | absent |
| `listenForPhrase.on` | Open the microphone while VRChat runs | `false` |

`sound` is left out of the file when it is blank, the same rule the clips folder follows: the file
says nothing rather than saying `""`. `listenForPhrase.on` is always written, including `false`, so
a file somebody opens says plainly that it is off.

A missing object, a missing field or a field of the wrong shape takes the default — which for both
of these means off.

---

## 10. What is not built

- **Choosing the phrase** (§5.2). Decided rather than pending.
- **Choosing the microphone** (§3.3). It follows Windows' default, which is also what keeps the
  client from ever asking for a list of microphones.
- **Listening on Linux** (§3.4). Follows recording, which is Windows only.
- **A spoken reason with the voice off** (§6.1). A saved clip makes a sound; one that was not saved
  makes none.
- **A voice activity detector in front of the matcher** (§2.4).
- **Tuning.** `KeywordsScore` and `KeywordsThreshold` are the engine's own defaults (1.0 and 0.25).
  Nothing here has been tuned, because tuning needs a microphone, a headset and somebody saying the
  phrase forty times.
- **Any measurement at all.** §3.5 is estimates. Nothing in this feature has been run against a real
  microphone, and the false-accept and false-reject rates for a made-up word like "Modbot" are
  unknown. The first thing to do with a headset and a build is to find out whether it hears you, and
  whether it hears you when nobody said it.
