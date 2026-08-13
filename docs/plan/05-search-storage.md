# Phase 5 — Search, index, favorites, storage analyzer

§97 phase 5, plus §27–§29 and §61–§67. Completes the spec's v1.1 feature set (§90).

The governing rule (§28): **never recursively scan the phone on every query.** Search is a
local index lookup; the device is only touched to keep the index fresh.

## 5.1 Index schema

`Files` (§28) and `Media` (§59) in SQLite, both keyed by `DeviceId` (§39):

```sql
CREATE TABLE Files (
  Id INTEGER PRIMARY KEY, DeviceId TEXT NOT NULL, ParentId INTEGER,
  Name TEXT NOT NULL, Path TEXT NOT NULL, Extension TEXT, MimeType TEXT,
  Size INTEGER, ModifiedTime INTEGER, IsDirectory INTEGER, MediaType INTEGER
);
CREATE UNIQUE INDEX IX_Files_Device_Path ON Files(DeviceId, Path);
CREATE INDEX IX_Files_Device_Size ON Files(DeviceId, Size DESC);
CREATE INDEX IX_Files_Device_Modified ON Files(DeviceId, ModifiedTime DESC);

CREATE VIRTUAL TABLE FilesFts USING fts5(
  Name, content='Files', content_rowid='Id',
  tokenize="unicode61 remove_diacritics 2"
);
```

`unicode61 remove_diacritics 2` matters: without it, Arabic, CJK and accented filenames don't
match properly (§74), and this app is explicitly built to handle them. Keep FTS in sync with
triggers, and store `Path` case-preserved but index a folded copy for matching.

## 5.2 Indexer

Breadth-first crawl using sync `LIST` — thousands of entries per second, since it's one socket
and no per-file round trip. Properties:

- **Incremental**: compare size + mtime against the stored row; only changed subtrees are
  rewalked. A full recrawl is a user action, not routine.
- **Skips by default**: `/sdcard/Android/data` and `/sdcard/Android/obb` (huge, permission-
  fraught, uninteresting), plus directories containing `.nomedia` for media indexing.
- **Cancellable with progress** (§47), and yields to foreground work — indexing must never
  make browsing feel slow.
- Runs per device, on connect, and writes into a transaction batch (a few thousand rows at a
  time) rather than row-by-row.

## 5.3 Search UI (§27)

Instant results from the index as the user types, with filters:

- **Type**: images / videos / audio / documents (from `MediaType` + MIME)
- **Size**: <10 MB / 10–100 MB / >100 MB / custom
- **Date**: today / this week / this month / custom range

Results are grouped by folder with a "reveal in Explorer view" action. If a path hasn't been
indexed yet, offer a live device search for that subtree rather than silently returning
nothing — and say which mode produced the results, so the user can trust them.

## 5.4 Favorites, Quick Access, Recent

- **Favorites** (§65): pin any directory — `DCIM/Camera`, `Download`, `WhatsApp/Media`.
  Stored locally, **per device** (§67).
- **Quick Access** (§66): the sidebar section, reorderable, with sensible defaults on first
  connect.
- **Recent** (§64): a virtual folder grouped Today / Yesterday / This Week, computed from the
  index. No physical Android directory involved.
- **Per-device profiles** (§67): DeviceId, DisplayName (user-renameable), LastConnected,
  favorites, view/sort/gallery preferences, benchmark results from phase 3.

## 5.5 Storage analyzer (§62) — a real differentiator

Computed entirely from the index, so it's instant after the first crawl:

```
256 GB total
Photos      82 GB  ████████████
Videos      61 GB  █████████
Apps        38 GB  █████
Downloads   14 GB  ██
Other       21 GB  ███
```

Click a category → largest files, sorted descending (§63), with thresholds >1 GB / >500 MB /
>100 MB. Plus a directory treemap for "what is actually eating my storage", drillable, with
delete and download actions inline — the point is to *act* on the finding, not just view it.

Note honestly in the UI that "Apps" is inferred from `/sdcard/Android` plus total-vs-accounted
difference; true per-app storage needs APIs beyond plain ADB (that's phase 7 territory).

## 5.6 Duplicate detection (§61)

Escalating cost, stopping as early as possible:

1. **Group by size** — anything unique by size is not a duplicate. Free, from the index.
2. **Filename similarity** — `IMG001.jpg`, `IMG001 (1).jpg`, `IMG001-copy.jpg`. Free.
3. **Partial hash** — 64 KB head + 64 KB tail via `AdbRangeStream`. Cheap, kills almost all
   remaining false groups.
4. **Full hash** — device-side `sha256sum`, only for candidates that survive step 3.

Present groups with a suggested keep (largest / newest / shortest name) and require explicit
confirmation before deleting anything.

## Implementation notes (2026-08-12)

**Two search passes, not one.** FTS5 tokenizes on separators, so `invoice` finds `old-invoice.jpg` and
prefix matching finds `invoice-2026.pdf` while the user is still typing — but FTS cannot match mid-token, so
`voice` would miss `invoice` entirely. A `LIKE` pass fills that gap and the results are unioned, token
matches first. Query terms are quoted before reaching MATCH, or a filename containing `"` or `(` would be
read as FTS syntax and throw.

**`remove_diacritics 2` in the tokenizer** is what lets someone type `resume` and find `résumé.pdf`. Without
it, users cannot find their own files by typing ASCII — verified by test.

**Unaccounted storage is named, not guessed.** The index only sees what Android lets us read, so the
difference between the volume's used bytes and the indexed total is reported as "used by apps and areas
Android does not allow reading" rather than being attributed to an invented "Apps" category.

**Duplicate cost ordering is enforced by test.** Size grouping (free, from the index) → head *and* tail
sample via range reads (cheap) → full device-side hash (expensive, opt-in). Sampling both ends matters:
files from the same camera share a header, and head-only sampling would group them wrongly. A test asserts
`sha256sum` runs exactly twice for two survivors among four same-size candidates — the claim would rot
silently otherwise.

### Favourites and Quick Access — done (2026-08-13)

A pin button in the Explorer toolbar toggles the current folder, and pinned folders appear in a Quick Access
sidebar (§65, §66). They are stored per device (§67), so two phones do not share pins.

First connection seeds defaults — Camera, Download, Pictures, Movies — but **only those the device actually
has**, checked with an existence probe. Suggesting WhatsApp on a phone without it would be noise, and §26
warns against assuming any particular layout.

### Still deferred

The Recent virtual folder (§64). It is a query over the existing index rather than new machinery, so it is
small work whenever it is wanted.

## Phase 5 exit criteria

Verified by tests (19 index tests + 8 duplicate tests):

- [x] Token, prefix and mid-token (substring) matching all work.
- [x] Arabic, CJK, Korean and emoji filenames are found; **diacritics are folded** so `resume` finds
      `résumé.pdf`.
- [x] Query punctuation (`"`, `(`, `)`) cannot break MATCH syntax.
- [x] Filters compose: kind + size + date + subtree scope.
- [x] Two devices keep separate indexes (§39); re-indexing updates rather than duplicating.
- [x] Deleted files are pruned from **both** the table and the FTS shadow table.
- [x] Storage aggregation by category, totals, largest-files threshold, per-folder breakdown.
- [x] Duplicates: identical files grouped with reclaimable bytes; same-size-different-content rejected;
      **same-header-different-tail rejected**; full hashing skipped unless asked for, and then run only on
      survivors (**exactly 2 of 4 candidates hashed**); missing `sha256sum` degrades to an honest
      "very likely identical" verdict.

Verified on a Galaxy S24 Ultra (2026-08-13):

- [x] Crawled **7,710 entries from DCIM in 2.6 s** (~3,000 entries/second).
- [x] **Search returned 500 matches in 21 ms** — comfortably inside the sub-100 ms target (§45).
- [x] Storage aggregation over real data: 7,693 files, 85.7 GB, across three categories, computed entirely
      from the index with no further device access.
- [x] Volume capacity read from the device: 190.1 GB used of 221.8 GB.

Still pending:

- [ ] A full-device crawl (not just DCIM) at the 50,000-file scale, confirming the crawl stays cancellable
      and browsing stays responsive throughout.
