# Modbot — The companion's voice: which model speaks

- **Date:** 2026-09-18
- **Status:** Implemented with this document
- **Covers:** which text-to-speech model the companion downloads and speaks with, why that one,
  what was measured, what happens to the old voice on a PC that already has it, and the voice
  picker on the Settings screen
- **Replaces:** the Piper voice `en_US-kristin-medium` pinned since the voice shipped (2026-09-16)
- **Related:** `THIRD-PARTY-NOTICES.md` ("The companion's voice"),
  `docs/content/docs/companion/settings.mdx` ("The one download")

---

## 1. Why this changed

The voice worked. It did not sound good. `en_US-kristin-medium` is a Piper model — a small VITS
network trained on one reader — and it sounds like one: flat, clipped at the ends of sentences, and
wrong on the made-up names VRChat is full of. A moderator asked for a better-sounding voice, free,
and that is the whole of the requirement.

Nothing else about the voice was wrong, so nothing else changes. The engine is still sherpa-onnx in
this process, the download is still one pinned address checked against one size and one SHA-256, the
announcements are still made and played on the moderator's own PC, and a voice that will not
download or will not load still cannot stop the client reporting presence.

---

## 2. What was looked at

Everything compared here is published in the same place the old voice came from: the sherpa-onnx
project's `tts-models` release on GitHub. That matters more than it sounds. Staying in that feed
means the download, the size check, the hash check and the unpacking are the code that is already
written and already tested, and the engine already in the client can run the result.

The measurements are real. Each archive was downloaded, unpacked and run through
`org.k2fsa.sherpa.onnx` 1.13.8 — the version the client already uses — on an Intel Core i9-14900KF,
speaking four sentences of the kind the companion actually says ("Kitty Fantastico joined.",
"Flagged user Rubber Duck Debugger joined.", "Sparkle Monkey left.", "three people joined."), two
threads, after one warm-up sentence. **CPU per line** is the processor time one such line costs;
**wait per line** is how long the moderator waits for it before it starts playing.

| | Sound | Licence | Download | On disk | Memory | CPU per line | Wait per line |
|---|---|---|---|---|---|---|---|
| **Kokoro 82M v0.19, full size** (chosen) | Best of the free small models; ranked first on Hugging Face's public TTS Arena listening comparison, above models many times its size | Apache-2.0, plus espeak-ng data (GPL-3.0-or-later) | 305 MB | 353 MB | 503 MB | 1.4 s | 0.7 s |
| Kokoro 82M v0.19, 8-bit copy | Same model, slightly degraded by shrinking | same | 99 MB | 152 MB | 311 MB | **3.0 s** | **1.5 s** |
| Kokoro 82M v1.0 / v1.1, many languages | Same family, newer, 53 voices | same, plus Chinese word lists | 334–348 MB | ~390 MB | not measured | not measured | not measured |
| Piper `en_US-kristin-medium` (what we had) | Flat, clipped, poor on invented names | MIT, plus espeak-ng data | 64 MB | 80 MB | 196 MB | 0.13 s | 0.06 s |
| Piper `en_US-ryan-high` / `ljspeech-high` | Better than medium; still recognisably Piper | MIT | 110 MB | ~130 MB | not measured | not measured | not measured |
| Matcha `en_US-ljspeech` | Smoother than Piper, one voice, still a single-reader model | Apache-2.0 / BSD | 73 MB | not measured | not measured | not measured | not measured |
| Kitten nano / mini | Tiny and quick; roughly Piper-grade | Apache-2.0 | 25–150 MB | not measured | not measured | not measured | not measured |
| ZipVoice | Voice cloning from a sample | Apache-2.0 | 104–605 MB | not measured | not measured | not measured | not measured |

### 2.1 On judging how it sounds

Nobody involved in writing this listened to the candidates side by side, and the spec should say so
rather than pretend to a verdict it did not reach. The ranking above rests on Hugging Face's TTS
Arena, a public listening comparison where people are played two clips and pick one; Kokoro-82M has
sat at the top of it since it was published, above much larger models, and well above every Piper
voice. That is the strongest evidence available without a listening test, and it points the same way
as every other public comparison of the two.

The voice picker in §5 is the hedge. If Bella is not to somebody's taste there are nine more, and
nothing else has to change for them to try one.

---

## 3. What was chosen, and why not the smaller copy

**Kokoro 82M, version 0.19, English, full size.**

```
https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2
319,625,534 bytes
SHA-256 912804855a04745fa77a30be545b3f9a5d15c4d66db00b88cbcd4921df605ac7
```

Downloaded and hashed from that address while writing this; the size and hash above are this
file's, not a guess, and they match the digest GitHub itself records for the asset. The archive
holds `model.onnx`, `voices.bin`, `tokens.txt` and `espeak-ng-data`, and speaks at 24,000 samples a
second.

The part worth writing down is that **the smaller copy is the slower one**. The obvious choice for a
machine that is also running VRChat is the 8-bit build: a third of the download, half the memory,
and normally faster. It is not faster. On the same machine, the same sentences and the same engine,
the 8-bit model costs **twice** the processor time of the full-size one and takes **twice** as long
before a line starts playing. ONNX Runtime has no fast 8-bit path for the operations this model is
built from, so it emulates them, and emulation is dearer than the arithmetic it replaces. Choosing
the small file to be kind to the moderator's CPU would have done the opposite.

So the trade is honest: the full-size model costs about five times the download and four times the
disk of the Piper voice it replaces, and buys a voice people actually prefer, at a processor cost
that is still small — 1.4 seconds of one or two cores for a line that takes about 1.7 seconds to
say, once every few seconds at the very busiest.

It is not free, and it is worth naming the cost plainly: this is roughly ten times the Piper voice's
processor cost per line. That is the price of the thing that was asked for. The queue that decides
what gets said (`AnnouncementQueue`) drops a join that has waited more than five seconds, and that
still holds with room to spare: making a line and playing it is about 2.4 seconds here, and would
have to get more than twice as slow on a weaker PC before a join started being dropped that would
not have been dropped before. The 8-bit model would have been at 3.2 seconds before anything else
went wrong, which is the second reason it was not chosen.

### 3.1 What was rejected

- **Kokoro v1.0 and v1.1, many languages.** Newer and larger, with 53 voices. They need two extra
  data folders that exist only to read Chinese, for a companion whose every sentence is English and
  whose only variable is a person's display name. More to download, more to pin, more to go wrong,
  for nothing this feature uses.
- **A bigger Piper voice** (`ryan-high`, `ljspeech-high`). Cheap, easy, and still Piper. A high
  Piper voice is a clearer recording of the same flatness; it does not answer the complaint.
- **Matcha and Kitten.** Both are good for what they are and both are single-reader models in the
  same class as the voice being replaced. Changing engines to land in the same place is not worth a
  moderator re-downloading anything.
- **ZipVoice.** It clones a voice from a recording. Modbot has no recording to clone, must never ask
  for one, and the client contains nothing that can record — the source guard fails the build if it
  ever does.
- **Anything not in the sherpa-onnx release feed.** A second place to fetch from is a second address
  to pin, a second licence to check, and a second thing that can disappear. The rule is one address.

---

## 4. Licences

| Piece | Licence |
|---|---|
| Kokoro-82M model weights (`model.onnx`), the voice data (`voices.bin`) and the token list (`tokens.txt`) | Apache-2.0, as the `LICENSE` file inside the archive states |
| `espeak-ng-data`, which turns written words into sounds | GPL-3.0-or-later |
| `org.k2fsa.sherpa.onnx`, the engine | Apache-2.0 |

All three are free for commercial use and redistribution. The espeak-ng data was already accepted
for exactly this purpose and is unchanged: it is the same folder the Piper archive carried, and
GPL-3.0 code inside an AGPL-3.0 program is permitted by both licences. `THIRD-PARTY-NOTICES.md`
records the swap.

Nothing is redistributed by Modbot either way. The archive is not in the installer; it is fetched
from GitHub, onto the moderator's own PC, the first time somebody turns the voice on.

---

## 5. Ten voices, one download

Kokoro carries eleven voices in one file. Ten of them are people — Bella, Nicole, Sarah, Sky, Adam,
Michael, Emma, Isabella, George and Lewis — and the eleventh is a blend of two of the others, which
is left out because a list with "Default" sitting between "Bella" and "Nicole" reads as a mistake.

They cost nothing extra. They are already in the one file that is already being downloaded, and
picking between them is one number handed to the engine. So the Settings screen gets a **Voice**
list, saved in the `voice` object of `settings.json` as `name`. **Bella** is the one a fresh install
speaks with.

This does not weaken "one pinned download". There is still one address, one size, one hash and one
folder. What changed is that the folder was always going to contain ten voices and the moderator may
as well be allowed to choose.

A name in the file that the model does not have — a hand-edited settings file, or a name from a
later version — falls back to Bella rather than failing. A voice that will not speak must not be a
voice that will not start.

---

## 6. A PC that already has the old voice

The old voice sits in `%APPDATA%\Modbot\voices\en_US-kristin-medium`. The new one goes in
`%APPDATA%\Modbot\voices\kokoro-en-v0_19`.

Nothing has to be migrated, because nothing in the old folder is reusable: it is a different model
of a different shape, and the marker file inside it names a different hash. The existing check
(`VoiceModel.IsPresent`) already looks for the pinned folder with a marker holding the pinned hash,
so an old install simply reads as "not downloaded" and fetches the new voice the next time the voice
is turned on or **Test** is pressed.

**The old folder is deleted, once the new voice is in place and not before.** Keeping it as a
fallback was considered and rejected: the client can only load the voice it has pinned, so a
fallback would be 80 MB that nothing is able to speak with. Deleting it only after the new one has
downloaded, hashed and unpacked means a failed or abandoned download leaves the PC exactly as it
was. The same sweep removes any other leftover voice folder, so a moderator who has been through two
changes of voice does not accumulate all of them.

### 6.1 What the Settings screen says while this happens

The card has always said `Downloading voice… 40%`. Two things about that are now untrue enough to
fix.

First, it does not say how much. 305 MB is not 64 MB, and a progress figure with no total behind it
tells a moderator on a slow connection nothing about whether to wait. The line now names the size:
`Downloading voice… 40% of 305 MB`.

Second, it does not distinguish a first download from a replacement. A moderator whose voice worked
yesterday and who is now watching a progress bar should be told which of those is happening, so
there is a fourth state — **Replacing** — for "the old voice is still on this PC and the new one is
coming": `Getting the new voice… 40% of 305 MB`.

Both are statements of what is happening, not explanations of why, which is the only kind of text
the screen is allowed.

---

## 7. What this touched

| File | Change |
|---|---|
| `src/Modbot.Companion/Voice/VoiceModel.cs` | The pinned voice is Kokoro; a voice file (`voices.bin`) joins the model, tokens and data; the ten voice names and their numbers; `Number()` maps a name to one |
| `src/Modbot.Companion/Voice/VoiceDownload.cs` | Checks the voice file is in the archive; sweeps away other voice folders once the new one is in place |
| `src/Modbot.Companion/Voice/VoiceOutput.cs` | `Speak` takes the voice's name |
| `src/Modbot.Companion/Voice/VoiceSettings.cs` | `VoiceName`, defaulting to Bella |
| `src/Modbot.Companion/Voice/VoiceStatus.cs` | `Replacing`, and the download's size |
| `src/Modbot.Companion/Voice/VoiceAnnouncer.cs` | Hands the chosen voice's name to the engine |
| `src/Modbot.Companion/Presentation/CompanionSettings.cs` | `name` inside the `voice` object, read and written |
| `src/Modbot.Companion.App/Voice/SherpaVoice.cs` | Loads Kokoro rather than a Piper VITS model; speaks as the chosen voice |
| `src/Modbot.Companion.App/Voice/VoiceHost.cs` | Knows an old voice is present, so the state can be `Replacing`; reports the size |
| `src/Modbot.Companion.App/MainWindow.cs` | The **Voice** list; the status line names the size |
| `THIRD-PARTY-NOTICES.md`, `docs/content/docs/companion/settings.mdx` | The new address, size, hash and licences |

Playback needed nothing. `VoiceClip` has always carried its own sample rate; Windows already
resamples to whatever the device runs at, and OpenAL is handed the rate directly. 24,000 rather than
22,050 passes straight through both.

The engine package is unchanged at `org.k2fsa.sherpa.onnx` 1.13.8, which has supported Kokoro since
well before it. Nothing was upgraded to make this work.
