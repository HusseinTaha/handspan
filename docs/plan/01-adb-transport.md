# Phase 1 — ADB transport

The foundation; everything else is a client of this. Corresponds to §97 phase 1 and §71–§73.

We speak the adb **host protocol** over localhost TCP ourselves, and use Google's `adb`
binary only to launch the server. We never touch the USB wire protocol — that stays Google's
job, which is what §98's warning is actually about.

```
AndroidExplorer  --TCP 127.0.0.1:5037-->  adb server  --USB-->  device
```

## 1.1 Server management (§72)

`AdbServerManager` responsibilities:

- Locate the binary via `IAdbBinaryProvider`.
- Probe `host:version`. If nothing answers, run `adb start-server`.
- **If a different version's server is already running** — common with Android Studio or
  scrcpy — warn the user and continue. **Never silently kill someone else's server.**
- Capture stdout/stderr, exit codes, and enforce timeouts on the binary invocation.
- Reconnect with exponential backoff (250 ms → 8 s cap) when the server dies mid-session.
- Expose `RestartServerAsync()` and `RescanAsync()` for the diagnostics page (§49).

### Binary discovery order

1. Bundled `tools/platform-tools/`
2. `PATH`
3. `ANDROID_HOME` / `ANDROID_SDK_ROOT`
4. Windows: `%LOCALAPPDATA%\Android\Sdk\platform-tools`
   macOS: `~/Library/Android/sdk/platform-tools`, `/opt/homebrew/bin`, `/usr/local/bin`
5. User-configured path from settings (§50)

If none is found, offer a **consented one-click download** of
`platform-tools-latest-{windows,darwin}.zip` from Google into app data. Never silent — the
app must work fully offline afterwards (§44). Pin a known version, record it, and verify the
archive's hash. On macOS, clear the `com.apple.quarantine` xattr and set the exec bit, or
the extracted binary won't run.

## 1.2 Wire protocol (`AdbSocketClient`)

Framing: every request is **4 hex length digits + ASCII payload**. Responses are `OKAY` or
`FAIL`, the latter followed by a length-prefixed reason.

```
send: "0012host:track-devices"
recv: "OKAY"  then repeatedly: 4-hex length + payload on every device-list change
```

### Host services

| Service | Purpose |
|---|---|
| `host:version` | Server presence and version check |
| `host:devices-l` | One-shot device list, long form |
| `host:track-devices` | **Push-based hotplug.** Long-lived socket streaming the device list on every change |
| `host-serial:<s>:features` | Feature negotiation |
| `host-serial:<s>:get-state` | State of one device |
| `host:transport:<serial>` | Bind this socket to a device, then send one local service below |
| `host:connect` / `host:disconnect` / `host:pair` | Wireless (phase 6) |
| `host:forward:...` | Companion channel (phase 7) |
| `host:kill` | Only ever on explicit user request |

`host:track-devices` is why device detection needs no polling and comes in well under §45's
2-second target — and it behaves identically on Windows and macOS, so hotplug needs no
platform code at all. That is a notable win: the usual approach (WMI device notifications on
Windows, IOKit on macOS) is entirely avoided.

### Local services (after `host:transport:`)

| Service | Use |
|---|---|
| `sync:` | `LIS2`/`LIST`, `STA2`/`STAT`, `SEND`, `RECV` — the workhorse |
| `exec:<cmd>` | Raw stdout **and stdin**, no PTY, no CRLF mangling. The key to resume and range reads |
| `shell,v2,raw:<cmd>` | When exit code and separated stderr matter |
| `shell,raw:<cmd>` | Fallback when `shell_v2` is unavailable |

Plain `shell:` is avoided: on older devices it applies PTY line-ending translation that
corrupts binary data — a classic source of "the file transferred but is subtly broken".

### Feature negotiation

Parse `host-serial:<s>:features` into `DeviceCapabilities`. The four that change behavior:

| Feature | Effect when absent |
|---|---|
| `stat_v2` | Fall back to `STAT`: 32-bit size, no error field |
| `ls_v2` | Fall back to `LIST`: 32-bit size per entry |
| `shell_v2` | No separated stderr or exit code; use `exec:` and infer |
| `sendrecv_v2` | No compressed transfer path |

## 1.3 Sync protocol

Messages are an 8-byte header — 4 ASCII id bytes + a 4-byte little-endian length or param —
followed by payload. Data chunks max out at **64 KB**.

| Request | Response |
|---|---|
| `STAT` + path | `STAT` + mode(4) + size(4) + mtime(4) |
| `STA2` + path | `STA2` + 72-byte struct: error, dev, ino, mode, nlink, uid, gid, **size(8)**, atime, mtime, ctime |
| `LIST` + path | stream of `DENT` + mode + size + mtime + namelen + name, ended by `DONE` |
| `LIS2` + path | stream of `DNT2` + v2 stat struct + namelen + name, ended by `DONE` |
| `SEND` + "path,mode" | then `DATA`+len+bytes (repeat), `DONE`+mtime → `OKAY` or `FAIL`+reason |
| `RECV` + path | `DATA`+len+bytes (repeat), then `DONE` |
| `QUIT` | ends the sync session |

> Verify the exact `STA2` struct layout against AOSP `file_sync_service.h` while
> implementing, and lock the parse down with `FakeAdbServer` round-trip tests. Getting a
> field offset wrong here produces plausible-looking garbage sizes rather than a clean error.

## 1.4 Listing and stat — no `ls -la` parsing (§73)

`ListAsync` uses `LIS2`, falling back to `LIST` when `ls_v2` is absent. Entries arrive as
mode/size/mtime/name **records**, so filenames containing spaces, quotes, newlines, emoji and
RTL text are safe *by construction* rather than by escaping (§74). This is the whole reason
the spec forbids text parsing.

Three details that matter:

- **`LIST` v1 sizes are 32-bit and wrong above 4 GB.** When `stat_v2` is available, prefer
  `STA2` for sizes, and always for single-file info. When it isn't, flag entries at exactly
  `0xFFFFFFFF` as "size unknown" rather than displaying a lie.
- **Symlinks.** `/sdcard` is typically a link to `/storage/emulated/0`. Entries report mode
  `LNK`; resolve them with `STA2` so they present as directories and are navigable.
- **Timestamps** are Unix-UTC seconds → convert to local time for display (§25), and never
  round-trip a displayed local time back into a protocol call.

Root at `/sdcard`, presented as **"Internal Storage"** (§16). Detect additional volumes under
`/storage/*` (SD card, USB OTG) and present those too. Do not offer `/system`, `/data`,
`/vendor`, `/apex`; UI copy is "accessible Android storage", never "the entire filesystem"
(§17).

## 1.5 Shell safety (§71)

One helper, used everywhere:

```csharp
static string ShellQuote(string arg) => "'" + arg.Replace("'", "'\\''") + "'";
```

It is the **only** route from user input to a command line, and `sync:`/`exec:` are preferred
over shell entirely. Unit-tested against: `it's`, `$HOME`, `` `whoami` ``, `$(id)`,
`; rm -rf /`, a literal newline, `صور العائلة`, `照片`, `旅行 🌴`.

## 1.6 Device info (§6)

One batched `getprop` per session: `ro.product.manufacturer`, `ro.product.model`,
`ro.build.version.release`, `ro.build.version.sdk`. Storage via
`stat -f -c '%b %a %S' /sdcard` → total/free/block size. Battery best-effort from
`/sys/class/power_supply/battery/capacity`, falling back to `dumpsys battery`. USB speed
where detectable.

All of it non-blocking and failure-tolerant: **a missing battery reading must never block
browsing.** Populate the dashboard progressively as each answer arrives.

## 1.7 Validation against the real server — done first, instead of `FakeAdbServer`

**Deviation from the original plan, 2026-08-12.** A real device became available, so the packet
layouts were validated against Google's own ADB server rather than against a fake we also wrote —
which is strictly stronger evidence, because a fake would agree with our own misreading of the
protocol.

`tests/AndroidExplorer.Adb.Tests/RealAdbServerTests.cs` runs the transport against the live server and
compares its answers with the adb CLI's, skipping cleanly when no device or binary is present.
Confirmed so far: protocol version negotiation, `host:devices-l` parsing **matching the CLI exactly**,
`host:track-devices` pushing an immediate snapshot, and an unauthorized device surfacing as
`DeviceUnauthorizedException` rather than a protocol error.

Still pending an authorized device: structured listing of `/sdcard`, `stat` agreeing with listing for
the same file, `/sdcard` symlink resolution, missing-path errors, and the device dashboard probe.
These are the ones that exercise the `stat_v2` field offsets, where a wrong offset yields plausible
garbage rather than an error — so they are the tests that matter most.

## 1.8 `FakeAdbServer` — still worth building

A loopback TCP server in `tests/AndroidExplorer.Adb.Tests` that speaks the real host + sync
protocol against an in-memory filesystem, with injectable faults:

- drop the connection after N bytes (cable pull)
- return `FAIL` for a given path (permission denied)
- throttle throughput (slow USB 2)
- advertise or withhold `stat_v2` / `ls_v2` / `shell_v2` / `sendrecv_v2`
- emit `device` → `offline` → `unauthorized` transitions on the track-devices stream
- serve a synthetic 5 GB sparse file for resume tests

Even with hardware available, this remains the only way to test the failure paths reproducibly: you
cannot ask a real phone to drop its connection at byte 3,355,443,200 on demand. Phase 3's resume tests
depend on it.

## Phase 1 exit criteria

`dotnet test` green against `FakeAdbServer` for:

1. Request framing, `OKAY`/`FAIL` parsing, length prefixes.
2. `track-devices` stream deltas → correct `DeviceChanged` events, including
   `unauthorized` → `device`.
3. Feature negotiation with and without each of the four features.
4. Listing filenames with spaces, `'`, `"`, newline, emoji, Arabic, CJK.
5. A >4 GB file reporting a correct size via `STA2`, and the v1 path flagging unknown rather
   than truncating.
6. Symlinked `/sdcard` resolving to a navigable directory.
7. `ShellQuote` injection battery.
8. Server restart and reconnect-with-backoff behavior.

Plus, with a real device when available: detection under 2 seconds, correct state
transitions, `getprop` and storage figures matching the phone's own Settings screen.
