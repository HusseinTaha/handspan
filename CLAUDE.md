# Handspan — conventions

Cross-platform (Windows + macOS) Avalonia desktop app that manages Android devices over
ADB. Read `docs/plan/README.md` before starting work; `docs/notes.txt` is the product spec
and `§n` references throughout the docs point into it.

## Hard rules

Most come from the spec, and all of them exist because breaking them causes a specific, known
bug. Rule 11 is the exception to the provenance: it came from an artifact that was minutes from
being published broken.

1. **Never run an ADB operation on the UI thread** (§46). Everything is async with a
   `CancellationToken` (§47).
2. **Android paths are `DevicePath`, never `string`, never a Windows path** (§75). If you
   find yourself writing `Path.Combine` on a device path, stop.
3. **Never parse `ls -la`** (§73). Use the sync protocol's `LIS2`/`LIST` and `STA2`/`STAT`.
   Filenames contain spaces, quotes, newlines, emoji and RTL text.
4. **All user input reaching a shell goes through `ShellQuote`** (§71). Prefer `sync:` and
   `exec:` over `shell:` entirely.
5. **Every model and cache key carries `DeviceId`** (§39). Two phones must never share a
   cache entry.
6. **No `#if WINDOWS` and no P/Invoke outside `src/Handspan.App/Platform/*`.**
   Platform behavior goes behind a Core interface with one implementation per OS.
7. **Never pull a full-size image to draw a thumbnail** (§94). Use the tiered extraction in
   `docs/plan/04-gallery.md`.
8. **Write to `.part`, then `mv` into place.** A partial file must never look complete, and
   a destination is never overwritten without inspecting it first (§13).
9. **Logs must not contain file paths, filenames or EXIF GPS** unless verbose diagnostics is
   explicitly enabled (§43).
10. **Never try to circumvent Android's security model** — no root exploits, no bypassing
    the ADB authorization prompt (§17, §41, §78).
11. **Build release archives with `tar`, never `Compress-Archive` or `ZipFile`.** On
    PowerShell 5.1 both write entry names with **backslash** separators. Windows extractors
    tolerate it; macOS `unzip` does not, and since a `.app` is a directory, the bundle never
    reassembles — a download that looks fine and does nothing. No test on a Windows machine can
    catch this, so assert on the entry names instead: a release archive must contain zero
    entries matching `*\*`.

## Layout

```
src/Handspan.Core        models, interfaces, exceptions — zero platform deps
src/Handspan.Adb         socket client, sync protocol, IDeviceFileSystem
src/Handspan.Services    DeviceManager, TransferManager, Settings, Cache
src/Handspan.Data        SQLite: dir cache, transfer journal, index
src/Handspan.Media       thumbnails, decoding, streaming, metadata
src/Handspan.Search      indexer, search, storage analysis
src/Handspan.App         Avalonia UI + Platform/{Windows,MacOS}
tests/*                         xUnit; FakeAdbServer lives in Handspan.Adb.Tests
companion/                      Android companion app (phase 7, Kotlin)
```

Dependency direction is strictly downward: `App → Services → Adb → Core`. `Core` references
nothing. **`Startup.cs` is the only file in `App` allowed to name a concrete type from a lower
layer** — it is the composition root. Every other file in `App` depends solely on interfaces from
`Handspan.Core`, reached through `IDeviceSession`.

## Testing without a phone

`FakeAdbServer` (in `tests/Handspan.Adb.Tests`) is a loopback TCP server that speaks
the real adb host + sync protocol against an in-memory filesystem, with injectable
disconnects, failures, slow streams and feature downgrades. Most behavior — including
resume — is testable with no hardware. Use it rather than mocking `IAdbClient`.

## Packaging

`build/publish.ps1` is the only entry point. `-Portable` (Windows only) calls
`build/make-portable.ps1`; the macOS runtimes get a `.app` from `build/make-app-bundle.ps1`.
Details and the reasoning live in `docs/plan/08-packaging.md`.

```powershell
./build/publish.ps1 -Runtime win-x64 -Portable -Version 0.6.0
./build/publish.ps1 -Runtime osx-arm64 -Version 0.6.0
```

- **Portable mode is one seam.** Everything writable — settings, both SQLite databases, the
  thumbnail cache, logs, a downloaded adb — resolves through
  `IShellIntegration.GetAppDataFolder()`. `PortableMode` redirects that to a `Data` folder
  beside the executable when a `Handspan.portable` marker file sits next to it. **Anything new
  that writes to disk must go through that call**, or it will escape to the user's profile and
  silently break the portable build's only promise.
- A marker present but the folder unwritable falls back to per-user data rather than refusing
  to start, and reports it. Probe with an actual write: `CreateDirectory` succeeding proves
  nothing about writability.
- Trimming stays **off** — Avalonia resolves XAML types by reflection and the trimmer removes
  them silently, so the app publishes cleanly and then dies at runtime.
- `IncludeNativeLibrariesForSelfExtract` is **off for portable builds only**. It unpacks ~40 MB
  into `%TEMP%` and leaves it there, which contradicts the whole point.
- The macOS bundles are cross-compiled on Windows and cannot be finished there: no executable
  bit, no signature. `finish-macos-build.sh` ships inside the archive to do both on the Mac.

### PowerShell 5.1 traps in these scripts

The build scripts run under Windows PowerShell 5.1 (.NET Framework), which fails in ways
PowerShell 7 does not. All three of these have already cost real debugging time here:

- `Compress-Archive` and `ZipFile` write backslash separators — see hard rule 11.
- `Get-Content -Raw` decodes a BOM-less UTF-8 file as ANSI, and the string it returns carries
  `PSPath`/`PSProvider` properties that `ConvertTo-Json` serializes into your payload. Use
  `[IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)`.
- A `.ps1` saved without a BOM is itself read as ANSI, so a non-ASCII literal in a script
  breaks the parse. Keep build scripts ASCII-only and use `[char]0x2014` when you need one.
- `Copy-Item` preserves the source timestamp, so restoring a file can leave it *older* than the
  build output and MSBuild will skip recompiling. A suspiciously fast build is the tell.

## Stack notes

- .NET 10 (current LTS). Avalonia 12.1.1, MIT. CommunityToolkit.Mvvm for observables.
  Do **not** add `Avalonia.Diagnostics` — it stopped at 11.3.x (DevTools is in the core package in
  v12) and mixing majors breaks the build.
- No paid dependency is acceptable in this project.
- ffmpeg and LibVLCSharp (phase 4) are LGPL — dynamic linking only, ship the license texts,
  no GPL-only ffmpeg components.
