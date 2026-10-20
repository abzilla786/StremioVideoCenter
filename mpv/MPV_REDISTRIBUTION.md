# mpv redistribution

| | |
|---|---|
| **Covers** | The `mpv.exe` stored under `mpv/` that the extension starts as a separate program, and what is needed to redistribute it. |
| **Last reviewed** | 2026-10-19. The folder was inspected read-only. On 2026-10-19 the publisher's release archives were downloaded into a scratch area (not the repository) and compared with the bundled `mpv.exe` (section 1.2). Nothing was run or packaged. |
| **Release status** | **Open.** The origin of `mpv.exe` is verified (section 1.2). Two items remain: the notices of the statically linked libraries and the source location, which depend on the build run's job summary and logs (GitHub sign-in needed; kept about 30 days). See [section 6](#6-open-items). |
| **Not** | Legal advice. |

The playback design (standalone `mpv.exe`, JSON IPC, `--wid` embedding) is not affected by this review.

**Evidence labels.** **Confirmed**: read from a file or document. **Evidence**: derived from strings inside the binary (heuristic, not a manifest). **Owner-supplied**: stated by the project owner and not independently checked. **Expected**: general knowledge, not checked against this exact build. **Open**: needs a decision or more evidence.

---

## 1. The bundled build

The owner replaced the `mpv` folder on 2026-10-19 with the 64-bit build from the release below. This section separates what the local files show from what comes from the release page.

### 1.1 What the local files show (confirmed)

Read-only inspection of the folder on the owner's computer; the files were staged for reading and not modified.

| Item | Value |
|---|---|
| File | `mpv/mpv.exe`, 64-bit PE (machine `0x8664`) |
| Size | **121,949,184 bytes** |
| SHA-256 | **`0c7894d232438ed1d3f59884d438918fd5401eea9efed014dae6eaeec6277b80`** |
| Version string in the binary | **`mpv v0.41.0-1224-g6a262d11f`** (1224 commits after the v0.41.0 tag, commit `6a262d11f`) |
| FFmpeg version string in the binary | `N-127652-gffb15c94c` |
| PE build timestamp; file time | 2026-10-18 15:08:27 UTC; 2026-10-18 15:13:20 UTC |
| Build tree paths in the binary | `/__w/mpv-winbuild/mpv-winbuild/mpv-winbuild-cmake/...` (a GitHub Actions container build of the build project, using clang/LLVM runtime sources) |
| Build configuration embedded in the binary | meson options including `-Doptimization=3 -Db_lto=true -Db_lto_mode=thin -Dlibmpv=true -Dlibarchive=enabled -Dlibbluray=enabled -Ddvdnav=enabled -Drubberband=enabled -Dvapoursynth=enabled ...`. It does **not** contain `-Dgpl=false`, so mpv's default (GPL) applies. No `-march` or CPU-level flag appears. |
| Enabled features reported | `gpl`, `ffmpeg`, `libass`, `libplacebo`, `libbluray`, `dvdnav`, `rubberband`, `luajit`, `libarchive`, `libcurl`, `lcms2`, `openal`, `shaderc`, `spirv-cross`, `sixel`, `subrandr`, `uchardet`, `vapoursynth`, `vulkan`, `zimg` and others; no `lgpl` feature |
| Bundled codec | an x264 string (`x264 - core ... 2003-2025`) is present, so x264 is built in |
| Imported DLLs | Windows system DLLs only (for example `KERNEL32`, `USER32`, `GDI32`, `d2d1`, `DWrite`, `OPENGL32`, `vulkan-1`, `api-ms-win-crt-*`); no delay-load imports; **no non-system DLL is imported**. `vulkan-1.dll` comes from the system's Vulkan loader or graphics driver. This is static inspection of the import table; it does not show what mpv loads at run time and is not a runtime-compatibility claim. |

Other files in the folder (all dated 2026-10-18 15:13 UTC):

| File | Size | SHA-256 |
|---|---|---|
| `mpv.com` | 3,584 | `11ce88e007e0a5c3c69427795887395d85c3d0ccfb5fad1739084623fe250b5e` |
| `mpv-register.bat` | 202 | `e3c354d13bfe4ae2b9bafbd1b7609753c47f78a3a4989b082f34d5a938aa7a09` |
| `mpv-unregister.bat` | 206 | `d40f2f76e00e08287c39c15654e9350831e4418f1bd9619bc8dcff0dfb055e91` |
| `updater.bat` | 818 | `120fd67c7d0109eb01961a47c301f77da7f6da18b80a7b39c5f7ea4664817f7b` |
| `installer/updater.ps1` | 27,514 | `950b4f3086801df0f724e52eb999323ab65b5d0b3d2576c65d57be54c636e5c5` |
| `mpv/fonts.conf` | 2,703 | `f141c1b89b172d22f213531646c21e288f0ebf3ec46484698896e1b33c626756` |
| `doc/manual.pdf` | 1,043,512 | `e9f47c7ffb97f3f5a9c00a56e16537c75a00426ca1c60e59105342154442ada5` (PDF created 2026-10-18 15:09:04 UTC) |
| `doc/mpbindings.png` | 190,376 | `d27b24e45be912eb14935c3540147a65ad3be513c3fe444f010ad543ae4cc352` |

The prepared licence files `LICENSE-mpv.txt` and `licenses/` (four GPL and LGPL texts) sit in the same folder; they were not part of the downloaded build. `LICENSE-mpv.txt` was updated on 2026-10-19 with the owner's explicit authorisation (it is the only file in the folder that was changed); the other files were not touched.

### 1.2 Consistency with the supplied release

**Release (owner-supplied): <https://github.com/zhongfly/mpv-winbuild/releases/tag/2026-10-18-6a262d11f6>.** Information below marked *release page* was read from the release page and its GitHub API listing on 2026-10-18 (read-only web requests; nothing was downloaded).

| Release page says | Local binary shows | Result |
|---|---|---|
| mpv commit `6a262d11f63733d37e3ee6d5fafcb1be1ef83952` | version string `...-g6a262d11f` | **Consistent.** The binary's 9-character commit abbreviation is a prefix of the release's full commit. |
| FFmpeg short commit `ffb15c94c` (from the `ffmpeg-*-git-ffb15c94c.7z` asset names) | FFmpeg version `N-127652-gffb15c94c` | **Consistent.** |
| Built and published 2026-10-18 (15:28 UTC; published 15:28:50Z) | PE timestamp 15:08:27, files 15:13:20, manual 15:09:04 (all 2026-10-18 UTC) | **Consistent.** The files predate publication by about 20 minutes, the expected order; the release page's "Build Time" label is 15:28. |
| 64-bit assets `mpv-x86_64-...` and `mpv-x86_64-v3-...` | 64-bit x86-64 PE | **Consistent** with both; the variant is not determined (below). |
| Compiler: clang; build project `mpv-winbuild` | clang/LLVM runtime paths and `/__w/mpv-winbuild/...` build tree | **Consistent.** |
| The `mpv-<arch>` archives carry no `lgpl` label; `lgpl` variants exist only for `ffmpeg-*` and `mpv-dev-*` | `gpl` feature present, no `lgpl` feature | **Consistent** with a GPL build. |

**Conclusion.** The replacement is **consistent with** release `2026-10-18-6a262d11f6`: the mpv commit, the FFmpeg commit, the build date, the architecture and the GPL status all line up, and no trace of the previous build (`654e9382c`) remains in the binary. The previous mismatch is resolved.

**Verified on 2026-10-19 (supersedes the earlier "not verified" result).**

| Check | Result |
|---|---|
| Publisher's `sha256.txt` (downloaded from the release) lists `mpv-x86_64-20261018-git-6a262d11f6.7z` as `ea8c0decf5b22fb3dfaf580bc95e70c301cb966d3538637c4245f11eb0163e2b` | The same digest appears on the GitHub Actions run page (run 39138561860, artifact list) |
| Downloaded `mpv-x86_64-20261018-git-6a262d11f6.7z`: 33,597,985 bytes | SHA-256 `ea8c0dec…3e2b` — **matches the publisher's value** |
| Downloaded `mpv-x86_64-v3-20261018-git-6a262d11f6.7z`: 34,866,884 bytes | SHA-256 `5957bf33…c6c5` — matches the publisher's value |
| `mpv.exe` extracted from the baseline `x86_64` archive | **Byte-identical** to the bundled `mpv/mpv.exe` (`cmp`; SHA-256 `0c7894d2…7b80`) |
| `mpv.exe` extracted from the `v3` archive | Different file (SHA-256 `b7f067ca…9ee0`) — **not** the source of the bundled file |
| `mpv.com` and `mpv/fonts.conf` from the baseline archive | Identical to the bundled files |

**Conclusion.** The bundled `mpv.exe` is the file inside the publisher's baseline `x86_64` archive. The asset variant is therefore **determined: baseline x86-64, not v3**, so no CPU requirement beyond x86-64 needs stating.

**What this still does not prove.**

- **The owner's own download.** The archive was fetched again from the publisher on 2026-10-19; whether the owner's copy came from it is not recorded, and the owner's download date is unknown. The identical file contents make the origin certain; they do not record when the owner downloaded it.
- **Build reproducibility and attestation.** The build workflow runs `actions/attest` on the release archives. This review did not verify an attestation, and the binary was not rebuilt.
- **The archive carries no licence files.** It holds only `mpv.exe`, `mpv.com`, helper scripts, two documentation files and `mpv/fonts.conf`; no notice for any bundled library.

**Facts read from the build workflow** (public page `.github/workflows/mpv.yml`, read 2026-10-19): the toolchain repository `shinchiro/mpv-winbuild-cmake` is cloned at its default branch, not pinned; the build downloads and applies an mpv patch before compiling ("Download mpv patch", "Apply mpv pr releated patch"); the release uploads binaries only, **no source archive**; per-package commits are written to the job summary ("Packages Version" table) and logs, which need a GitHub sign-in; the project README says the last 30 days of builds are retained. The source of the bundled binary is therefore mpv `6a262d11f` **plus the publisher's patches** plus the library commits in the run's summary.

**Other facts read from the release page** (about the release, not the local file): build run <https://github.com/zhongfly/mpv-winbuild/actions/runs/39138561860>; build-project commit `65cc641a906c10239b925da506ab48ae3f2cfa13`; the page lists no package versions or commits other than mpv's (FFmpeg's appears only in asset names).

### 1.3 Remnants of the old build and files that must not ship

- **No remnants of the old build were found.** Every file in the folder is dated 2026-10-18 (the old build's files were dated 2026-08-25, and its `mpv.exe` 2026-09-11); the binary contains no trace of `654e9382c`; the file set and names are unchanged and `doc/manual.pdf` has a new size (old 1,039,490, new 1,043,512). The other helper files have the same sizes as before, so for them the dates are the only evidence. Static comparison only.
- **Missing files: none identified.** `mpv.exe` imports no non-system DLLs and uses no delay-load imports, so no companion DLL is expected. This is not a runtime-compatibility claim.
- **Files that must not ship** (present in the folder and copied to the build output by the project): `mpv.com`; `mpv-register.bat` and `mpv-unregister.bat` (they run `mpv --register` / `--unregister`, which change the Windows registry); `updater.bat` and `installer/updater.ps1` (the updater downloads and runs software and queries `zhongfly/mpv-winbuild` releases); `doc/manual.pdf` and `doc/mpbindings.png`. See [section 5](#5-files-in-mpv-and-how-packaging-should-treat-them).
- **`LICENSE-mpv.txt` was out of date and has been corrected (2026-10-19, owner-authorised).** It showed the previous build's version, size and hash. It now carries this build's values (section 1.1), the release URL, the build project's MIT licence distinction, and `TODO-RELEASE-BLOCKER` markers for what is genuinely unresolved (library notices; source location). The archive-hash marker was resolved on 2026-10-19 (section 1.2). The change is in the working tree only.

### 1.4 Handling of this binary in the repository
`mpv.exe` is stored through Git LFS (`.gitattributes`), and earlier commits contain it. A public repository that serves the file is itself a distribution of it and would carry the same obligations (see section 4). Whether to keep it in the repository or to fetch a hash-pinned copy at packaging time is **undecided**. Removing it from earlier history would need a history rewrite, which has not been done and needs the owner's explicit instruction.

### 1.5 The build project's licence

The page <https://github.com/zhongfly/mpv-winbuild/blob/main/LICENSE> contains the **MIT License, `Copyright (c) 2021 zhongfly`**. The file does not say what it covers. It is the licence of the build project's own files (scripts and configuration) as published in that repository. It is **not** the licence of the compiled `mpv.exe` or of any library inside it, and it is not used as the mpv licence in `mpv/LICENSE-mpv.txt`. If a source bundle includes the build project's scripts, their MIT notice must be kept with them.

---

## 2. Licences that apply to the shipped `mpv.exe`

**Which licence text was prepared, and why.** The licence version is taken from what the binary and the build documentation show, and the selection is provisional until the build's own notices are collected.

- **mpv itself.** Upstream states that mpv as a whole is GPLv2 or later by default, and LGPLv2.1 or later only when built with `-Dgpl=false`. Some files carry more permissive licences and the manual stays GPLv2+. *Confirmed* (upstream `Copyright` file and README for v0.41.0, read in an earlier review).
- **This build is GPL-enabled.** The binary lists the `gpl` feature and has no `lgpl` feature, its embedded build configuration does not contain `-Dgpl=false` (section 1.1), and the release's `mpv-<arch>` executable archives carry no `lgpl` label (section 1.2). An x264 string is present in the binary.
- **Linked libraries decide the combined result.** The upstream recipe repository `shinchiro/mpv-winbuild-cmake`, which the build project describes itself as based on, configures FFmpeg with `--enable-gpl --enable-version3` (read on `master` in an earlier review, which may differ from the commit that built this binary). FFmpeg's configuration flags were **not** found in the strings of the replacement `mpv.exe` either (only its version, `N-127652-gffb15c94c`), so this is not confirmed from the binary. The upstream recipe was read for a different checkout, not for this commit. With GPL and version-3 FFmpeg, plus x264, x265, rubberband and libdvdnav in the same static binary, the combined `mpv.exe` is expected to be a GPL work, most likely **GPLv3 or later** as a whole. Parts keep their own licences ([`LICENSING_INVENTORY.md`](LICENSING_INVENTORY.md#3-libraries-inside-mpvexe-expected-licences)). *Expected.*
- **Texts prepared** in `mpv/licenses/`: `GPL-3.0.txt` (expected effective licence), `GPL-2.0.txt` (mpv's upstream baseline and GPLv2 components), `LGPL-3.0.txt` and `LGPL-2.1.txt` (LGPL libraries such as libbluray, libplacebo, libssh and OpenAL, expected). They are unmodified copies of the standard texts taken from the licence directory of a standard Linux distribution; they were not compared against the FSF originals, which could not be fetched. Reproduction of the permissive-licence notices (BSD, MIT, Apache-2.0, MPL-2.0, zlib and others) is **not** done: it needs the build's package list.
- **The build project's own MIT licence is not an mpv licence** (section 1.5).
- **Patents and codecs.** The binary contains encoders and decoders for formats such as H.264 and HEVC. Copyright licences do not grant patent rights. This has not been assessed (**Open**).

---

## 3. Is shipping it next to the extension "mere aggregation"?

This is **not assumed**. The facts of the arrangement and the licence text that bears on them are set out below. The result is an assessment for a qualified reviewer, not a conclusion.

### Facts of the arrangement (confirmed from source)
1. **Separate executable, started as a process.** `Services/MpvProcessService.cs` starts `mpv.exe` with `System.Diagnostics.Process` (`UseShellExecute = false`). `libmpv` is not referenced, loaded or linked, and nothing from mpv is compiled into the extension.
2. **Communication uses documented, public mechanisms:** command-line options, mpv's JSON IPC over a named pipe (`--input-ipc-server=\\.\pipe\...`) and a native window handle (`--wid=<hwnd>`). The extension sends mpv's documented commands and reads documented property and event messages; it does not exchange internal data structures with mpv.
3. **Replaceable.** The extension looks for `mpv\mpv.exe` in its own folder first and falls back to other locations (`FindMpvExecutable`), so another compatible mpv build can be used.
4. **No mpv code in the repository.** A pattern search of the source found no copied or ported mpv code (heuristic only).
5. **Distribution medium.** The plan is one `.pext` package (a ZIP) containing the extension, its dependencies and the `mpv/` folder.

### What the licence text says
- **GPLv2 section 2** (read): "mere aggregation" of another work not based on the Program with the Program on a volume of storage or distribution medium does not bring that other work under the licence.
- **GPLv3 section 5 and the FSF GPL FAQ** on aggregates and on pipes, sockets and command-line arguments could not be loaded in the earlier review and have **not** been quoted or re-read. From general knowledge the FAQ treats separate programs that communicate over pipes, sockets or command lines as normally separate unless the communication is intimate enough to make them one program. This must be checked against the live text.

### Assessment (the reviewer's reading, for qualified review)
- The facts above (process boundary, public interface, no linking, replaceable player) are consistent with the extension and `mpv.exe` being separate programs distributed together. This is **likely, not established**, and no qualified person has reviewed it.
- A reviewer may weigh the other way that the extension cannot play anything without mpv, is designed around one player, and embeds mpv's window in its own. None of these is the "intimate data exchange" criterion, but no ruling settles the question.
- **Aggregation, if it holds, protects only the extension's licence.** Whoever hands out `mpv.exe` must still meet the GPL and each bundled library's licence for that file.
- If aggregation were rejected, MIT code can be included in a GPL combined work, so that risk is manageable for the extension's own code. It is **not** manageable for `Microsoft.Bcl.Async`, whose licence forbids distribution that subjects it to an "Excluded License" ([`LICENSING_INVENTORY.md`](LICENSING_INVENTORY.md#the-microsoftbclasync-problem)). That is a further reason to resolve that dependency.

---

## 4. What must accompany a distributed package

Expected minimum, based on GPLv2 section 3 (read) and common practice for GPLv3. Each item still has to be confirmed.

1. **Licence texts** for the mpv build: the applicable GPL text(s), the LGPL texts for LGPL parts, and every permissive licence that requires reproduction (BSD, MIT, Apache-2.0, MPL-2.0, zlib and similar), in a `licenses/` folder referenced from `THIRD_PARTY_NOTICES.md`.
2. **Copyright and attribution notices** for each linked library. The distributor's release does not appear to include them, so they would have to be assembled from the build's package list and upstream.
3. **A clear statement of the bundled build:** version, SHA-256, builder (mpv-winbuild project) and the release. `mpv/LICENSE-mpv.txt` carries this statement; it was corrected to the current `mpv.exe` on 2026-10-19 and must be kept in step with the shipped file (re-check the size and hash at packaging time).
4. **Corresponding source for the GPL/LGPL components.** For a package offered as a download, the route that fits is GPLv3 section 6(d), read in the prepared `GPL-3.0.txt`: offer the binary from a designated place and offer equivalent access to the corresponding source in the same way through the same place at no further charge; the source may sit on a different server if clear directions are kept next to the binary, and **the owner stays obligated to keep it available for as long as needed**. GPLv2 section 3 offers the equivalent options (including a written offer valid for at least three years). Whether this route is enough, and for which parts, is for a qualified reviewer. Concretely, the source set for the shipped binary consists of:
   - mpv at commit `6a262d11f63733d37e3ee6d5fafcb1be1ef83952` (the binary reports `g6a262d11f`; matches the release);
   - FFmpeg at commit `ffb15c94c` (the binary reports `N-127652-gffb15c94c`; matches the release's asset names; full hash to be resolved);
   - every other statically linked library at the version or commit used (not on the release page; to be read from the build run and the build project's package definitions at build-project commit `65cc641a906c10239b925da506ab48ae3f2cfa13`, which have not yet been read);
   - the build scripts at that commit (MIT, see section 1.5).

   The practical route is a source archive published as an asset on the same release page as the package, with its location written into `mpv/LICENSE-mpv.txt`. Linking only to the third-party project is weaker: it does not by itself supply the exact source of every component, it is outside the owner's control, and its README says it keeps only a limited number of recent builds (about 30 days, as read in an earlier review and not re-checked). The build run's logs may expire as well; if they hold the per-package commits, save them early.
5. **Do not strip or alter** licence and copyright notices in mpv or its libraries.
6. **Do not ship the helper files** that are not needed at runtime (section 5).
7. **Tell users how to replace mpv** with their own build.

---

## 5. Files in `mpv/` and how packaging should treat them

| File | Needed at runtime? | Packaging rule |
|---|---|---|
| `mpv.exe` | Yes | Include, with the obligations in section 4. |
| `mpv/fonts.conf` (project path `mpv\mpv\fonts.conf`) | Not established. It is fontconfig's default configuration; whether mpv reads it when started with `--no-config` is not verified | **Provisionally include.** Decided by the subtitle test with and without it ([`PACKAGING_PLAN.md`](PACKAGING_PLAN.md#34-the-fontsconf-decision), test G9). |
| `LICENSE-mpv.txt`, `licenses/*` (four files) | n/a (licence notices) | **Include**, in the package's `mpv\` folder. `LICENSE-mpv.txt` is releasable only when no `TODO-RELEASE-BLOCKER` marker remains. |
| `mpv.com` | No (console wrapper) | **Exclude.** |
| `doc/manual.pdf`, `doc/mpbindings.png` | No | **Exclude** (documentation, adds size). |
| `mpv-register.bat`, `mpv-unregister.bat` | No; they change the Windows registry | **Exclude.** |
| `updater.bat`, `installer/updater.ps1` | No; the updater downloads and runs software from the network (mpv builds, `yt-dlp`, 7-Zip) | **Exclude.** This also removes a way for the package to modify itself after install. |

`StremioVideoCenter.csproj` copies everything under `mpv\**` into the build output (a `Content` rule and the `CopyMpvRuntimeFiles` target), which now also brings the prepared licence files along. The exclusions above must therefore be applied when staging ([`PACKAGING_PLAN.md`](PACKAGING_PLAN.md)). No project file has been changed.

---

## 6. Open items

Items marked **(owner)** need your decision or action.

**Provenance**
- [x] **Archive verified** (2026-10-19, section 1.2): the bundled `mpv.exe` is byte-identical to the one in `mpv-x86_64-20261018-git-6a262d11f6.7z`, whose SHA-256 matches the publisher's list. Variant: baseline x86-64.
- [ ] **(owner, optional) Record the date of your own download.** Not needed to identify the file; it only completes the provenance record.
- [x] **Package version table received** (2026-10-19): 86 packages with the built commit of each are recorded in [`MPV_BUILD_COMPONENTS.md`](MPV_BUILD_COMPONENTS.md), with the expected licence and the obligations. FFmpeg's configuration (`--enable-gpl --enable-version3`, no `--enable-nonfree`) is confirmed from the publisher's FFmpeg build of the same release.
- [ ] **(owner) Capture the rest of the build summary before it expires (about 2026-11-17):** the `mpv-winbuild-cmake` commit, compiler and Rust versions, merged-PR list and the packages fetched as archives (not in the table you supplied). They are needed for the source archive.

**Licence files and source**
- [x] **`mpv/LICENSE-mpv.txt` identification updated** (2026-10-19, owner-authorised; the only mpv-folder file changed): `mpv.exe` 121,949,184 bytes, `v0.41.0-1224-g6a262d11f`, FFmpeg `N-127652-gffb15c94c`, SHA-256 `0c7894d2…7b80`, the release URL, and the MIT-versus-binary distinction. The archive-hash marker was removed on 2026-10-19 after verification (section 1.2). Two `TODO-RELEASE-BLOCKER` markers remain; each is removed only when its requirement is met: library notices (below) and the source location (below).
- [ ] **Collect library notices** into `mpv/THIRD_PARTY_LICENSES.txt` from the source trees ([`MPV_BUILD_COMPONENTS.md`](MPV_BUILD_COMPONENTS.md#4-simplest-practical-way-to-provide-the-source), step 6). The GPL and LGPL texts in `mpv/licenses/` are prepared; the others are not.
- [ ] **(owner) Publish the source archive** on the same release page as the package, as planned in [`MPV_BUILD_COMPONENTS.md`](MPV_BUILD_COMPONENTS.md#4-simplest-practical-way-to-provide-the-source), and write its location into `mpv/LICENSE-mpv.txt`.
- [ ] **(owner) Decide how the repository holds `mpv.exe`** (Git LFS, or fetched at packaging time) and whether older history is affected. The previous binary is in earlier commits.
- [ ] **Qualified review** of the aggregation reasoning and the patent position, or the owner's informed decision to proceed without it.
- [ ] **(owner) Approve the final arrangement.** A root `LICENSE` (MIT, own material, `Copyright (c) 2026 abdullah (abzilla786)`) is prepared and awaits approval; [`Licence.md`](../Licence.md) holds the notice and the third-party section.

**Packaging**
- [ ] **Apply the exclusions** in section 5 and inspect the real package ([`PACKAGING_PLAN.md`](PACKAGING_PLAN.md)).

None of this requires changing the playback design.
