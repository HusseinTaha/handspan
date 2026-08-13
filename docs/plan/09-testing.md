# Testing strategy

§79–§81. The central constraint: **the dev machine has no Android device attached and no adb
installed**, so the product must be verifiable without hardware. That's not a compromise — it
also makes failure injection (cable pulls, permission denials, slow links) reproducible in a
way real hardware never is.

## `FakeAdbServer` — the backbone

A loopback TCP server (in `tests/Handspan.Adb.Tests`) speaking the **real** adb host +
sync protocol against an in-memory filesystem. Prefer it over mocking `IAdbClient`: mocks
verify that we called what we intended, while `FakeAdbServer` verifies the bytes on the wire
are actually correct — which is where protocol bugs live.

Injectable faults:

| Fault | Simulates |
|---|---|
| Drop connection after N bytes | Cable pull mid-transfer |
| `FAIL` for a path | Permission denied, protected location |
| Throttle throughput | USB 2, slow phone |
| Withhold `stat_v2` / `ls_v2` / `shell_v2` / `sendrecv_v2` | Older devices |
| Emit `device` → `offline` → `unauthorized` | Reboot, lock, revoked authorization |
| Synthetic 5 GB sparse file | Large-file and resume tests |
| `dd` rejecting `conv=notrunc` | OEM toybox variation |
| Server death | adb server crash |

## Automated coverage by area

**Protocol (phase 1)** — framing and `OKAY`/`FAIL` parsing; `track-devices` deltas; feature
negotiation in all combinations; `STA2` struct parsing round-trip; `LIST` v1 32-bit truncation
flagged rather than displayed; symlink resolution.

**Unicode (§74)** — filenames with spaces, `'`, `"`, embedded newline, emoji, `صور العائلة`,
`照片`, `旅行 🌴`, plus a name at the 255-byte limit. Listing, transferring, renaming and
searching each round-trip byte-exactly.

**Shell safety (§71)** — `ShellQuote` against `it's`, `$HOME`, backticks, `$(id)`,
`; rm -rf /`, newline. Assert the generated command line, and assert that no code path
concatenates raw input.

**Paths (§75)** — `DevicePath` normalization, `..` rejection, `\` rejection, root edge cases,
and a test that no public filesystem method accepts a bare `string`.

**Transfers (phase 3)** — pull and push: complete, cancelled, and **interrupted at 3.2 GB of
5 GB then resumed to a byte-identical file** (§13's worked example). The `conv=notrunc`
fallback producing identical output. `.part` never visible as complete. Journal replay after a
hard process kill. Scheduler concurrency limits. Conflict policies including apply-to-all.
Retry backoff counts. Size-mismatch triggering retry.

**Thumbnails (phase 4)** — EXIF IFD1 extraction from a truncated JPEG, **asserting the byte
count transferred stays tiny** (this is the property the gallery's feel depends on); HEIC box
parsing; cache key invalidation when size or mtime changes; LRU eviction at the cap; decode
cancellation when cells scroll out of view.

**Range server (phase 4)** — HTTP Range correctness including multi-range and tail ranges;
token rejection; path traversal rejection.

**Index and search (phase 5)** — incremental crawl detecting added/removed/modified;
diacritic-insensitive and CJK FTS matching; filter composition; duplicate detection asserting
that **only final candidates get full-hashed**.

**Recovery (phase 6)** — every row of the §6.4 table in `06-wireless-multi.md`.

**Cross-platform guard** — `dotnet build -r osx-arm64` in CI on the Windows job, so a
Windows-only API cannot merge silently.

## Live device checklist

Only real hardware proves these. Run per phase, and on every release.

1. Detection under 2 seconds from plugging in (§45); `unauthorized` → `device` transition after
   accepting the RSA prompt.
2. Browse `/sdcard/DCIM/Camera`; a folder with 10,000+ files scrolls at 60 FPS.
3. Push and pull a 1 GB+ file; **record actual throughput** in `docs/COMPATIBILITY.md` — this
   is the number that justifies the project over MTP.
4. **Pull the cable mid-transfer** → pause → reconnect → resume → byte-identical result.
5. Lock the phone mid-transfer; reboot the phone mid-queue; sleep and wake the PC mid-transfer.
6. Delete, rename, mkdir, move within the device; recursive delete of a deep tree.
7. Drag a folder each direction, and a 40-file multi-selection out to Explorer/Finder.
8. Create a file named `صور العائلة 🌴.jpg` on the phone and verify it lists, transfers,
   renames and searches correctly.
9. Second device connected simultaneously: no cache bleed, independent queues.
10. 10,000-photo gallery: first thumbnails inside a second; 4K H.265 video plays and seeks
    without downloading.
11. A protected path produces the §78 message, not an exception.

## Matrices to fill in (§79, §80)

**Devices**: Pixel, Samsung, Xiaomi, OnePlus, Motorola, Oppo, Vivo × multiple Android versions
× USB 2 / USB 3 / USB-C. Record per device: adb version, supported operations, `sha256sum`
present, `dd conv=notrunc` behavior, media directory layout quirks, known issues. Drive
`DeviceCapabilities` from observed behavior, never from version sniffing.

**Files**: empty, 1 KB, 1 MB, 1 GB+, 4 GB+ (the 32-bit boundary), very large directories,
unicode names, names with spaces, duplicate names differing only in case, hidden files.

**Media**: JPEG, HEIC, PNG, WebP, AVIF, MP4, H.265, HDR video, 4K video, and a file with a
wrong extension (to prove MIME sniffing works).
