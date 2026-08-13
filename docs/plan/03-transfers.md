# Phase 3 — File operations and the Transfer Manager

§97 phase 3, plus §9–§13 and §31–§37. **This phase is the one that makes the product useful**
— at the end of it, Handspan replaces MTP for daily work.

The transfer manager is what §11 calls "one of the most important components", and resume
(§13) is the feature the `adb` CLI fundamentally cannot provide, which is why phase 1 built
our own socket client.

## 3.1 Operations (§9)

Read: list, stat, open, download, stream. Write: upload file, upload directory, mkdir,
rename, move, copy, delete, recursive delete. Clipboard: copy, cut, paste — interoperating
with the OS clipboard so Ctrl+C in Explorer/Finder and Ctrl+V in the app works.

The UI never sees raw ADB (§10) — only `transferManager.UploadAsync(...)` /
`DownloadAsync(...)`.

Move within the device is `exec:mv` (cheap, no data crosses USB). Move across the boundary is
copy-then-delete with the delete gated on successful verification. Recursive delete uses
`rm -rf` with a **quoted** path (§71) and an explicit confirmation (§51 — there is no
recycle bin in v1).

## 3.2 The fast path

Fresh transfers use sync `SEND`/`RECV` with 64 KB chunks: byte-exact progress, immediate
cancellation, no shell involved.

Two invariants that always hold, in both directions:

1. **Write to `<name>.part`, then `mv` into place on success.** A partial file must never
   look complete — not in Explorer, not in the phone's gallery.
2. **Never overwrite a destination without inspecting it first** (§13). Conflict policy is
   resolved before a single byte moves.

## 3.3 Resume — the 1 MB alignment trick (§13)

Sync `SEND` always truncates and `RECV` always starts at zero, so resume needs `exec:`.
The design decision that makes this robust across OEMs is **aligning every resume offset to a
1 MB boundary**, which means we only ever need baseline `dd` semantics:

**Pull (device → PC).** Truncate the local `.part` down to `floor(len / 1MB) * 1MB`, then:

```
exec:dd if='<quoted path>' bs=1048576 skip=<K>
```

and append the stream. Block-aligned `skip` avoids depending on `iflag=skip_bytes` or
`tail -c +N`, both of which vary across toybox versions on real phones. Discarding under a
megabyte of already-transferred data is a trivial price for working everywhere.

**Push (PC → device).** Send the remainder as an ordinary sync `SEND` to a sibling temp file, append it,
then verify:

```
sync SEND  ->  '<path>.aepart.aeresume'
exec:cat '<path>.aepart.aeresume' >> '<path>.aepart'
exec:rm -f '<path>.aepart.aeresume'
```

> **Corrected 2026-08-13 after testing on hardware.** This originally piped the remainder into
> `dd of=… seek=K conv=notrunc` on the socket's stdin. That is wrong on a real device: `dd` issues one
> `read()` per block, and a socket read returns only what has arrived, so it writes short blocks and loses
> the rest — resuming a 3 MiB upload on a Galaxy S24 Ultra produced a 2.69 MiB file. It passed against the
> fake server for weeks because the fake modelled the documented behaviour rather than the real one.
> `iflag=fullblock` would fix it and is exactly the non-baseline option this design avoids, so the append
> route is now primary: it uses only sync `SEND` and `cat`, both proven by every other transfer.

**Recovery sources** all funnel into the same path (§13): USB disconnect, device reboot,
cable failure, PC sleep, adb restart, app crash. The first five are handled by the journal in
memory; the last one is why the journal is persisted.

**Later optimization:** `sendrecv_v2` with zstd where the device advertises it — a real win
on compressible data. Benchmark before enabling by default; it costs CPU and does nothing for
already-compressed photos and video.

## 3.4 `AdbRangeStream`

A seekable, read-only `Stream` over a remote file, built on the same range-read mechanism.
Phase 3 uses it for metadata sniffing; **phase 4's thumbnail extraction and video streaming
depend on it entirely**, which is why it lands here where its tests are simple.

## 3.5 `TransferManager` (§11)

State per job: source, destination, direction, total size, bytes transferred, percentage,
current speed (EWMA over a sliding window), ETA, status, error, retry count.

Statuses, exactly §11: **Queued, Preparing, Transferring, Paused, Completed, Failed,
Cancelled, Retrying.**

### Persistence

Jobs are journaled to SQLite, so **resume survives an app crash**, not merely a cable pull
(§13). On startup, incomplete jobs are restored as `Paused` with their `.part` files intact
and offered for resume — never auto-restarted without the user's device being present.

### Scheduler (§12)

Per-device concurrency, size-classified:

| Class | Threshold | Default streams |
|---|---|---|
| Small | < 8 MB | 4 parallel |
| Large | ≥ 8 MB | 1–2 |

Configurable in settings. The spec is right that more parallelism is not automatically faster
— USB and the adb server are the real bottleneck — so ship a **benchmark hook** that measures
actual throughput at 1/2/4/8 streams and records it in the device profile, rather than
guessing.

### Progress and threading

Progress events are throttled to ~8 Hz before touching the UI; a 64 KB chunk callback at full
USB 3 speed fires over a thousand times a second and will destroy frame rate if forwarded
naively. No ADB work on the UI thread (§46), cancellation everywhere (§47).

### Bulk operations

Directory transfers enumerate recursively during `Preparing` to produce the §34 preview:

```
Copy 428 files?
Source: Android/DCIM/Camera → C:\Pictures\Phone
Total: 428 files, 7.82 GB          [Cancel] [Start Transfer]
```

Enumeration itself is cancellable and streams counts as it goes, so a huge tree doesn't look
frozen.

### Conflicts (§35, §36)

Replace / Skip / Rename / Compare, with **apply to all**. The compare view shows existing vs
incoming size and modified time, with hashing only on explicit request — hashing every file
over a phone connection is expensive and the spec calls it out (§36).

### Verification (§37)

Size is always verified. SHA-256 is optional: device-side `sha256sum` compared against a
local hash, retried on mismatch. Off by default for the same cost reason.

### Failure behavior

- Device disconnects → **all jobs for that device go `Paused` with a reason**, and the UI says
  "Transfer paused because Galaxy S25 was disconnected" (§38, §86).
- Same serial reappears → auto-resume from the journal.
- Transient errors → auto-retry with exponential backoff, default 3 attempts, then `Failed`
  with a human sentence.
- Destination disk full, destination folder vanished, phone storage unavailable → specific
  typed exceptions and specific messages, never a generic failure (§81 covers the full list;
  hardening completes in phase 6).

### Transfers page (§85)

Active / Completed / Failed tabs with persistent history grouped by day. Per-job pause,
resume, cancel, retry, and "open containing folder". Completion notifications via
`IPlatformNotifications` (§86).

## 3.6 Drag and drop (§31)

**Inbound** (Explorer/Finder → app): Avalonia `DataFormats.Files`, dropping files, folders and
multi-selections onto a target directory or the sidebar. Identical on both OSes.

**Outbound** (app → Explorer/Finder) is the genuinely hard direction, because the files don't
exist locally yet. Phase 3 ships the portable approach: on drag start, stage the selection
into the cache directory with a progress dialog, then hand over real paths.

It sits behind `IShellDragService`, so the on-demand upgrade is a drop-in replacement in
phase 6 — Windows `CFSTR_FILEDESCRIPTORW` + `CFSTR_FILECONTENTS`, macOS
`NSFilePromiseProvider` — letting a 40-photo drag stream on demand instead of pre-staging.
Staging first is the right order: it works everywhere on day one, and the native shims are
pure optimization rather than a prerequisite.

## Implementation notes (2026-08-12)

**The journalled resume point is measured, not counted.** Progress reports are throttled to ~8 Hz, so a
transfer that dies inside the first throttle window used to journal zero bytes — which lost the resume
point and made `IsResumable` return false, blocking resume from the UI. On failure or pause the engine
now *measures* the partial (local file length, or a remote stat of the `.aepart`) before journalling.
Found by the interrupted-upload test, and it would have been a real bug on fast connections.

**`.aepart` is the staging suffix** in both directions. Downloads append to a local `.aepart` and
`File.Move` on success; uploads write a device-side `.aepart` and `mv` on success. Cancelling deletes it;
pausing keeps it, which is the whole difference between the two.

**Android filenames are sanitized for the local filesystem.** `:` `*` `?` `"` `<` `>` `|` and trailing
dots are legal on Android and illegal on Windows, so a download of `what:is*this?.txt` would otherwise
fail with an obscure IO error (§74).

**Windows-hostile timing in tests.** Interrupting a live loopback transfer at an exact byte offset is
inherently racy, so the resume paths are tested by placing an aligned partial deterministically and
asserting the `dd skip=`/`seek=` command that follows. The interruption itself is tested separately and
tolerates either outcome. Racing the fault injector made the test flaky without testing resume harder.

## Phase 3 exit criteria

Verified against `FakeAdbServer` (16 transfer tests, all passing):

- [x] **Interrupted at 3.2 of 5 units, resumed, byte-identical result** — §13's worked example, scaled
      to MiB to keep the test fast. Asserts the resume went through `dd bs=1048576 skip=3`, not a restart.
- [x] Upload resume writes with `seek=3 conv=notrunc` and produces a byte-identical file.
- [x] `.aepart` never appears as a completed file; cancel deletes it, pause keeps it.
- [x] A crash-interrupted job is restored as `Paused` with a journalled resume point, and completes.
- [x] A failed job survives a restart as `Failed`, stays resumable, and completes on resume.
- [x] Auto-retry recovers a transient failure and records the attempt count.
- [x] Scheduler honours the small-file concurrency limit (peak concurrency asserted).
- [x] Directory download expands to one job per file, preserving the tree and unicode names.
- [x] Skip and Rename conflict policies behave; optional SHA-256 verification runs on the device.
- [x] A Windows-illegal Android filename downloads under a sanitized name.

Verified on a Galaxy S24 Ultra (2026-08-13):

- [x] **Throughput: 38.0–38.8 MB/s** pulling 195 MB — the number that justifies this over MTP.
- [x] Upload → device-side SHA-256 → download round-trips byte-identically.
- [x] **Resumed upload and resumed download both reassemble the original exactly** — after the `dd` bug above.
- [x] The transfer manager completes a real download and leaves no `.aepart` behind.

Still pending:

- [ ] Physically pull the cable mid-transfer, reconnect, confirm resume (needs a human hand on the cable).
- [ ] Drag files in from Explorer and confirm the queue behaves on a real device.

With a real device: push and pull a 1 GB+ file and record throughput; **pull the cable
mid-transfer and watch pause → reconnect → resume**; delete, rename, mkdir, move within the
device; drag a folder each direction; transfer a tree containing unicode filenames and verify
names round-trip exactly.
