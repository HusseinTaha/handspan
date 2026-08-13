# Phase 0 — Architecture and scaffolding

## Why this shape

The single most important structural decision (§2, §98): **the UI must not depend on ADB.**
The UI talks to `IDeviceFileSystem` and friends; ADB is one backend behind that boundary.
This is what allows phase 7's companion app — and, if ever needed, MTP — to be an addition
rather than a rewrite.

```
UI (Avalonia)  →  Application Services  →  Virtual Device FS  →  ADB backend  →  device
```

Dependency direction is strictly downward: `App → Services → Adb → Core`. `Core` references
nothing. **The UI must never reference `AndroidExplorer.Adb`**; it reaches everything through
interfaces hanging off `DeviceSession`.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Platforms | Windows + macOS, portable from day one | Retrofitting cross-platform is expensive and invasive; free if decided up front |
| UI framework | **Avalonia 12** (§3 recommends WinUI 3) | WinUI 3 is Windows-only. Avalonia preserves every layer of the spec's design and adds macOS. MIT licensed |
| Target framework | **net10.0** | Current LTS, supported to Nov 2028. .NET 9 is STS and went out of support May 2026; .NET 8's LTS window closes Nov 2026. Retargeting is a one-line change if needed |
| ADB access | **Own socket client** (§98 says don't reimplement) | We implement only the *host* protocol — documented, stable, localhost TCP — never the USB wire protocol. It is the only way to get §13 resume, §58 streaming and §73 structured listing |
| Transport priority | ADB first-class; FS API transport-neutral; MTP much later | §98 |
| MVVM | CommunityToolkit.Mvvm | Source-generated observables, no runtime weaving |
| Storage | SQLite via Microsoft.Data.Sqlite | Directory cache, transfer journal, file/media index, FTS5 |

### Why not the alternatives

**WinUI 3** — Windows-only, needs the Windows App SDK toolchain, and drag-out of files that
don't exist locally is a known weak spot. **.NET MAUI** — macOS via Catalyst is a poor fit
for a dense multi-window file manager. **Tauri/Electron** — the ADB layer would be rewritten
in Rust/TS and the spec's C# design discarded. **Qt/C++** — the most native result and the
best shell integration, at far more implementation effort.

## Licensing — nothing paid, anywhere

Avalonia core is **MIT**. The paid Avalonia products (XPF for porting existing WPF apps,
Accelerate for tooling) are not used. CommunityToolkit.Mvvm, Microsoft.Extensions.*,
Microsoft.Data.Sqlite: MIT. Serilog: Apache 2.0. MetadataExtractor: Apache 2.0. SQLite:
public domain. Google platform-tools: Apache 2.0, fetched at first run rather than committed.

**Phase 4 media libraries — decided 2026-08-12:** bundle LibVLCSharp and ffmpeg, **LGPL,
dynamically linked**, with license notices and a source offer. Our own source stays closed.
Two consequences to respect: the ffmpeg build must be an **LGPL** build (not one of the common
prebuilt GPL builds bundling x264/x265, which would reach our application), and **Mac App
Store distribution is ruled out** — direct DMG download is unaffected. Full rationale and
obligations in [04-gallery.md](04-gallery.md#46-licensing--decided-bundle-lgpl-dynamically-linked).

## Solution layout

```
AndroidExplorer.sln
src/AndroidExplorer.Core        models, interfaces, exceptions — zero platform deps
src/AndroidExplorer.Adb         socket client, sync protocol, IDeviceFileSystem impl
src/AndroidExplorer.Services    DeviceManager, TransferManager, Settings, Cache
src/AndroidExplorer.Data        SQLite: dir cache, transfer journal, file/media index
src/AndroidExplorer.Media       thumbnails, decoding, streaming, metadata (phase 4)
src/AndroidExplorer.Search      indexer, search, storage analysis (phase 5)
src/AndroidExplorer.App         Avalonia: Views/ViewModels/Controls/Platform
tests/AndroidExplorer.Adb.Tests         FakeAdbServer + protocol tests
tests/AndroidExplorer.Core.Tests
tests/AndroidExplorer.Services.Tests
tests/AndroidExplorer.Media.Tests
companion/                      Android companion app (phase 7, Kotlin/Gradle)
tools/platform-tools/           bundled adb, gitignored
```

Adapted from §3: `.Data` and `.Search` are split out because the SQLite layer is shared by
the directory cache (phase 2), transfer journal (phase 3) and search index (phase 5).

## Build configuration

- `git init` + `.gitignore` (standard .NET, plus `tools/platform-tools/`, `cache/`).
- `global.json` pinning SDK **10.0.203**, `rollForward: latestFeature` — reproducible builds.
- `Directory.Build.props` with shared `TargetFramework=net10.0`, `Nullable=enable`,
  `TreatWarningsAsErrors=true`, `LangVersion=latest`, and **`InvariantGlobalization=false`**.
  That last one matters: RTL and CJK filename handling depends on real ICU data.
- Package versions as resolved in phase 0: **Avalonia 12.1.1** (`Avalonia`, `.Desktop`,
  `.Themes.Fluent`, `.Fonts.Inter`), CommunityToolkit.Mvvm 8.4.2,
  Microsoft.Extensions.Hosting 10.0.11, Serilog.Extensions.Logging 10.0.0, Serilog.Sinks.File 7.0.0.
  Note `Avalonia.Diagnostics` is **not** referenced — it stopped at 11.3.x because DevTools moved
  into the core package in Avalonia 12, and mixing the two majors breaks the build.
  `Avalonia.Controls.DataGrid` and `.Controls.ItemsRepeater` are added in phase 2 when the views
  need them.
- Central Package Management via `Directory.Packages.props` is worth adding once more projects take
  packages; with only the App project referencing any, it would be ceremony for now.

## Cross-platform discipline

The rule that keeps macOS cheap: **no `#if WINDOWS`, no P/Invoke outside
`src/AndroidExplorer.App/Platform/{Windows,MacOS}`.** Platform behavior lives behind
Core-declared interfaces with one implementation per OS:

| Interface | Windows | macOS |
|---|---|---|
| `IAdbBinaryProvider` | `%LOCALAPPDATA%` paths, `.exe` | `~/Library/Android/sdk`, Homebrew paths, quarantine clearing, +x |
| `IShellIntegration` | reveal in Explorer, `ShellExecute` open-with, Known Folders | reveal in Finder, `open -a`, `NSWorkspace` |
| `IShellDragService` | staged paths → later `CFSTR_FILEDESCRIPTORW` | staged paths → later `NSFilePromiseProvider` |
| `IPlatformNotifications` | toast notifications | `NSUserNotification` / notification center |
| `IPowerEvents` | `WM_POWERBROADCAST` | `NSWorkspace` sleep/wake notifications |

A `dotnet build -r osx-arm64` in CI proves nothing Windows-only leaked into shared code.

## Domain model

Exactly §76, so later phases drop in cleanly: `DeviceId`, `DevicePath`, `DeviceEntry`,
`DeviceFile`, `DeviceDirectory`, `MediaItem`, `Album`, `TransferJob`, `TransferProgress`,
`StorageInfo`, `FileMetadata`, `DeviceCapabilities`, `DeviceState`.

### `DevicePath` — a value type, not a string (§75)

```csharp
public readonly struct DevicePath : IEquatable<DevicePath>
{
    public string Value { get; }          // always POSIX, always absolute, no trailing /
    public string Name { get; }           // last segment
    public DevicePath Parent { get; }
    public DevicePath Combine(string childName);
    public bool IsRoot { get; }
    public static DevicePath Parse(string posixPath);   // rejects '\', rejects relative
}
```

Every filesystem method accepts `DevicePath` and nothing else. This single type eliminates
the entire `C:\sdcard\DCIM` class of bug — the one §75 calls out by name.

### `DeviceCapabilities` (§77)

Public flags the UI reads to disable unsupported operations rather than failing at runtime:
`CanBrowseSharedStorage`, `CanUpload`, `CanDownload`, `CanDelete`, `CanRename`,
`CanCreateDirectory`, `CanStream`, `CanWirelessAdb`. Plus internal negotiation results:
`HasStatV2`, `HasLsV2`, `HasShellV2`, `HasSendRecvV2`, `HasCompanion`.

### Interfaces (§15, §68–71)

`IDeviceManager`, `IDeviceConnection`, `IDeviceFileSystem`, `ITransferManager`,
`IThumbnailService`, `IGalleryService`, `ISearchService`, `IMediaPreviewService`,
`IMetadataService`, `ICacheService`, `ISettingsService`. All declared in phase 0; the
later-phase ones are stubs that throw `NotSupportedException` until their phase lands.

`IDeviceFileSystem` is implemented verbatim from §15 — "the most important abstraction in
the application" — with `ListAsync`, `GetInfoAsync`, `OpenReadAsync`, `UploadAsync`,
`DownloadAsync` (both taking `IProgress<TransferProgress>` and `CancellationToken`),
`CreateDirectoryAsync`, `DeleteAsync`, `RenameAsync`.

### `DeviceSession` (§70)

One per connected device, owning `Connection`, `FileSystem`, `TransferManager`, `Gallery`,
`Search`, `Metadata`. Prevents global state sprawl. **Every model and cache key carries
`DeviceId`** (§39) — retrofitting that later is exactly the cross-device cache collision the
spec warns about.

## Errors become sentences (§48)

Typed exceptions in Core, each with a user-facing message resource:

| Exception | Shown to the user |
|---|---|
| `DeviceDisconnectedException` | "The Android device disconnected during the transfer." |
| `AccessDeniedException` | "Android denied access to this folder. This location may be protected by Android." |
| `PathNotFoundException` | "That folder no longer exists on the device." |
| `DeviceOfflineException` | "The device is connected but not responding. Try reconnecting the cable." |
| `AdbServerException` | "Could not start the Android connection service." |
| `InsufficientSpaceException` | "Not enough free space to finish this transfer." |
| `DeviceStorageUnavailableException` | "The device's storage is unavailable — the phone may be locked." |

The UI never displays "exit code 1", a raw stderr dump, or a protocol `FAIL` string.

## Cross-cutting infrastructure

- **DI + hosting**: `Microsoft.Extensions.Hosting`, one composition root in `App`.
- **Logging**: `Microsoft.Extensions.Logging` → Serilog rolling file. A scrubbing enricher
  strips paths, filenames and GPS by default; verbose diagnostics is opt-in (§43).
- **Settings** (§50): JSON in the platform app-data folder, `ISettingsService` with change
  notifications, and per-device profiles (§67).
- **Privacy** (§42, §44): no cloud, no telemetry, no network calls in the core path. The only
  outbound request the app may ever make is the consented platform-tools download.

## Phase 0 exit criteria — met 2026-08-12

- [x] `dotnet build` clean, **0 warnings** (with `TreatWarningsAsErrors`).
- [x] `dotnet build -r osx-arm64` clean — nothing Windows-only leaked into shared code.
- [x] `dotnet test` green: **88 tests**, covering `DevicePath` normalization and rejection of
      `C:\...`, unicode/emoji/RTL segments, the 255-byte name limit, ancestry, dictionary-key
      behavior, plus `DeviceId` cache-key safety and the transfer/device model invariants.
- [x] App launches, window renders, theme follows the OS, adb discovery runs on startup and the
      log records only the outcome — never the path (§43).

Known gaps deliberately left for their own phases: `IPlatformNotifications` logs instead of
notifying (phase 3), `IPowerEvents` is a no-op (phase 6), and the lower-layer projects
(`Adb`, `Services`, `Data`, `Media`, `Search`) contain only their interfaces so far.
