# Icon generation

Every Handspan icon is drawn from code so the marks are reproducible and reviewable, rather
than binaries nobody can regenerate. Nothing here ships, and this project is deliberately
**not** in `Handspan.sln` — it is run by hand when the design changes.

```sh
cd build/icon
dotnet run -- ../..          # the argument is the repository root
```

Note the `--`. Without it, `dotnet run` forwards its own arguments to the program and the
first one is taken as the repo root, which scatters the output into a directory named after
the flag.

## What it writes

| Path | Used by |
|---|---|
| `src/Handspan.App/Assets/handspan.ico` | `<ApplicationIcon>` — the `.exe` icon Explorer and the taskbar show. Carries 16, 20, 24, 32, 40, 48, 64, 128, 256 |
| `src/Handspan.App/Assets/handspan-*.png` | `Window.Icon` in `MainWindow.axaml`. A PNG rather than the `.ico` because Skia, which decodes Avalonia's bitmaps, does not read ICO |
| `build/AppIcon.icns` | Copied into `Contents/Resources` by `make-app-bundle.ps1`, matching the `CFBundleIconFile` key in `Info.plist` |
| `docs/assets/logo.png` | The README |

## The design

Two piers with a deck overhanging them: a span across supports, which is what the name means
and which also reads as an **H**.

It got there by elimination, testing each candidate at 16 px rather than admiring it at 512.
A literal suspension bridge — towers, catenary, deck — is legible at 128 and complete mud at
16, because three elements cannot survive 256 pixels of total area. A bare arch reads as a
lowercase "n". An arch standing on a deck reads as a lamp. Bowing the H's crossbar slightly
looks like a mistake rather than a decision, and bowing it enough to read as an arch makes the
crossbar visually detach at small sizes.

What survived was the letterform, which is guaranteed legible, with the deck extended past
the piers so it reads as a span resting on supports rather than a default H. That costs
nothing at 16 px.

Below 24 px the corner radius is tightened and the strokes thickened by about a tenth.
Proportions that look right at 512 look thin and over-rounded when the whole tile is 16
pixels across.

The gradient is teal 600 to cyan 500. A graphite tile with a teal mark was tried and rejected:
on a dark taskbar it dissolves into the background. Blue reads as generic Windows chrome,
amber reads as a warning, and emerald reads as a finance app.

## Containers

Both are written by hand, because .NET has no cross-platform ICO or ICNS encoder.

- **ICO** — a Vista-or-later icon directory where each entry is a whole PNG rather than a DIB.
  A width byte of 0 means 256, which is why the 256 entry looks empty in a hex dump. Old
  `System.Drawing` on .NET Framework cannot select that entry and falls back to 128; that is
  its limitation, not a malformed file.
- **ICNS** — the `icns` magic, a big-endian total length, then typed entries of
  `[4-byte type][4-byte big-endian length including the header][PNG]`. Retina variants
  (`ic11`–`ic14`) are the same pixel sizes as their non-retina counterparts, which is correct:
  the type code, not the dimensions, tells macOS the scale factor.
