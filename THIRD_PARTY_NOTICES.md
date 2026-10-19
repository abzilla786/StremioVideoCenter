# Third-Party Notices

Zino's Media Center uses third-party software and integrates with external services. Each component remains subject to its own applicable copyright, licence terms and conditions. The MIT License for Zino's Media Center does not replace or override the licences of third-party components.

## 1. Zino's Media Center

Copyright (c) 2026 abdullah (abzilla786)

The original code and documentation of Zino's Media Center are intended to be licensed under the MIT License, to the extent the project owner has the right to license that material.

The full licence text is available in [`LICENSE`](LICENSE).

The project's icon is AI-generated artwork commissioned for this project and approved by the owner. No claim of exclusive copyright or particular copyright status is made here.

## 2. MPV media player

Zino's Media Center uses [MPV](https://mpv.io/) as a separate media-player executable. The application launches `mpv.exe` and communicates with it through command-line options, JSON IPC and a window handle; it does not link directly to `libmpv`.

### Bundled binary identification

- **Version:** `mpv v0.41.0-1224-g6a262d11f`
- **Architecture:** x86-64 baseline
- **MPV commit:** `6a262d11f63733d37e3ee6d5fafcb1be1ef83952`
- **FFmpeg version string:** `N-127652-gffb15c94c`
- **FFmpeg commit:** `ffb15c94c286c9d1af911991b3ad08724427372a`
- **Binary size:** 121,949,184 bytes
- **SHA-256:** `0c7894d232438ed1d3f59884d438918fd5401eea9efed014dae6eaeec6277b80`

The binary was verified against the publisher's release archive on 19 October 2026.

### Build provenance

The supplied Windows build was published by the [zhongfly/mpv-winbuild project](https://github.com/zhongfly/mpv-winbuild).

- [Build release `2026-10-18-6a262d11f6`](https://github.com/zhongfly/mpv-winbuild/releases/tag/2026-10-18-6a262d11f6)
- [Build workflow run 39138561860](https://github.com/zhongfly/mpv-winbuild/actions/runs/39138561860)
- [MPV upstream source](https://github.com/mpv-player/mpv)
- [MPV Windows build project](https://github.com/zhongfly/mpv-winbuild)
- [Build scripts](https://github.com/shinchiro/mpv-winbuild-cmake)

The build evidence records FFmpeg configured with `--enable-gpl --enable-version3`, without `--enable-nonfree`. The build also records the following revisions:

- **mpv-packaging:** `1c534ce8c21fab0be2a394ad73decc15e015693e`
- **mpv-winbuild-cmake:** `1f8d2e76255abc48f0456d9be99b4cab9c6566e6`
- **LLVM:** `03b58ead1a5d7caae4ab76ec69d66cfa896da372`
- **Clang:** `23.1.2`

MPV, FFmpeg and the libraries incorporated into the executable retain their respective licence terms. The build project's own MIT licence applies to its own material, not to MPV or the libraries included in the executable.

The official GNU licence texts are available at:

- [GNU General Public License, version 2](https://www.gnu.org/licenses/old-licenses/gpl-2.0.html)
- [GNU General Public License, version 3](https://www.gnu.org/licenses/gpl-3.0.html)
- [GNU Lesser General Public License, version 2.1](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)
- [GNU Lesser General Public License, version 3](https://www.gnu.org/licenses/lgpl-3.0.html)

These links provide the licence texts; the licence applicable to an individual component must be determined from that component's own source and distribution terms.

## 3. Newtonsoft.Json

- **Version:** `13.0.5-beta1`
- **Licence:** MIT License
- **Copyright:** Copyright (c) 2007 James Newton-King
- **Project:** https://github.com/JamesNK/Newtonsoft.Json

The following notice is reproduced from the package's licence:

```text
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## 4. System.ValueTuple

The development package inventory identifies System.ValueTuple version `4.5.0`, which provides tuple functionality for .NET applications.

- **Licence:** MIT License
- **Copyright:** .NET Foundation and Contributors

The package's licence includes the following notice:

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

The package also contains a `THIRD-PARTY-NOTICES.TXT` with additional notices, including references to Intel Slicing-by-8 and Unicode data terms.

## 5. Microsoft .NET packages

The project and its development environment use Microsoft .NET packages, including packages associated with asynchronous operations and compatibility support.

The licence terms supplied with each Microsoft package govern that package and any assemblies distributed with it. Microsoft's terms are separate from the project's MIT License.

The Playnite SDK is used for extension development. Playnite provides its SDK as part of its own platform; the project's licence does not relicense Playnite or its SDK.

## 6. Playnite

- **Website:** https://playnite.link/
- **Source repository:** https://github.com/JosefNemec/Playnite

Zino's Media Center is an extension developed for Playnite. Playnite and its associated software remain the property of their respective rights holders and are subject to their own licence terms.

## 7. Stremio and compatible add-ons

Zino's Media Center integrates with compatible Stremio add-ons to obtain catalogue information and playback sources. Depending on the user's configuration, these integrations may include Cinemeta, Torrentio, Comet, AnimeFLV, OpenSubtitles and other compatible providers.

These integrations refer to external services and add-ons, not necessarily software distributed with Zino's Media Center. Each add-on remains subject to its own licence, terms and privacy practices.

- [Stremio](https://www.stremio.com/)

## 8. External services

The application may interact with external services, depending on the features used and the user's configuration. These may include:

- **Kitsu** — https://kitsu.io/
- **Real-Debrid** — https://real-debrid.com/
- **OpenSubtitles** — https://www.opensubtitles.com/

These services are independently operated. Their names are included to identify integrations and compatibility, and do not imply affiliation, sponsorship or endorsement.

Users are responsible for reviewing the terms and privacy policies of the services and add-ons they choose to configure.

## 9. Licence scope and disclaimer

The MIT License applies only to the original Zino's Media Center material covered by that licence, to the extent the project owner has the right to license it. Third-party software, libraries, media players and external services retain their own applicable rights and terms.

All third-party software is provided subject to its respective licence conditions. Copyright notices and licence texts applicable to distributed components must be retained in accordance with those terms.

Zino's Media Center is an independent community project and is not an official product of or endorsed by Playnite, MPV, Stremio, Real-Debrid, Kitsu, OpenSubtitles or any add-on operator.
