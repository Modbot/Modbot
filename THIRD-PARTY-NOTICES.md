# Third-party notices

Modbot is licensed under the AGPL-3.0 (see `LICENSE`). The desktop client and its installer also
ship code that belongs to other people, under their own terms. This file records what, and the
notices those terms ask for.

Library code pulled in as NuGet packages (Avalonia, SkiaSharp, HarfBuzzSharp, Vortice, Serilog,
Velopack, and the .NET runtime itself) carries its licence inside each package; all of them are
MIT-licensed, and the notices are in the packages' own `LICENSE` files. The one thing copied into
this repository as a binary is listed in full below.

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

## Fonts

**Bricolage Grotesque** (Jeremy Landes, Atelier Triay), **IBM Plex Sans** and **IBM Plex Mono**
(IBM) are used under the SIL Open Font License 1.1. The web projects bundle the Plex and Bricolage
faces from the `@fontsource` packages; the desktop client embeds a static cut of Bricolage Grotesque
at `src/Modbot.Client.App/Assets/Fonts/`, with the licence beside it, for its wordmark. **Inter**
(Rasmus Andersson) reaches the desktop client through the `Avalonia.Fonts.Inter` package, also
under the SIL Open Font License 1.1.

The SIL Open Font License permits use, bundling and redistribution of the fonts, and forbids
selling the fonts by themselves. The full text is at https://openfontlicense.org.
