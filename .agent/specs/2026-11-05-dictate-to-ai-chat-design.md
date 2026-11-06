# Dictate: speak a message to the AI chat

**Status:** phase A built 2026-11-05 (the microphone, the speech engine, the download, the
controller and the guard change). The button on the headset panel is phase A3; the chat panel is
phase B. Approved 2026-11-04, split into phases 2026-11-05.
**Narrows:** the listening design (2026-09-19) §3, which let **one** file open a microphone. It is
now two. See §4.

---

## 1. What was asked for

A moderator in a headset has no keyboard. The AI chat is typed. A **Dictate** button on the
overlay's button bar: tap it and speak (the microphone animates), tap again to stop (a spinner
while the words are written out), and the text goes into the chat's message box. The speech is
turned into text on the moderator's own PC. Nothing is sent to a speech service.

## 2. The phases

| Phase | What | State |
|---|---|---|
| A1 | The speech model pin, its download and the controller (`src/Modbot.Companion/Dictation/`) | built |
| A2 | The microphone and the speech engine (`DictationListening.cs`), the host wiring (`CompanionHost.Dictation.cs`), the guard change, the notice | built |
| A3 | The button on the headset panel: mic icon, animated while listening, spinner while writing, percent while the model is fetched | to do |
| B | The chat panel, its size, the text arriving as the AI writes it, and the real switch for whether Dictate is offered | to do |

Until phase B the button is hidden behind a developer switch: the environment variable
`MODBOT_DICTATE=1`. Phase B replaces it with "AI Chat is on and I am allowed to use it".

## 3. How it behaves

- **Tap, model missing.** The model is fetched (about 108 MB, once). The button shows the
  percentage. When it is ready the button goes back to idle; the moderator taps again to speak, so
  nobody is recorded the moment a download ends.
- **Tap, model present.** The microphone opens, shared, the one picked for phrase listening or the
  Windows default. The button animates.
- **Tap again.** The microphone closes. The recording is written out as text on a worker thread.
  The text is handed to the chat; the button goes back to idle.
- **Sixty seconds.** A recording stops by itself at 60 seconds of sound, counted in samples, and
  is treated as a second tap.
- **Silence, or nothing recognised.** Nothing is sent. The button goes back to idle. A recording
  whose loudest moment is quiet is not even given to the engine, because the engine invents words
  for silence.
- **Taps while the model is being fetched or the words are being written** are ignored.
- **Failures** (no microphone, the engine throws, the download fails) become one sentence and the
  button goes back to idle.
- **Quitting** closes the microphone and drops the recording.

## 4. Privacy, and the guard

- **Audio is kept in memory only.** From the first tap to the text being written, and then it is
  dropped. It is never saved to disk and never sent anywhere. Only the text goes on, and only to
  the chat the moderator is typing into.
- **The engine.** Moonshine tiny, English, 8-bit (MIT, Useful Sensors), run by sherpa-onnx, which
  the client already uses to listen for a phrase. It runs on the processor. It is loaded to write
  one recording out and let go of straight after.
- **One download.** The only thing Dictate fetches: the model, once, on the first tap, from the
  pinned address on GitHub with nothing attached, checked against a pinned SHA-256. A test copy
  (`MODBOT_DATA_FOLDER`) downloads nothing.
- **The narrowing, 2026-11-05.** `CompanionSourceGuardTests` used to allow `PhraseListening.cs`
  and `ClipSound.cs` to name a recording API. It now allows three files:
  `ClipSound.cs`, `DictationListening.cs` and `PhraseListening.cs`. The new file is held to the
  phrase listener's rules, each in a test of its own: a disclosure block saying nothing is saved
  and nothing is sent, shared mode only (it gets its microphone from the phrase listener's
  shared-mode class and never names the exclusive mode), no loopback, no list of devices, and none
  of `File.`, `FileStream`, `StreamWriter`, `Directory.`, `HttpClient`, `ClientWebSocket` or
  `WaveFileWriter`. The model's paths come in as text from `DictationModel`, so the file never
  touches the disk.
- **The one list of microphones.** Phrase listening asks Windows for the list only while listening
  is on. A tap on Dictate asks once, only when a microphone is picked, to find that one.
- `DictationDownload.cs` joins the declared senders (twelve now), and `new DictationDownload(` is
  built in one place, after the test-copy check.

## 5. Phase B, not built yet

- The chat panel on the headset, and its resize.
- The AI's answer arriving as it is written.
- The text from Dictate going into the message box, and sending.
- The real gating, replacing `MODBOT_DICTATE`.
- Docs: a page for the feature, a line on the section's overview page, a line in the Introduction,
  and the privacy policy.
