# Third-party notices

Modbot is licensed under the AGPL-3.0 (see `LICENSE`). The companion and its installer also
ship code that belongs to other people, under their own terms. This file records what, and the
notices those terms ask for.

Library code pulled in as NuGet packages (Avalonia, SkiaSharp, HarfBuzzSharp, Vortice, Silk.NET, Serilog,
Velopack, and the .NET runtime itself) carries its licence inside each package; all of them are
MIT-licensed, and the notices are in the packages' own `LICENSE` files. The exceptions are
`LanguageDetection.Ai`, the offline language detector the server uses to mark the language on a
moderation flag — Apache-2.0, a port of Nakatani Shuyo's `language-detection`, with its notice in
its own package — and the packages behind the companion's voice, listed in their own section
below because two of them are not MIT-licensed. The one thing copied into this repository as a
binary is listed in full below.

## OpenVR (`openvr_api.dll`)

`src/Modbot.Overlay/native/win-x64/openvr_api.dll` is Valve's 64-bit Windows build of the OpenVR
runtime library, taken unmodified from <https://github.com/ValveSoftware/openvr> at tag `v2.15.6`
(SHA-256 `bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a`). It is installed
beside `Modbot.exe`, with the licence file `openvr_api.LICENSE.txt` next to it, and is how the
headset overlay talks to SteamVR on the same machine.

It is redistributed under the BSD 3-clause licence below.

```
Copyright (c) 2015, Valve Corporation
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
this list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
may be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

"SteamVR" and "OpenVR" are trademarks of Valve Corporation. Modbot is not affiliated with or
endorsed by Valve.

## The companion's voice, and listening for a phrase

The companion can say what it sees out loud, and — off unless a moderator switches it on — listen
for one spoken phrase. The pieces that make both work come from NuGet and are not copied into this
repository, but two of them are not MIT-licensed and two of them are fetched at run time, so they
are listed here.

| What | Package | Licence | Notes |
|---|---|---|---|
| Text to speech engine, and the phrase matcher | `org.k2fsa.sherpa.onnx` 1.13.8 and its native package for each platform | Apache-2.0 | One library serves both. Its native library statically links ONNX Runtime (MIT), piper-phonemize (MIT) and **espeak-ng (GPL-3.0-or-later)**, which turns words into sounds. GPL-3.0 code inside an AGPL-3.0 program is permitted by both licences. |
| Windows sound output, the one microphone, and the sound in a clip | `NAudio.Wasapi` 3.1.0 (with `NAudio.Core`) | MIT | Playback everywhere. From `Listening/PhraseListening.cs` only, and only while a moderator has switched listening on, one shared-mode microphone — nothing it hears is recorded, kept or sent. From `ClipSound.cs` only, and only while a moderator has switched Clips on, the sound **one named program** is playing: VRChat, and Discord if they ticked that box. Never what the machine is playing; the call that would hand that over is banned everywhere in the client. |
| Linux sound output | `Silk.NET.OpenAL` 2.23.0, `Silk.NET.OpenAL.Extensions.Enumeration` 2.23.0 | MIT | The binding. |
| Linux sound output | `Silk.NET.OpenAL.Soft.Native` 1.23.1 | **LGPL-2.0-or-later** | OpenAL Soft (`libopenal.so`), shipped beside the companion and loaded as a shared library, which is the use the LGPL permits without conditions on Modbot's own code. Its source is at <https://github.com/kcat/openal-soft>. |
| Unpacking the voice | `SharpZipLib` 1.4.2 | MIT | Reads the bzip2 layer of the downloaded archive. |

**The voice itself is not shipped.** It is downloaded once, when a moderator first turns the voice
on, from one pinned address — the sherpa-onnx project's `tts-models` release on GitHub — and
checked against a pinned SHA-256 before it is used (`src/Modbot.Companion/Voice/VoiceModel.cs`):

```
https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2
319,625,534 bytes, SHA-256 912804855a04745fa77a30be545b3f9a5d15c4d66db00b88cbcd4921df605ac7
```

That archive holds **Kokoro 82M**, version 0.19, English (`model.onnx`, `voices.bin` and
`tokens.txt`, all **Apache-2.0**, from <https://huggingface.co/hexgrad/Kokoro-82M> — the archive
carries the licence text itself) and a copy of espeak-ng's language data (GPL-3.0-or-later, from
<https://github.com/espeak-ng/espeak-ng>). Both sit in `%APPDATA%\Modbot\voices` on the
moderator's PC and are never redistributed by Modbot.

**The phrase matcher is not shipped either.** It is downloaded once, when a moderator first turns
**Listening** on, from one pinned address — the sherpa-onnx project's `kws-models` release on
GitHub — and checked against a pinned SHA-256 before it is used
(`src/Modbot.Companion/Listening/PhraseModel.cs`):

```
https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01.tar.bz2
17,626,723 bytes, SHA-256 f170013b4716e41b62b9bfd809687c207cef798ef9bc6534d524e17af9b6561a
```

That archive holds a **3.3M-parameter zipformer keyword model** trained on GigaSpeech
(**Apache-2.0**, from <https://www.modelscope.cn/pkufool/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01>).
Five files from it sit in `%APPDATA%\Modbot\phrases` on the moderator's PC and are never
redistributed by Modbot.

Until 2026-09-18 this was the Piper voice `en_US-kristin-medium` (MIT, from
<https://github.com/rhasspy/piper>). It was replaced because it did not sound good enough; the
voice engine design (`.agent/specs/2026-09-18-voice-engine-design.md`) has the comparison. A client
that already has the old voice deletes it once the new one is downloaded.

## Fonts

**Bricolage Grotesque** (Jeremy Landes, Atelier Triay), **IBM Plex Sans** and **IBM Plex Mono**
(IBM) are used under the SIL Open Font License 1.1. The web projects bundle the Plex and Bricolage
faces from the `@fontsource` packages; the companion embeds a static cut of Bricolage Grotesque
at `src/Modbot.Companion.App/Assets/Fonts/`, with the licence beside it, for its wordmark. **Inter**
(Rasmus Andersson) reaches the companion through the `Avalonia.Fonts.Inter` package, also
under the SIL Open Font License 1.1.

The SIL Open Font License permits use, bundling and redistribution of the fonts, and forbids
selling the fonts by themselves. The full text is at https://openfontlicense.org.
