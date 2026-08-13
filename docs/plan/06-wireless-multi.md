# Phase 6 — Wireless ADB, multi-device, hardening

§97 phase 6, plus §38–§41, §49–§50 and §78–§81. This is the phase that turns a working app
into a dependable one.

## 6.1 Wireless ADB (§40)

Android 11+ supports wireless debugging. Flow:

1. User opens Developer options → Wireless debugging → Pair device with pairing code.
2. We pair: `host:pair:<code>:<host>:<port>`.
3. We connect: `host:connect:<host>:<port>`.
4. The device then appears on the normal `track-devices` stream and needs no special handling
   anywhere else in the app — it's just another serial.

Optional convenience: mDNS discovery of `_adb-tls-pairing._tcp` and `_adb-tls-connect._tcp` to
populate host/port automatically instead of typing them.

**Not the default** (§40). The UI states plainly that USB is faster and more predictable for
large transfers, and warns before starting a multi-gigabyte transfer over Wi-Fi. Wireless is
for convenience — grabbing a few photos without a cable — and the app should say so.

Capability flag `CanWirelessAdb` gates the UI (§77). Never bypass authorization (§41).

## 6.2 Multi-device UI (§39)

The plumbing exists from phase 2 (`DeviceSession` per serial, `DeviceId` in every model and
cache key). This phase adds the surface:

- Device switcher in the sidebar; each device shows its own tree, gallery and queue.
- Per-device transfer queues with independent concurrency and independent scheduler
  benchmarks.
- **Device → device copy**, staged through the PC, with a single progress job spanning both
  legs and correct cleanup of the staging file on failure.
- A dedicated test: connect two phones, browse and transfer on both simultaneously, and assert
  **no cache bleed** — the failure mode §39 exists to prevent.

## 6.3 Native promise-based drag-out

Replaces phase 3's staging with on-demand streaming, behind the existing `IShellDragService`:

| OS | Mechanism |
|---|---|
| Windows | `IDataObject` with `CFSTR_FILEDESCRIPTORW` + `CFSTR_FILECONTENTS`, served lazily from `AdbRangeStream` |
| macOS | `NSFilePromiseProvider` with a promise fulfilment queue |

This is the only genuinely platform-specific UI work in the project, which is why it lives in
`App/Platform/{Windows,MacOS}` and why phase 3 shipped the portable version first. Dragging 40
photos to a folder then streams on demand rather than pre-staging gigabytes.

## 6.4 Hardening (§81)

Every failure below gets a deliberate behavior and a human message (§48), and each gets a
`FakeAdbServer` test:

| Failure | Behavior |
|---|---|
| Cable pulled mid-transfer | Jobs → `Paused`; auto-resume on reconnect |
| Cable pulled mid-listing | Cached view retained; banner explains; retry on reconnect |
| Phone sleeps / locks | Detect `DeviceStorageUnavailable`; pause and explain (FUSE storage can vanish while locked) |
| Phone reboots | Session torn down and rebuilt; queue paused then resumed |
| adb server crashes | Reconnect with backoff, restart the server, restore sessions |
| PC sleeps and wakes | `IPowerEvents` → pause before sleep, revalidate devices on wake |
| App killed hard | Journal replay on next start; jobs restored as `Paused` |
| Destination disk full | `InsufficientSpaceException`, job `Failed` with a specific message, `.part` retained |
| Destination folder vanished | Detected before write; job `Failed`, offer to pick a new destination |
| Protected path (`/data`) | §78 message: "This Android location is protected…" |
| Unauthorized mid-session | Return to the authorization walkthrough, keep the queue paused |

## 6.5 Settings and diagnostics

Complete the §50 settings surface:

- **General**: default download/upload folders, confirm deletes, confirm large transfers,
  start with Windows/macOS login, minimize to tray/menu bar.
- **Explorer**: default view, sort order, show hidden files, show extensions, refresh behavior.
- **Transfers**: max concurrent transfers, retry count, resume behavior, verification,
  completion notification.
- **Gallery**: thumbnail quality, cache size cap, video thumbnail generation, timeline
  grouping.
- **Connection**: prefer USB, allow wireless ADB, **ADB executable location**, timeouts.

**Diagnostics** (§49): Export Diagnostic Log, Restart ADB, Rescan Devices, Open ADB Logs — and
a verbose-logging toggle that is off by default and warns that it records file paths (§43).
"Never require users to understand ADB commands" is the standard for this whole page.

## 6.6 Compatibility matrix (§79)

Start a living table in `docs/COMPATIBILITY.md`, filled in as real hardware is tested: Android
version, OEM, model, adb version, supported operations, known issues. Test targets per §79:
Pixel, Samsung, Xiaomi, OnePlus, Motorola, Oppo, Vivo across multiple Android versions.

Record OEM quirks as they're found — missing `sha256sum`, `dd` without `conv=notrunc`, unusual
media directory layouts — and drive `DeviceCapabilities` from them rather than from version
sniffing.

## Implementation notes (2026-08-12)

**Settings are read at the point of use, not captured.** `TransferManager` and `ThumbnailService` hold an
`ISettingsService` and read `.Current` per operation, so changing concurrency, retry count or thumbnail size
applies to the next job without a restart. The settings file is user-visible and *will* be hand-edited, so
values are clamped on load and save — zero concurrent transfers would stall the queue forever.

**Disconnect now pauses before tearing down.** Previously a vanishing device disposed its session directly,
which lost the in-memory queue. The manager now calls `PauseAllAsync` with a reason first, so partial files
and journalled resume points survive — the difference between resuming and starting over (spec §38).

**Sleep is handled through `IPowerEvents`**, implemented on Windows via `SystemEvents.PowerModeChanged` plus
`SessionEnding` (log-off deserves the same treatment). On wake the app **re-enumerates devices before
resuming anything**, so a phone unplugged during sleep does not have transfers resumed against a device
that is gone.

**Wireless pairing** uses `host:pair:<code>:<host>:<port>` then `host:connect`. Both answer `OKAY` even when
they fail and explain themselves in the payload, so the response text is inspected rather than trusted.
Off by default, with the UI stating that USB is faster for large transfers (spec §40).

**Multi-device is done (2026-08-13).** The shell holds a session per connected device and a switcher appears
in the rail once there is more than one. Only the active device's pages are re-pointed on switch; background
devices keep transferring, because their queues live in their own sessions. Unplugging a background device
does not disturb what the user is looking at, and unplugging the active one falls through to another
connected device rather than dumping the user on Home.

### Still outstanding in this phase

- **Native promise-based drag-out** (Windows `CFSTR_FILEDESCRIPTORW`, macOS `NSFilePromiseProvider`). The
  staging implementation from phase 3 works; this is an optimisation, and it is the largest single piece of
  platform interop in the project.
- Device-to-device copy; the compatibility matrix, which needs real hardware.

## Phase 6 exit criteria

Verified by tests (13 settings and profile tests):

- [x] Settings survive a restart and raise change notifications.
- [x] Hand-edited nonsense is clamped (zero concurrency, zero thumbnail size, negative retries).
- [x] A corrupt settings file falls back to defaults instead of blocking startup.
- [x] Favourites and profiles are per device (§39, §67), including unicode display names; adding a
      favourite twice is harmless.
- [x] The **application's own composition root** validates, so a view model cannot gain an unregistered
      dependency and crash at launch.

Pending hardware or outstanding work:

- [ ] Pair and connect over Wi-Fi against a real phone, then browse and transfer.
- [x] **Two devices simultaneously** — verified against two independent fake servers (6 tests): each lists
      its own files; the directory cache does not blend them at the same path; a write on one does not
      invalidate the other's cache; transfers run and complete independently; pausing one leaves the other
      running; the journal restores only the requested device's jobs.
- [ ] Two *real* phones connected at once.
- [ ] Drag 40 photos out and confirm on-demand streaming (needs the native shims).
- [ ] Sleep the PC mid-transfer, wake, and watch the queue resume.
- [ ] Every row of the §6.4 failure table exercised.
