# Android Explorer — conventions

Cross-platform (Windows + macOS) Avalonia desktop app that manages Android devices over
ADB. Read `docs/plan/README.md` before starting work; `docs/notes.txt` is the product spec
and `§n` references throughout the docs point into it.

## Hard rules

These come from the spec and exist because breaking them causes specific, known bugs.

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
6. **No `#if WINDOWS` and no P/Invoke outside `src/AndroidExplorer.App/Platform/*`.**
   Platform behavior goes behind a Core interface with one implementation per OS.
7. **Never pull a full-size image to draw a thumbnail** (§94). Use the tiered extraction in
   `docs/plan/04-gallery.md`.
8. **Write to `.part`, then `mv` into place.** A partial file must never look complete, and
   a destination is never overwritten without inspecting it first (§13).
9. **Logs must not contain file paths, filenames or EXIF GPS** unless verbose diagnostics is
   explicitly enabled (§43).
10. **Never try to circumvent Android's security model** — no root exploits, no bypassing
    the ADB authorization prompt (§17, §41, §78).

## Layout

```
src/AndroidExplorer.Core        models, interfaces, exceptions — zero platform deps
src/AndroidExplorer.Adb         socket client, sync protocol, IDeviceFileSystem
src/AndroidExplorer.Services    DeviceManager, TransferManager, Settings, Cache
src/AndroidExplorer.Data        SQLite: dir cache, transfer journal, index
src/AndroidExplorer.Media       thumbnails, decoding, streaming, metadata
src/AndroidExplorer.Search      indexer, search, storage analysis
src/AndroidExplorer.App         Avalonia UI + Platform/{Windows,MacOS}
tests/*                         xUnit; FakeAdbServer lives in AndroidExplorer.Adb.Tests
companion/                      Android companion app (phase 7, Kotlin)
```

Dependency direction is strictly downward: `App → Services → Adb → Core`. `Core` references
nothing. **`Startup.cs` is the only file in `App` allowed to name a concrete type from a lower
layer** — it is the composition root. Every other file in `App` depends solely on interfaces from
`AndroidExplorer.Core`, reached through `IDeviceSession`.

## Testing without a phone

`FakeAdbServer` (in `tests/AndroidExplorer.Adb.Tests`) is a loopback TCP server that speaks
the real adb host + sync protocol against an in-memory filesystem, with injectable
disconnects, failures, slow streams and feature downgrades. Most behavior — including
resume — is testable with no hardware. Use it rather than mocking `IAdbClient`.

## Stack notes

- .NET 10 (current LTS). Avalonia 12.1.1, MIT. CommunityToolkit.Mvvm for observables.
  Do **not** add `Avalonia.Diagnostics` — it stopped at 11.3.x (DevTools is in the core package in
  v12) and mixing majors breaks the build.
- No paid dependency is acceptable in this project.
- ffmpeg and LibVLCSharp (phase 4) are LGPL — dynamic linking only, ship the license texts,
  no GPL-only ffmpeg components.
