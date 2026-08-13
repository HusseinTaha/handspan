# Android Explorer

A native desktop file manager and gallery for Android devices, over ADB — for **Windows and
macOS**. Built to replace Windows' MTP interface (slow, flaky, poor file-operation
semantics) and to fill the gap on macOS, where Google's Android File Transfer is abandoned.

Status: **planning complete, implementation not started.**

## What it does

Browse accessible Android storage fast, transfer files both directions with real progress
and resumable transfers, browse photos and videos as a proper gallery with thumbnails, and
search across the device — without ever touching the Windows "Phone" / MTP interface.

| | |
|---|---|
| Transport | ADB over USB (primary), ADB over Wi-Fi (optional) |
| UI | Avalonia 12 (MIT), .NET 10 |
| Platforms | Windows 10/11, macOS 12+ |
| Data | SQLite (directory cache, transfer journal, media/file index) |
| Privacy | Fully local. No cloud, no telemetry, no network needed after install |

## Documentation

- **[docs/plan/](docs/plan/)** — the full implementation plan, phase by phase. Start with
  [docs/plan/README.md](docs/plan/README.md).
- **[docs/plan/00-architecture.md](docs/plan/00-architecture.md)** — layering, decisions,
  domain model, licensing.
- **[docs/plan/FEATURES.md](docs/plan/FEATURES.md)** — every feature, its phase, and the
  spec section it comes from.
- **docs/notes.txt** — the original 98-section product & technical specification. Source of
  truth; `§n` references throughout the plan docs point into it.

## Building and running

Requires only the **.NET 10 SDK** — no workloads, no Visual Studio, no Android SDK.

```sh
dotnet build                                  # 11 projects
dotnet test                                   # unit + protocol tests
dotnet run --project src/AndroidExplorer.App  # launch
```

The built executable is at
`src/AndroidExplorer.App/bin/Debug/net10.0/AndroidExplorer.exe` (`AndroidExplorer` on macOS),
and can be launched directly — faster than `dotnet run` after the first build.

Cross-compile check for macOS, which proves no Windows-only API leaked into shared code
(a real run needs a Mac):

```sh
dotnet build -r osx-arm64
```

### What you'll see

Phase 0 is scaffolding, so the window is a shell that reports the environment: platform, .NET
runtime, whether `adb` was found and where, the app-data folder, and the download folder. The
navigation rail shows all eight planned pages with the later-phase ones disabled. The device
dashboard and file browser arrive in phase 2.

### Getting adb

The app searches, in order: a bundled copy, `PATH`, `ANDROID_HOME` / `ANDROID_SDK_ROOT`, the
usual SDK locations per OS, then the path configured in settings. If it reports "Not found",
install platform-tools one of these ways:

| | |
|---|---|
| Official zip | [Windows](https://dl.google.com/android/repository/platform-tools-latest-windows.zip) · [macOS](https://dl.google.com/android/repository/platform-tools-latest-darwin.zip) — extract anywhere, then set the path in settings or add it to `PATH` |
| Windows package manager | `winget install Google.PlatformTools` |
| macOS | `brew install --cask android-platform-tools` |
| Android Studio | Already included, at `%LOCALAPPDATA%\Android\Sdk\platform-tools` or `~/Library/Android/sdk/platform-tools` — found automatically |

Phase 1 adds a consented in-app download so none of this is necessary.

### Application data

Settings, the transfer journal, caches and logs live in
`%LOCALAPPDATA%\AndroidExplorer` on Windows and
`~/Library/Application Support/AndroidExplorer` on macOS. Logs are under `logs/` and are
scrubbed of file paths and names unless verbose diagnostics is enabled (§43); deleting the
folder resets the app completely.

## Requirements on the phone

USB debugging must be enabled and this computer authorized on the device:

1. **Settings → About phone**, tap **Build number** seven times to unlock Developer options.
   On Samsung it's under *About phone → Software information*.
2. **Settings → System → Developer options → USB debugging**, turn it on.
3. Connect the cable. The phone shows **"Allow USB debugging?"** — tick *Always allow from this
   computer* and tap **Allow**. If no prompt appears, unlock the phone and replug.
4. Confirm with `adb devices` — the device should be listed as `device`, not `unauthorized`.

The app walks through this on first run. It never attempts to bypass Android's ADB
authorization, and it does not require or use root.
