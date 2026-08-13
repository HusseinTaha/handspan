# Android Explorer

A desktop file manager and photo gallery for Android devices, over ADB — for **Windows and
macOS**. Built to replace Windows' MTP interface (slow, flaky, poor file-operation semantics)
and to fill the gap on macOS, where Google's Android File Transfer is abandoned.

No MTP. No cloud. No telemetry. No root.

**Status: working.** Browsing, transfers with resume, the gallery, search and storage
analysis are all implemented and have been exercised against a real phone. See
[what's done and what isn't](#whats-done-and-what-isnt).

| | |
|---|---|
| Transport | ADB over USB (primary), ADB over Wi-Fi (optional) |
| Built with | Avalonia 12 · .NET 10 · SQLite · SkiaSharp — all MIT/Apache |
| Platforms | Windows 10/11 (x64, arm64), macOS 12+ (Intel, Apple Silicon) |
| Licence | MIT |

## Why it's fast

It talks the documented ADB **host and sync protocol** directly over a local TCP socket,
rather than shelling out to `adb.exe` and parsing text. Three consequences do most of the
work:

- **Structured listings.** `LIS2`/`STA2` return mode, size and mtime as binary records, so
  filenames with spaces, quotes, newlines, emoji and RTL text are safe by construction. There
  is no `ls -la` parsing anywhere in this codebase.
- **Range reads.** A seekable stream over a remote file means a photo thumbnail costs the
  first ~128 KB of the file, not all 5 MB of it. That is the difference between a gallery that
  feels instant and one that doesn't.
- **1 MiB-aligned resume.** Interrupted transfers restart from the last whole megabyte using
  only baseline `dd`/`cat` semantics, so it works on toybox as well as busybox.

### Measured on hardware

Samsung Galaxy S24 Ultra (SM-S928B), Android 16, USB 3:

| | |
|---|---|
| Pull throughput | **38.0–38.8 MB/s** (195 MB in ~5 s) |
| Index crawl of DCIM | 7,710 entries in 2.6 s |
| Search across that index | 500 matches in **21 ms** |
| Storage analysis | 7,693 files / 85.7 GB categorized |
| Resumed upload and download | byte-identical, SHA-256 verified on the device |

## What it does

**Explorer** — fast virtualized browsing of accessible storage, instant re-open from a SQLite
directory cache, breadcrumbs, multi-select across files *and* folders, copy/cut/paste,
rename, delete, mkdir, drag & drop in from Explorer/Finder, favourites and Quick Access, a
properties dialog with EXIF.

**Transfers** — a real transfer manager: queued/active/completed/failed, per-file progress
with speed and ETA, size-classified parallelism, automatic retry with backoff, conflict
resolution (replace/skip/rename/compare, apply-to-all), optional SHA-256 verification, and a
**journal in SQLite so an interrupted queue survives an app crash**, not merely a cable pull.
Folder trees keep their structure on the way out.

**Gallery** — a date-grouped timeline built from a media index, not a folder view. Thumbnails
come from embedded EXIF previews read out of a bounded header range. Virtual albums by
directory (Camera, Screenshots, WhatsApp, …) with duplicate names disambiguated by their
parents. Multi-select with one-click transfer of everything selected. Image viewer with zoom,
pan and rotate.

**Search & storage** — FTS5 over the file index with `remove_diacritics`, so Arabic and CJK
search correctly; filters by type, size and date; storage breakdown by category and
directory; largest-files drill-down; duplicate detection that escalates from size to filename
to partial hash before it ever hashes a whole file.

**Backup** — incremental camera backup to the PC with a high-water mark that never moves
backwards, filing into `yyyy/yyyy-MM`.

**Multiple devices** — concurrent sessions with a device switcher. Every model and cache key
carries the device id, so two phones can never share a cache entry.

## Privacy

Everything is local. There is no network call in the core path, no analytics, and no account.
Logs are scrubbed by default — "Transfer started", never a filename, path, or EXIF GPS
coordinate. A photo's *location presence* is shown in the properties dialog immediately,
because that is what you need to know before sharing it, but the coordinates themselves are
only read when you click to reveal them, and are never written to the index or the log.

The app never attempts to bypass ADB authorization, and never asks for root.

## Building and running

Requires only the **.NET 10 SDK** — no workloads, no Visual Studio, no Android SDK.

```sh
dotnet build                                  # 11 projects
dotnet test                                   # 340 tests
dotnet run --project src/AndroidExplorer.App  # launch
```

The built executable is at
`src/AndroidExplorer.App/bin/Debug/net10.0/AndroidExplorer.exe` (`AndroidExplorer` on macOS)
and can be launched directly — faster than `dotnet run` after the first build.

### Publishing

```powershell
./build/publish.ps1 -Runtime all -Version 0.5.0
./build/make-app-bundle.ps1 -PublishDir artifacts/osx-arm64 -Suffix arm64
```

Builds are self-contained and single-file, so a user needs no .NET install. Trimming is
deliberately off: Avalonia resolves XAML types by reflection and the trimmer removes them
silently, producing a build that publishes cleanly and then fails at runtime.

macOS bundles are assembled on Windows, so the final `chmod +x` and quarantine-clearing step
runs on the Mac itself — `finish-macos-build.sh` is generated next to the bundle for that.

### Getting adb

The app searches, in order: a bundled copy, `PATH`, `ANDROID_HOME` / `ANDROID_SDK_ROOT`, the
usual SDK locations per OS, then the path configured in settings. If it reports "Not found",
it offers a one-click download of Google's official platform-tools — with consent, never
silently — or you can install it yourself:

| | |
|---|---|
| Windows | `winget install Google.PlatformTools` |
| macOS | `brew install --cask android-platform-tools` |
| Official zip | [Windows](https://dl.google.com/android/repository/platform-tools-latest-windows.zip) · [macOS](https://dl.google.com/android/repository/platform-tools-latest-darwin.zip) |
| Android Studio | Already included and found automatically |

### Application data

Settings, the transfer journal, caches and logs live in `%LOCALAPPDATA%\AndroidExplorer` on
Windows and `~/Library/Application Support/AndroidExplorer` on macOS. Deleting the folder
resets the app completely.

## Requirements on the phone

USB debugging must be enabled and this computer authorized:

1. **Settings → About phone**, tap **Build number** seven times. On Samsung it's under
   *About phone → Software information*.
2. **Settings → System → Developer options → USB debugging**, turn it on.
3. Connect the cable. The phone shows **"Allow USB debugging?"** — tick *Always allow from
   this computer* and tap **Allow**. If no prompt appears, unlock the phone and replug.
4. Confirm with `adb devices`: the device should read `device`, not `unauthorized`.

The app walks you through this on first run, and renders `unauthorized` as guidance rather
than an error.

Tip: if the device keeps dropping out, turn on **Developer options → Stay awake** and disable
USB selective suspend on the host.

## What's done and what isn't

Phase-by-phase status lives in [docs/plan/README.md](docs/plan/README.md). In short:

**Done and hardware-verified** — ADB transport, hotplug detection, structured listing, device
probe, all file operations, the transfer manager with resume in both directions, tiered
thumbnail extraction, the gallery, FTS5 search, storage analysis, multi-device sessions,
wireless pairing, publish scripts for four runtimes.

**Not done yet:**

| | |
|---|---|
| Video thumbnails, HEIC/AVIF decoding, in-app video player | Needs ffmpeg/LibVLC (LGPL, dynamic linking) bundled — designed for, not yet wired |
| Android companion app (MediaStore queries, device-generated thumbnails, `FileObserver` push updates) | Needs Gradle + Android SDK + JDK 17. Purely additive — nothing depends on it |
| Native promise-based drag-*out* (`CFSTR_FILEDESCRIPTORW` / `NSFilePromiseProvider`) | Today dragging out stages files to the cache first, which works but copies eagerly |
| Signed installers, notarized DMG, auto-update | Needs an Authenticode certificate and an Apple Developer ID |
| Recent virtual folder, device-to-device copy | Straightforward, not yet built |

**Verified only on one phone.** Everything above was tested against a Galaxy S24 Ultra on
Windows. The macOS build compiles but has not been run on a Mac. If you try it on other
hardware, a note in the issues is genuinely useful — the compatibility matrix in
`docs/plan/09-testing.md` is waiting to be filled in.

## Documentation

- **[docs/plan/](docs/plan/)** — the implementation plan and current status, phase by phase.
- **[docs/plan/00-architecture.md](docs/plan/00-architecture.md)** — layering, decisions,
  domain model.
- **[docs/plan/FEATURES.md](docs/plan/FEATURES.md)** — every feature, its phase, and the spec
  section behind it.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** — how to work on it, including the rules that exist
  because breaking them caused specific bugs.
- **docs/notes.txt** — the original 98-section product and technical specification, kept
  verbatim. `§n` references throughout the docs point into it.

## Licence

MIT — see [LICENSE](LICENSE). Third-party components and their licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); all are permissive, and none are copyleft.

Android is a trademark of Google LLC. This project is independent and not affiliated with,
endorsed by, or sponsored by Google.
