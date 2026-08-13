# Implementation plan

Full plan for Android Explorer, from empty repo to finished product. Phase ordering follows
the spec's own recommended build order (`docs/notes.txt` §97). Each phase ends with
something runnable and independently verifiable.

`§n` throughout these docs refers to numbered sections of `docs/notes.txt`.

## Hardware validation (2026-08-13)

First run against a real device: **Samsung Galaxy S24 Ultra (SM-S928B), Android 16, USB**. All 284 tests
pass with nothing skipped.

| Verified on hardware | Result |
|---|---|
| Our device list vs the `adb devices` CLI | identical |
| `stat_v2` field offsets — a wrong one yields plausible garbage | stat agrees with listing for the same file |
| `/sdcard` symlink resolution | resolves to a browsable directory |
| Structured listing of shared storage | correct, no `ls` parsing |
| Device probe (make, model, API level, storage) | populated |
| Range reads vs a full download | byte-identical at several offsets |
| **Embedded thumbnail from a prefix read, on real camera photos** | works; far less than half the originals transferred |
| mkdir / rename / delete with Arabic, CJK, emoji, apostrophes | round-trip byte-exact |
| Protected paths (`/data/data`) | refused with the §78 message |
| Upload → device-side SHA-256 → download | byte-identical, hashes agree |
| **Resumed upload from a 1 MiB-aligned partial** | byte-identical after the fix below |
| Resumed download from an aligned offset | reassembles the original exactly |
| Transfer manager end to end | completes, no `.aepart` left behind |
| **Pull throughput** | **38.0–38.8 MB/s** (195 MB in ~5 s) |
| Index crawl of DCIM | 7,710 entries in 2.6 s |
| **Search over that index** | 500 matches in **21 ms** (target was <100 ms) |
| Storage analysis | 7,693 files / 85.7 GB categorized; volume 190.1 GB used of 221.8 GB |

That throughput is the number the project exists for: MTP on comparable hardware typically manages a
fraction of it.

### The bug only hardware could find

Resumable **upload** was built on `dd of=… bs=1M seek=N conv=notrunc` fed through the socket's stdin. The
fake server modelled what the documentation describes, and it passed there for weeks. On a real Galaxy S24
Ultra it silently lost data: resuming a 3 MiB upload produced a **2.69 MiB file**.

The cause is that `dd` issues one `read()` per block, and a read from a socket returns only what has arrived
so far — so `dd` writes short blocks and drops the rest. Making it safe would need `iflag=fullblock`, which
is exactly the sort of non-baseline option this project set out not to depend on.

Rewritten to send the remainder as an ordinary sync `SEND` to a sibling temp file and append it with
`cat >> `, then verify the joined size. Both are primitives already proven by every other transfer. The
fake server now models shell append redirection too, so it can no longer pass something the phone rejects.

## Phases

| # | Phase | Delivers | Status |
|---|---|---|---|
| 0 | [Scaffolding](00-architecture.md) | Solution, projects, domain model, DI, logging | **Done** (2026-08-12) |
| 1 | [ADB transport](01-adb-transport.md) | Socket client, server management, hotplug, listing, device probe | **Done** (2026-08-12) — validated against a real device and the adb CLI |
| 2 | [Explorer](02-explorer.md) | Filesystem abstraction, directory cache, browsing UI | **Code complete** (2026-08-12) — live browsing not yet verified on hardware |
| 3 | [Transfers](03-transfers.md) | All file ops, transfer manager, resume, drag & drop | **Code complete** (2026-08-12) — resume verified against `FakeAdbServer`; live throughput not yet measured |
| 4 | [Gallery](04-gallery.md) | Tiered thumbnails, streaming server, gallery, viewers | **Partly done** (2026-08-12) — range reads, embedded-thumbnail extraction, cache, media index, timeline, albums, image viewer. Video frames + streaming server deferred to 4b |
| 5 | [Search & storage](05-search-storage.md) | SQLite index + FTS5, storage analyzer, duplicates, favourites | **Done** (2026-08-13) — index, FTS5 search with filters, storage breakdown, largest files, duplicates, favourites + Quick Access. Only the Recent virtual folder (§64) deferred |
| 6 | [Wireless & hardening](06-wireless-multi.md) | Wireless ADB, multi-device UI, native drag-out, recovery | **Mostly done** (2026-08-13) — settings, profiles/favourites, wireless pairing, sleep/disconnect pause, **multi-device sessions + switcher**. Native drag-out outstanding |
| 7 | [Companion app](07-companion-app.md) | Android APK: MediaStore, real thumbnails, change notifications | **Blocked** — needs Gradle + Android SDK + JDK 17, none present. Purely additive; nothing depends on it |
| 8 | [Packaging](08-packaging.md) | Installers, signing, notarization, updates | **Partly done** (2026-08-13) — publish scripts for 4 runtimes, macOS `.app` bundle, verified standalone run. Installers and signing need certificates |

Cross-cutting: **[09-testing.md](09-testing.md)** (test strategy, `FakeAdbServer`, device
and failure matrices) and **[FEATURES.md](FEATURES.md)** (feature → phase → spec section).

## The shape of the product

```
                    Windows / macOS UI  (Avalonia)
        Explorer │ Gallery │ Search │ Transfers │ Devices
                              │
                    Application Services
   DeviceManager │ TransferManager │ Thumbnails │ Metadata │ Cache
                              │
                  Virtual Device File System
        IDeviceFileSystem / IDeviceFile / IDeviceStream
                              │
                 ┌────────────┴────────────┐
              ADB Backend            (later) MTP
                 │
         ┌───────┴───────┐
    Direct ADB      Companion App (phase 7)
```

The UI never learns whether a file came from ADB, a companion app, or anything else. That
is the single most important structural decision (§2), and it is what makes phase 7 an
addition rather than a rewrite.

## Milestones worth aiming at

- **End of phase 3** — a daily-usable MTP replacement. This is the point the project becomes
  genuinely useful, and the natural first release.
- **End of phase 5** — feature-complete against the spec's v1.1 (§90).
- **End of phase 7** — the spec's v2 (§91), where the gallery becomes better than anything
  achievable through plain ADB.

## Ground rules that apply to every phase

Pulled out because they are easy to violate incrementally. The full list is in `CLAUDE.md`.

- No ADB on the UI thread (§46); cancellation on every long operation (§47).
- `DevicePath`, never strings (§75). Structured listings, never `ls -la` parsing (§73).
- `DeviceId` in every model and cache key (§39).
- Errors become human sentences — "The Android device disconnected during the transfer",
  not "exit code 1" (§48).
- Logs carry no filenames or GPS unless verbose diagnostics is on (§43).
- Fully local: no cloud, no telemetry, works offline after install (§42, §44).
- Performance targets (§45): device detection < 2 s, folder navigation < 300 ms perceived,
  first gallery thumbnails < 1 s, 60 FPS scrolling, 10,000+ entries per directory,
  50,000+ indexed media items.
