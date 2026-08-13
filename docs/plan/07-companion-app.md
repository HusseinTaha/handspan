# Phase 7 — Android companion app

> **Not started, and not startable on this machine (checked 2026-08-13).** Building it needs Gradle, the
> Android SDK and JDK 17+; this machine has none of those and only Java 8. Kotlin written here could not be
> compiled, installed or run, so it would be unverifiable code claiming to be a feature. Left undone
> deliberately rather than written blind.
>
> Nothing else depends on it: every companion capability is an optimisation over a direct-ADB path that
> already works.

§53–§56, §91. Optional APK that makes the gallery dramatically better than anything plain ADB
can do — while **direct ADB remains the fully-functional baseline**.

## Why it's worth building

Plain ADB forces us to ask the filesystem questions the filesystem answers badly:

| Question | Plain ADB | Companion |
|---|---|---|
| "Give me all photos" | Crawl thousands of directories looking for image extensions | One MediaStore query (§54) |
| "Thumbnail for this photo" | Range-read and parse embedded EXIF, or pull and decode | `ContentResolver.loadThumbnail` — the device already generated it |
| "What changed since last time?" | Re-crawl and diff (§52 — no filesystem watcher exists) | `FileObserver` / MediaStore change notifications, pushed |
| "Dimensions, duration, album" | Parse file headers over USB | MediaStore columns, already indexed by Android |
| "Hash this file" | `sha256sum` if present | Native, streamed |

The gallery becomes instant and correct instead of fast-and-approximate.

## Architecture

```
Windows/macOS app  ──host:forward──▶ adb server ──USB──▶ companion app ──▶ Android APIs
                                                         (MediaStore, FileObserver)
```

- Kotlin app exposing a `LocalServerSocket` on the **abstract** namespace (no listening TCP
  port on the phone, so nothing is exposed to the network).
- Desktop side requests `host:forward:tcp:<local>;localabstract:androidexplorer` and speaks a
  length-prefixed protocol over it — protobuf preferred over JSON for the media list, since
  50,000 items is where JSON parsing starts to cost real time.
- Installed via `adb install` **with explicit user consent** and a plain explanation of what it
  does and what it can access. Uninstallable from within Android Explorer.
- Version handshake on connect; a mismatched companion is ignored rather than trusted.

## Capabilities provided

- **MediaStore queries** returning media id, URI, filename, date taken, size, width, height,
  MIME type, album, and location (§54) — with GPS still treated as sensitive on our side (§43).
- **Device-generated thumbnails** at requested sizes, replacing tiers T1–T4 from phase 4
  entirely when present.
- **Change notifications** via `FileObserver` and MediaStore observers — replacing polling
  (§52) with push, so the Explorer and gallery update live.
- **Batch metadata** — one round trip for a page of items instead of one per item.
- **Device-side hashing** for transfer verification and duplicate detection.
- Scoped-storage-correct access using modern Android APIs rather than shell filesystem
  behavior (§53).

## Graceful degradation is a hard requirement

`DeviceCapabilities.HasCompanion` gates every companion path, and **every feature must have a
working direct-ADB fallback**. Concretely: the gallery, search, thumbnails and change detection
all keep their phase 4/5 implementations, and the companion is a faster provider behind the
same interfaces (`IGalleryService`, `IThumbnailService`, `IMetadataService`).

Test both paths in CI. The failure mode to avoid is the companion silently becoming a
prerequisite — at which point the app stops working on any phone where the user declines to
install it, which was the whole reason ADB was chosen as the transport (§55: "Use ADB direct
mode for maximum compatibility, companion mode for advanced capabilities").

## Phase 7 exit criteria

- Companion installs with consent, appears as a capability, and uninstalls cleanly.
- Gallery of 20,000 photos opens from MediaStore in a fraction of the crawl time, with
  device-generated thumbnails.
- Adding a photo on the phone appears in the app **without a manual refresh**.
- Toggling the companion off at runtime falls back to direct ADB with no functional loss —
  verified by running the full phase 4/5 test suites with the companion disabled.
