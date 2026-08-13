# Contributing

Thanks for looking. This project is MIT-licensed and contributions are welcome — bug reports
from other phones are the single most useful thing anyone can send, because everything here
has been verified against exactly one device so far.

## Getting set up

You need the **.NET 10 SDK** and nothing else. No workloads, no Visual Studio, no Android SDK.

```sh
dotnet build
dotnet test                                   # 347 tests, ~40 s
dotnet run --project src/Handspan.App
```

A phone is **not** required. `FakeAdbServer` (in `tests/Handspan.Adb.Tests`) is a
loopback TCP server that speaks the real ADB host and sync protocol against an in-memory
filesystem, with injectable disconnects, failures, slow streams and feature downgrades. Almost
all behaviour — including resume — is testable with no hardware. Prefer it over mocking
`IAdbClient`.

If a phone *is* attached, the live tests run automatically. Set
`HANDSPAN_NO_DEVICE_TESTS=1` to skip them.

Before opening a PR, also run the macOS cross-compile. It costs seconds and catches a
Windows-only API leaking into shared code — a real run needs a Mac, but this catches the
common mistake:

```sh
dotnet build -r osx-arm64
```

## Layout

```
src/Handspan.Core        models, interfaces, exceptions — zero platform deps
src/Handspan.Adb         socket client, sync protocol, IDeviceFileSystem
src/Handspan.Services    DeviceManager, TransferManager, Settings, Cache
src/Handspan.Data        SQLite: dir cache, transfer journal, index
src/Handspan.Media       thumbnails, decoding, streaming, metadata
src/Handspan.Search      indexer, search, storage analysis
src/Handspan.App         Avalonia UI + Platform/{Windows,MacOS}
tests/*                         xUnit
```

Dependency direction is strictly downward: `App → Services → Adb → Core`, and **Core
references nothing**. `Startup.cs` is the only file in `App` allowed to name a concrete type
from a lower layer — it is the composition root. Every other file in `App` depends solely on
interfaces from `Core`, reached through `IDeviceSession`.

A test calls the real `Startup.BuildServiceProvider()` with `ValidateOnBuild`, because a view
model gaining an unregistered dependency has crashed this app on launch twice.

## The rules that matter

These are not style preferences. Each one exists because breaking it caused a specific,
diagnosed bug. The full list with spec references is in [CLAUDE.md](CLAUDE.md).

1. **Never run an ADB operation on the UI thread.** Everything is async with a
   `CancellationToken`.
2. **Android paths are `DevicePath`, never `string`, never a Windows path.** If you find
   yourself writing `Path.Combine` on a device path, stop.
3. **Never parse `ls -la`.** Use the sync protocol's `LIS2`/`LIST` and `STA2`/`STAT`.
   Filenames contain spaces, quotes, newlines, emoji and RTL text.
4. **All user input reaching a shell goes through `ShellQuote`.** Prefer `sync:` and `exec:`
   over `shell:` entirely.
5. **Every model and cache key carries `DeviceId`.** Two phones must never share a cache entry.
6. **No `#if WINDOWS` and no P/Invoke outside `src/Handspan.App/Platform/*`.** Platform
   behaviour goes behind a Core interface with one implementation per OS.
7. **Never pull a full-size image to draw a thumbnail.** Use the tiered extraction in
   `docs/plan/04-gallery.md`.
8. **Write to `.part`, then `mv` into place.** A partial file must never look complete, and a
   destination is never overwritten without inspecting it first.
9. **Logs must not contain file paths, filenames or EXIF GPS** unless verbose diagnostics is
   explicitly enabled.
10. **Never try to circumvent Android's security model** — no root exploits, no bypassing the
    ADB authorization prompt. PRs that do will be closed.

Errors become human sentences. `"The Android device disconnected during the transfer"`, never
`"exit code 1"`.

## A note on testing

The fake server proves internal consistency. Only a phone proves correctness.

Resumable upload was built on `dd of=… seek=N conv=notrunc`, matched the documentation, and
passed against the fake server for weeks. On a real Galaxy S24 Ultra it silently truncated a
3 MiB upload to 2.69 MiB, because `dd` issues one `read()` per block and a socket read returns
only what has arrived. It now sends the remainder as an ordinary sync `SEND` and appends with
`cat >>`, and the fake server models append redirection so it can no longer pass something a
phone would reject.

Every bug of consequence in this project came from real hardware or someone's eyes, not from
the test suite. So: if you change transfer, listing or thumbnail behaviour, say in the PR
whether you ran it against a device, and which one. "Fake server only" is an acceptable
answer — an inaccurate one is not.

## Reporting a bug

Useful reports include the phone make/model, Android version, host OS, and what you expected
versus what happened. If it involves a transfer or a filename, **check that no private path
leaked into anything you paste** — and note that logs are scrubbed by default, so a verbose
log may contain filenames.

`Settings → Diagnostics → Export Diagnostic Log` collects the useful state.
