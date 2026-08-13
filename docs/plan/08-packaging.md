# Phase 8 — Packaging and distribution

Can start as soon as phase 3 lands, since end-of-phase-3 is the natural first release.

## Status (2026-08-13)

`build/publish.ps1` produces self-contained single-file builds for `win-x64`, `win-arm64`, `osx-x64` and
`osx-arm64`; `build/make-app-bundle.ps1` wraps the macOS output in a `.app` with an `Info.plist`. The
Windows build has been verified to launch on this machine with no .NET installed alongside it.

**Trimming is off, deliberately.** Avalonia resolves XAML types by reflection and the trimmer removes them
silently — the app publishes cleanly and then fails at runtime pointing nowhere useful. Not worth the saving.

**Native debug symbols are excluded.** SkiaSharp and HarfBuzz ship `.pdb` files totalling ~105 MB, larger
than the app itself. They arrive as native runtime assets, which `AllowedReferenceRelatedFileExtensions`
does not cover, so a `RemoveNativeSymbolsFromPublish` target filters them out of the publish list. This took
the Windows build from **210 MB to 105 MB**.

Still requiring credentials that cannot be scripted here: Authenticode signing, Apple Developer ID signing,
notarization and stapling, and the installer wrappers (Inno Setup / DMG).

## Windows

- Self-contained `win-x64` and `win-arm64`, single-file, trimmed only if verified safe
  (Avalonia + reflection-based XAML can break under aggressive trimming — measure, don't
  assume).
- **Installer**: Inno Setup for a plain per-user install, or MSIX if Store distribution is ever
  wanted. Per-user by default so no admin prompt.
- **Authenticode signing** — without it, SmartScreen will scare users away from a file-transfer
  utility, which is exactly the wrong first impression.
- Ship `platform-tools` under `tools/`, or fetch on first run (§4.1) to keep the installer small.

## macOS

- `osx-x64` + `osx-arm64`, combined into a universal binary with `lipo`, in a proper `.app`
  bundle with `Info.plist`, icon set, and `LSMinimumSystemVersion`.
- **Hardened runtime**, `codesign`, **notarization**, and staple — unsigned or un-notarized
  builds are blocked by Gatekeeper on modern macOS. Requires an Apple Developer ID
  (~$99/year), which is a real prerequisite to plan for.
- DMG with the usual drag-to-Applications layout.
- The bundled `adb` needs the exec bit and its quarantine attribute cleared, and must be signed
  as part of the bundle or it won't launch.

## Both

- Version stamping from git tags; a visible build id in Settings → About for support.
- **Optional auto-update**: Velopack (MIT) supports both platforms and is the least-friction
  option. Evaluate rather than assume, and keep updates opt-in — the app must remain fully
  functional offline (§44).
- License texts shipped for all third-party components, including the LGPL notices for ffmpeg
  and LibVLC if phase 4 uses them, plus a written offer for their source as LGPL requires.
- Crash reporting stays **optional and opt-in**, and must never include filenames or paths
  (§43).

## CI

- Build + test on Windows and macOS runners.
- `dotnet build -r osx-arm64` on the Windows job as the cross-platform guard, so a
  Windows-only API can't merge unnoticed.
- Publish artifacts per platform on tags; signing keys from CI secrets, never in the repo.

## Release checklist

1. All phase exit criteria met for the phases being shipped.
2. `dotnet test` green on both OSes.
3. Manual pass of the live device checklist in `09-testing.md`.
4. Fresh-machine install test: no .NET installed, no adb present, no device authorized — the
   first-run walkthrough must carry a new user all the way to a successful transfer. This is the
   single most important test, because it's the only one every user runs.
5. Uninstall leaves no orphaned cache or adb server running.
