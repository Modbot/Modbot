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

## The companion's voice

The companion can say what it sees out loud. The pieces that make that work come from NuGet and
are not copied into this repository, but two of them are not MIT-licensed and one of them is
fetched at run time, so they are listed here.

| What | Package | Licence | Notes |
|---|---|---|---|
| Text to speech engine | `org.k2fsa.sherpa.onnx` 1.13.8 and its native package for each platform | Apache-2.0 | Its native library statically links ONNX Runtime (MIT), piper-phonemize (MIT) and **espeak-ng (GPL-3.0-or-later)**, which turns words into sounds. GPL-3.0 code inside an AGPL-3.0 program is permitted by both licences. |
| Windows sound output | `NAudio.Wasapi` 3.1.0 (with `NAudio.Core`) | MIT | Playback only; the client references nothing that records. |
| Linux sound output | `Silk.NET.OpenAL` 2.23.0, `Silk.NET.OpenAL.Extensions.Enumeration` 2.23.0 | MIT | The binding. |
| Linux sound output | `Silk.NET.OpenAL.Soft.Native` 1.23.1 | **LGPL-2.0-or-later** | OpenAL Soft (`libopenal.so`), shipped beside the companion and loaded as a shared library, which is the use the LGPL permits without conditions on Modbot's own code. Its source is at <https://github.com/kcat/openal-soft>. |
| Unpacking the voice | `SharpZipLib` 1.4.2 | MIT | Reads the bzip2 layer of the downloaded archive. |

**The voice itself is not shipped.** It is downloaded once, when a moderator first turns the voice
on, from one pinned address — the sherpa-onnx project's `tts-models` release on GitHub — and
checked against a pinned SHA-256 before it is used (`src/Modbot.Companion/Voice/VoiceModel.cs`):

```
https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-kristin-medium.tar.bz2
67,259,230 bytes, SHA-256 c2206f572df2956c50b1ae3367eebce3853c663e890cba8048cd62b1e4dbe6c7
```

That archive holds the Piper voice `en_US-kristin-medium` (model weights MIT, from
<https://github.com/rhasspy/piper>; the voice was trained from scratch on public-domain LibriVox
recordings, per its model card) and a copy of espeak-ng's language data (GPL-3.0-or-later, from
<https://github.com/espeak-ng/espeak-ng>). Both sit in `%APPDATA%\Modbot\voices` on the
moderator's PC and are never redistributed by Modbot.

## Fonts

**Bricolage Grotesque** (Jeremy Landes, Atelier Triay), **IBM Plex Sans** and **IBM Plex Mono**
(IBM) are used under the SIL Open Font License 1.1. The web projects bundle the Plex and Bricolage
faces from the `@fontsource` packages; the companion embeds a static cut of Bricolage Grotesque
at `src/Modbot.Companion.App/Assets/Fonts/`, with the licence beside it, for its wordmark. **Inter**
(Rasmus Andersson) reaches the companion through the `Avalonia.Fonts.Inter` package, also
under the SIL Open Font License 1.1.

The SIL Open Font License permits use, bundling and redistribution of the fonts, and forbids
selling the fonts by themselves. The full text is at https://openfontlicense.org.
