# Feature traceability

Every feature, the phase that delivers it, and the spec section it comes from. Use this to
confirm nothing was dropped.

## Requested features

The originally requested feature set, all accounted for:

| Feature | Phase | Spec |
|---|---|---|
| Connect over USB via ADB, avoid MTP entirely | 1 | §4, §5 |
| Automatic device detection (hot-plug) | 1 | §38 |
| Fast folder browsing | 2 | §7, §29, §45 |
| Fast storage browsing | 2, 5 | §16, §62 |
| Copy/paste PC → phone and phone → PC | 3 | §9, §10 |
| Real-time transfer progress | 3 | §11 |
| Multi-file transfers | 3 | §11 |
| Bulk operations | 3 | §11, §34 |
| Pause / resume | 3 | §13 |
| Automatic retry | 3 | §11 |
| Parallel transfers where appropriate | 3 | §12 |
| Delete / rename / create folders | 3 | §9 |
| Search | 5 | §27, §28 |
| Image and video thumbnails | 4 | §21, §94 |
| Fast gallery browsing | 4 | §18–§26 |
| Multiple phone connections | 2 (core), 6 (UI) | §39 |
| Drag & drop both directions | 3 (staged), 6 (native) | §31 |

## Full feature inventory by phase

### Phase 1 — transport
adb binary discovery and provisioning · adb server start/detect/restart · version-conflict
detection · `host:track-devices` hotplug · device states (device / unauthorized / offline /
unknown) · feature negotiation · structured directory listing · stat with 64-bit sizes ·
symlink resolution · shell quoting · device info (manufacturer, model, Android version, API
level, serial, storage, battery, USB speed) · `FakeAdbServer`

### Phase 2 — explorer
`IDeviceFileSystem` · device sessions · capability detection · directory cache with
diff-refresh · details view · large icons · extra-large thumbnails view · breadcrumbs ·
address bar · back/forward/up · keyboard + mouse + touch navigation · sort · show hidden
files · context menu · properties dialog · device dashboard · setup walkthrough for
unauthorized devices · dark/light theme · quick access sidebar · status bar

### Phase 3 — transfers
download · upload file · upload directory · mkdir · rename · move · copy · delete ·
recursive delete · clipboard copy/cut/paste · transfer queue · progress with speed and ETA ·
8 transfer statuses · pause · resume across disconnect/reboot/sleep/adb-restart/app-crash ·
auto-retry with backoff · cancellation · parallel scheduler with size classification ·
transfer preview for bulk operations · conflict resolution (replace/skip/rename/compare,
apply-to-all) · size verification · optional SHA-256 verification · transfer history ·
completion notifications · drag & drop in and out

### Phase 4 — gallery
tiered thumbnail extraction (EXIF embedded, HEIC `thmb`, full-decode, video frame) ·
thumbnail cache with metadata-based invalidation and LRU eviction · viewport-priority decode
queue · loopback HTTP range server · video streaming without download · gallery timeline
with date grouping · virtual albums · configurable gallery sources · MIME-based media
detection · image viewer (zoom, pan, rotate, fit, actual size, prev/next, download, delete,
open-with, copy, fullscreen) · video player (play, pause, seek, volume, speed, fullscreen,
codec info) · audio preview · EXIF metadata in properties · virtualization for 50,000+ items

### Phase 5 — search and storage
SQLite file index · FTS5 filename search with unicode61 and diacritic folding · incremental
crawl · search filters by type, size and date · favorites · quick access pinning · recent
virtual folder · per-device profiles · storage analyzer with category and directory
breakdown · largest-file finder · duplicate detection by escalating cost

### Phase 6 — wireless, multi-device, hardening
wireless ADB pairing and connection · mDNS discovery · multi-device switcher · per-device
queues · device-to-device copy · native promise-based drag-out (Windows `IDataObject`, macOS
`NSFilePromiseProvider`) · sleep/wake recovery · adb server crash recovery · job journal
replay · disk-full and vanished-destination handling · full settings · diagnostics page ·
compatibility matrix

### Phase 7 — companion app
Kotlin APK · abstract-socket channel over `host:forward` · MediaStore queries ·
device-generated thumbnails · `FileObserver` change notifications · batch metadata ·
device-side hashing · graceful degradation when absent

### Phase 8 — packaging
Windows x64/arm64 installers and signing · macOS universal `.app`, hardened runtime,
notarization, DMG · bundled or downloaded platform-tools · optional auto-update

## Deliberately not built

| Not doing | Why |
|---|---|
| MTP fallback | Windows-only, and macOS has no MTP stack at all. Revisit only if real users need non-debuggable phones (§98 keeps the FS API transport-neutral so it stays possible) |
| Windows shell namespace ("This PC → Android Explorer") | Explicitly out of MVP (§88); the standalone app comes first |
| Root access / bypassing ADB authorization | Never (§17, §41, §78) |
| Device-side recycle bin | Not in v1 — permanent delete with confirmation. It consumes phone storage and needs careful design (§51) |
| Cloud sync, telemetry, automatic media upload | Against the privacy policy (§42, §43) |
| Contacts / SMS / app-data backup | Needs APIs beyond ordinary ADB file operations (§92) |
