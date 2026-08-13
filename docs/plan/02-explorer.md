# Phase 2 — Filesystem abstraction and Explorer UI

§97 phase 2. Turns the transport into a browsable file manager. Target: folder navigation
feels instant (§45: <300 ms perceived) and a 10,000-entry directory scrolls at 60 FPS.

## 2.1 `AdbFileSystem : IDeviceFileSystem`

Implements §15's interface verbatim over phase 1's socket client. Nothing above this line
knows ADB exists.

Capability detection at session start (§77) fills `DeviceCapabilities` — probe by *trying*
cheap operations rather than guessing from Android version, because OEM behavior varies more
than version numbers suggest: can we `LIST /sdcard`, can we create and remove a temp
directory there, is `sha256sum` present, is `dd` present with `conv=notrunc`. Cache the
result per device profile so it costs nothing on reconnect.

## 2.2 `DeviceManager` and sessions

Wraps `host:track-devices`, raises `DeviceChanged`, and creates/disposes a `DeviceSession`
per authorized serial. **Multi-device capable from the start** (§39) even though the
multi-device UI is phase 6 — because retrofitting `DeviceId` into caches later is precisely
the cross-device collision bug the spec warns about.

Device state handling (§5): `device` → connect; `unauthorized` → show the authorization
walkthrough, not an error; `offline` → offer reconnect; `unknown` → diagnostics.

## 2.3 Directory cache (§29) — where the speed comes from

```
open folder → render cached listing immediately → refresh from device in background
            → diff → patch the view
```

SQLite table keyed by **(DeviceId, Path)** holding the entry list plus a fetch timestamp.
The diff patches the observable collection rather than replacing it, so scroll position and
selection survive a refresh. Cache entries are invalidated by our own write operations
immediately, and by TTL otherwise.

ADB provides no filesystem watcher for arbitrary shared storage (§52), so the model is
**refresh + polling + cache**, with a manual refresh (F5) always available. Phase 7's
companion app upgrades this to real `FileObserver` push notifications.

## 2.4 UI structure

Layout follows the §82 wireframe:

```
┌─────────────────────────────────────────────────────────┐
│ ← → ↑   🔎 Search                         ⚙ Settings   │
├───────────────┬─────────────────────────────────────────┤
│ QUICK ACCESS  │  📱 Galaxy S25                          │
│ ⭐ Camera     │  > Internal Storage > DCIM > Camera     │
│ ⭐ Downloads  │  ┌────┐ ┌────┐ ┌────┐ ┌────┐           │
│ DEVICE        │  │IMG │ │IMG │ │IMG │ │IMG │           │
│ 📱 Galaxy     │  └────┘ └────┘ └────┘ └────┘           │
├───────────────┴─────────────────────────────────────────┤
│ 3 files │ 428 MB                       Transfer: idle   │
└─────────────────────────────────────────────────────────┘
```

Pages (§83): Home, Devices, Explorer, Gallery, Transfers, Search, Storage, Settings.
Later-phase pages are visible but disabled with a "coming in a later version" affordance
rather than hidden, so navigation doesn't change shape between releases.

### View modes (§8)

| Mode | Control | Notes |
|---|---|---|
| Details | virtualized `DataGrid` | Name / Type / Size / Modified, sortable, resizable columns |
| Large icons | `ItemsRepeater` + `UniformGridLayout` | generic type icons this phase |
| Extra-large thumbnails | same, larger cells | real thumbnails arrive in phase 4 |

Both must stay smooth at 10,000+ entries (§45), which means **UI virtualization is
non-negotiable** — no `ItemsControl` without a virtualizing panel, and no per-item work in a
loop over the whole collection.

### Navigation and input

Double-click to open, Back / Forward / Up, clickable breadcrumbs (§30), editable address bar,
keyboard and touch navigation. Windows-standard shortcuts (§87): Ctrl+C/X/V, Delete, F2,
Ctrl+A, Alt+←/→, Backspace, Enter, Ctrl+L. Ctrl+F is wired but inert until phase 5. On macOS
these map to the Cmd equivalents.

### Dialogs and menus

Context menu (§32): Open, Open With, Download, Copy, Cut, Paste, Rename, Delete, Properties,
Copy Path — with entries disabled per `DeviceCapabilities` rather than failing on use (§77).
Properties dialog (§33) shows type, size, path, modified, permissions, owner; the EXIF and
resolution sections light up in phase 4.

### Home and Devices pages

No device connected (§84): a **setup walkthrough** — enable Developer options → enable USB
debugging → accept the RSA prompt on the phone — with a live indicator that advances as the
state changes. We never bypass ADB authorization (§41), so `unauthorized` renders as
actionable guidance, not a failure.

Connected: the §6 dashboard — manufacturer, model, Android version, API level, serial,
storage bar, battery, connection type, ADB version, USB speed — with **Browse Files** and
**Open Gallery** actions.

## 2.5 Rules enforced from here on

- **No ADB call on the UI thread** (§46). ViewModels await services; services own the
  sockets.
- **Every long operation takes a `CancellationToken`** (§47) — including directory listing,
  which matters on directories with tens of thousands of entries.
- Errors surface as the human sentences from `00-architecture.md`, never protocol text (§48).
- Logging is scrubbed by default: "Opened folder", never the path (§43).

## Implementation notes (2026-08-12)

Two decisions taken while building:

**`ListBox` instead of `DataGrid` for the details view.** Avalonia 12's `DataGrid` ships as a separate
package whose Fluent theme include path differs from v11's, and the control brings styling risk for
what is a five-column list. A `ListBox` with a header row virtualizes by default, needs no extra
dependency, and gives the same behaviour. Revisit only if resizable/reorderable columns are wanted.

**Cache invalidation lives in a decorator**, `Services/CachedDeviceFileSystem.cs`, rather than at each
call site. A forgotten invalidation shows a folder that no longer matches the device — a bug that is
nearly invisible in review — so the wrapper makes it structural.

Rename and new-folder use inline overlays rather than dialog windows, keeping the phase free of window
plumbing; the same overlays serve delete confirmation (§51).

## Phase 2 exit criteria

Verified by tests (16 cache tests, `tests/AndroidExplorer.Services.Tests/DirectoryCacheTests.cs`):

- [x] A never-cached folder returns **null**, an empty folder returns **empty** — the distinction
      that decides whether the device gets read.
- [x] Entries round-trip faithfully, including the "size unknown" flag.
- [x] Arabic, CJK, emoji, `it's`, embedded newline and backslash names round-trip byte-exactly.
- [x] **Two devices never share a cache entry** (§39), and clearing one leaves the other intact.
- [x] Re-caching replaces rather than accumulates; a 5,000-entry listing round-trips.

Verified on a Galaxy S24 Ultra (2026-08-13):

- [x] **mkdir, rename and delete round-trip** with `صور العائلة`, `照片 test`, `it's mine`, `a b  c` and
      `emoji 🌴` — created, listed back byte-exactly, renamed, and deleted.
- [x] A protected path (`/data/data`) produces the §78 message rather than an exception.
- [x] Structured listing of shared storage, with `/sdcard` resolving through its symlink.

Still pending (need a person watching the screen):

- [ ] Browse several levels via breadcrumbs, back/forward and the address bar.
- [ ] A 10,000-entry directory scrolls smoothly and sorts without freezing.
- [ ] A revisited folder renders from cache immediately, then patches in the fresh listing.
- [ ] Unplugging mid-browse shows the disconnect message; replugging restores the session.
