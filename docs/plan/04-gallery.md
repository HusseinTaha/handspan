# Phase 4 — Thumbnails, viewers and Gallery

§97 phase 4, plus §18–§26 and §57–§60. The gallery is a **first-class feature, not another
folder view** (§18).

Everything here is governed by the spec's #1 performance principle (§94):

```
NEVER:  for each image: adb pull; generate thumbnail
INSTEAD: scan → metadata index → small/optimized transfer → cache → virtualized gallery
```

A phone with 20,000 photos at 4 MB each is 80 GB. Pulling those to draw a grid is not a slow
implementation, it's a broken one.

## 4.1 Tiered thumbnail extraction — the core trick

Thumbnails are obtained with the **smallest possible transfer**, escalating only when a
cheaper tier can't serve the file:

| Tier | Applies to | Method | Typical cost |
|---|---|---|---|
| **T1** | Most camera JPEGs | Range-read the first ~128 KB via `AdbRangeStream`, parse the **embedded EXIF IFD1 thumbnail** | 10–60 KB |
| **T2** | HEIC / HEIF | Parse ISOBMFF boxes from a partial read, extract the `thmb` item | ~100 KB |
| **T3** | PNG / WebP / GIF / BMP / AVIF | Pull the whole file **only if under threshold** (default 12 MB), decode and downscale via SkiaSharp | full file |
| **T4** | Video | Locate the `moov` atom (read the head; if absent, read the tail), then decode one frame at ~10% duration through ffmpeg over the loopback range server | a few MB |

**T1 is the difference between a gallery that feels instant and one that doesn't.** Nearly
every photo taken by a phone camera embeds a JPEG thumbnail in EXIF IFD1, so a 5 MB photo
yields a usable 20–60 KB thumbnail while transferring roughly 1% of the file. Phase 7's
companion app improves on even this by returning device-generated MediaStore thumbnails.

Files above the T3 threshold with no embedded thumbnail get a type icon plus their metadata
rather than a stalled grid cell. Never block the grid on one pathological file.

## 4.2 Cache (§21, §59)

Two caches, as the spec specifies:

**Thumbnail cache** — filesystem: `cache/<deviceId>/<hash>.webp`, WebP quality 80, where
`hash = SHA1(remotePath + size + mtime)`. The key is **DeviceId + RemotePath + Size +
ModifiedTime** (§21), so an edited or replaced photo regenerates automatically and two devices
can never collide (§39). LRU eviction to a configurable cap, default 2 GB (§50).

**Metadata cache** — SQLite `Media` table (§59): Id, DeviceId, Path, Name, Size, Modified,
MimeType, Width, Height, Duration. This is what lets the gallery open instantly.

### Decode pipeline

A bounded worker pool (default 4) fed by a **viewport-priority queue**: visible cells first,
then a small read-ahead margin, and **cancel work for cells scrolled out of view**. Without
cancellation, fast scrolling through 10,000 photos queues 10,000 transfers and the grid never
catches up.

## 4.3 Local streaming server (§58)

A loopback HTTP server serving `/{deviceId}/{token}/{urlencoded path}` with **HTTP Range
support**, backed by `AdbRangeStream`. This single component solves three problems at once:

- **Video plays without downloading** (§24) — LibVLCSharp opens the URL and seeks normally.
- **ffmpeg can decode a frame** for T4 thumbnails without a local copy.
- **"Open with" an external app** works on a device file with no explicit download.

Security: bind to 127.0.0.1 on a random port, require a **per-session token** in the path, and
reject requests without it — otherwise any local process could read the phone through us.
Reject path traversal and only serve within the device's accessible roots.

## 4.4 Gallery

- **Sources** (§19): DCIM, Pictures, Movies, Screenshots, Download by default, **configurable
  and never hard-coded** — OEMs and apps scatter media widely.
- **Media detection** (§20): the extension lists from the spec (images jpg/jpeg/png/webp/gif/
  heic/heif/avif/bmp; video mp4/mkv/mov/webm/avi/3gp/m4v; audio mp3/m4a/flac/wav/ogg/opus),
  **plus MIME sniffing where reliable** rather than trusting extensions alone.
- **Timeline** (§25): virtualized, date-grouped with sticky headers, normalized to local
  timezone. Backed by the SQLite index so it opens immediately, then background-scans and
  patches in changes (§60). Must handle 50,000+ items (§45). A date scrubber for fast travel
  through years.
- **Albums** (§26): virtual, directory-derived — Camera, Screenshots, Downloads, Videos,
  WhatsApp, Telegram, plus custom paths. Derived by inspection, **never by assuming an OEM
  layout**.

## 4.5 Viewers

**Image viewer** (§23): zoom, pan, rotate, fit, actual size, previous/next, download, delete,
open-with, copy, fullscreen. Keyboard per §87: ←/→ navigate, Space, Esc closes, F11
fullscreen. Loads the cached thumbnail instantly as a placeholder, then streams the full image
over it — so opening a photo is never a blank wait.

**Video viewer** (§24): play, pause, seek, volume, fullscreen, playback speed, duration,
resolution, codec info — via LibVLCSharp against the loopback server.

**`IMediaPreviewProvider`** (§57) with Image / Video / Audio / Document implementations, so the
UI doesn't care where media came from or how it's decoded.

**EXIF** (§33) via MetadataExtractor into the Properties dialog: camera, lens, ISO, exposure,
resolution. **GPS is treated as sensitive — displayed on request, never persisted to the index
or written to logs** (§43).

## 4.6 Licensing — decided: bundle LGPL, dynamically linked

**Decision (2026-08-12): option A.** Bundle ffmpeg and LibVLC/LibVLCSharp, dynamically linked,
with license notices and a source offer. Our own source stays closed; LGPL does not reach it.

Obligations we must honor:

1. **Dynamic linking only** — `ffmpeg` as a separate executable, `libvlc` as a separate
   `.dll`/`.dylib`. Never statically linked into our binary.
2. Ship the **LGPL license texts** and a notice naming the components and their versions.
3. Provide the **exact library source** or a written offer/link for the versions we ship.
4. Do not prevent a user from **replacing** the libraries with their own builds.
5. Publish any patches we make to the libraries themselves.

Two traps to avoid:

- **Use an LGPL ffmpeg build, not a GPL one.** ffmpeg is LGPL only when built without
  `--enable-gpl`; most popular prebuilt binaries bundle x264/x265 and GPL filters, which makes
  the build GPL — and GPL *would* reach our application. Pin a known LGPL build (or compile
  our own), record the version and its source URL, and assert the license in the build script.
- **This rules out Mac App Store distribution**, where LGPL's replaceability requirement
  conflicts with store sandboxing and signing. Direct download (DMG + notarization, per
  `08-packaging.md`) is unaffected. If App Store distribution ever becomes a goal, revisit
  with legal advice and fall back to native decoding.

The rejected alternatives, for the record: native decoding only (Windows WIC + Media
Foundation, macOS Image I/O + AVFoundation) means two implementations behind
`IMediaPreviewService` plus hand-rolled frame rendering into Avalonia; hybrid (native image
decode, external player for video) drops the in-app player and video thumbnails, both required
by §21 and §24.

## Implementation notes (2026-08-12)

**Range reads are the foundation.** `IDeviceFileSystem.ReadRangeAsync` uses `head -c` for a prefix and
`dd bs=1024 skip=… count=…` for an interior range, rounding outward and trimming locally. Both stay within
baseline toybox behaviour — no `iflag=skip_bytes`.

**Two extraction strategies, not four tiers.** Proper EXIF IFD1 parsing (little- *and* big-endian TIFF)
handles camera JPEGs; a bounded scan for a self-contained JPEG covers HEIC and DNG previews we cannot
address structurally. Both are capped by a 192 KB header window, so a 5 MB photo costs about 1% of itself.
Below that, a full decode runs only for files under the configured threshold; anything else gets an icon
rather than stalling the grid.

**Skia, not ffmpeg, for decoding** — MIT, so nothing in the shipping set is LGPL yet. That means **HEIC and
AVIF without an embedded preview, and all video frames, currently have no thumbnail**. Those need the ffmpeg
component, which is the main content of 4b.

### Streaming server — done (2026-08-13)

`DeviceStreamServer` serves device files over loopback HTTP with full range support, so a player seeks
inside a video that never leaves the phone. Video now plays **through the system's default player**, which
means playback works today with no LGPL dependency at all; an in-app player still needs LibVLCSharp.

Three defences, because this opens a socket that can read the user's phone: loopback-only binding, a
per-session random token compared in fixed time, and a registry of explicitly authorized paths — guessing a
URL cannot walk the device. Suffix ranges (`bytes=-N`) are supported because players use them to find a
trailing `moov` atom, and an MP4 with its index at the end is unplayable without that.

### Deferred to phase 4b

- ffmpeg-based video frame extraction and HEIC/AVIF decoding (the LGPL bundle, per the decision in §4.6).
  Videos currently show a film icon rather than a frame.
- LibVLCSharp in-app playback (§24). External-player streaming covers the capability meanwhile.
- Per-tile viewport tracking for thumbnail prefetch and cancellation. The service already bounds concurrency
  at four device reads and supports cancellation; the view currently requests the whole loaded page
- EXIF metadata in the properties dialog (§33), which needs MetadataExtractor

## Phase 4 exit criteria

Verified by tests (11 media tests):

- [x] EXIF IFD1 thumbnail extracted from little-endian **and** big-endian TIFF headers.
- [x] **Extraction works from a 128 KB window of a 5 MB file** — the property the gallery's feel depends on.
- [x] Fallback scan finds a JPEG preview inside a HEIC-shaped container.
- [x] A JPEG with no thumbnail yields nothing — the image's own scan data is not mistaken for a preview.
- [x] Noise-sized candidates rejected; truncated and out-of-window inputs handled without throwing.
- [x] Cache keys include size and modified time, so an edited photo regenerates (by construction).

Verified on a Galaxy S24 Ultra (2026-08-13):

- [x] **Embedded thumbnails extract from real camera photos**, transferring far less than half the
      originals — the claim the gallery's design rests on.
- [x] Range reads return byte-identical data to a full download at several offsets.
- [x] **Full pipeline on real data**: 8,099 media items scanned in 2.5 s; **12/12 thumbnails at 76 ms each**,
      and 21 ms for the same twelve from cache — a ~40× difference, which is what makes scrolling affordable.
- [x] Albums derived from the real folder layout, with colliding names disambiguated (below).
- [x] A real video streamed a range over the loopback server without being downloaded.
- [x] Changing a file's size or mtime yields a different cache key, so an edited photo regenerates.

### Album names must be disambiguated against each other (2026-08-13)

Naming each album by its own folder looked right until it met a real phone. This device has `Telegram` under
`Pictures`, `Movies` **and** `Download`, plus `Screenshots` under both `DCIM` and `Pictures` — so the list
showed three identical "Telegram" entries with no way to tell them apart.

Names are now assigned across the whole list: the short name is kept wherever it is unambiguous, and only
collisions grow a parent prefix, one ancestor at a time until they separate. Qualifying everything would be
noise — "DCIM · Camera" says nothing that "Camera" does not. The real device now yields
`Camera`, `Download · Telegram`, `Pictures · Telegram`, `Movies · Telegram`, `DCIM · Screenshots`.

The live test now asserts no two albums share a name, so this cannot regress.

### Multi-select and bulk export — added 2026-08-13

Selecting many items and copying them in one action (spec §31, §34), because that is the thing a photo
transfer tool is mostly used for.

- A circle on each tile toggles selection with no modifier keys — a gallery where multi-select requires
  knowing about Ctrl is a gallery where most people never find it. Ctrl+click and Shift+click work too, the
  latter selecting a range **across date groups**, since the timeline is one sequence to the user even
  though it is grouped.
- A plain click on a tile opens it, **except while a selection exists**, where it adjusts the selection
  instead. Throwing away a careful selection because of one stray click would be infuriating.
- The selection bar shows count and total size, and copying goes through the normal transfer queue as one
  batch — so it is scheduled, resumable and retried like anything else, rather than a separate copy path.
- Confirmation first, with an editable destination (§34); the selection is cleared once queued so a second
  click cannot silently copy everything again. Ctrl+A, Ctrl+D and Ctrl+C are wired (§87).

Covered by 11 tests over the selection rules, including backwards ranges and that cancelling queues nothing.

### Gallery bugs found by running it (2026-08-13)

Three faults that only appeared with a real device attached, all in the view rather than the pipeline:

1. **No thumbnails ever loaded.** The view requested them on `Loaded` and on `DataContextChanged` — both fire
   before the asynchronous scan fills the timeline, so it iterated an empty collection. Tiles now request
   their own thumbnail from the container's `Loaded` event, which also means only realized tiles hit the
   device, which is what §22 wanted in the first place.
2. **Tiles were not clickable.** The overlay button bound its command through `$parent[ItemsControl]`, which
   in nested repeaters resolves to the *inner* one — whose data context is a date group, not the gallery. The
   cast failed silently and the command was null. Handled in code-behind now, where it cannot mis-resolve.
3. **Only 240 items shown**, from a page-size constant. The timeline now loads the whole index, and the outer
   list is a virtualizing `ListBox` over date groups so that stays affordable.

Pending, needing 4b:

- [ ] A 10,000-photo DCIM folder: first visible thumbnails inside ~1 second (§45), flat memory.
- [ ] HEIC thumbnails where the file embeds no JPEG preview.
- [ ] A 4K H.265 video plays and seeks without being downloaded — needs 4b.
- [ ] Cache eviction under real load; range server token rejection — needs 4b.
