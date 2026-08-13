# Third-party notices

Android Explorer is distributed under the MIT License (see [LICENSE](LICENSE)). It also
includes, or links against, the third-party components listed below. Every one of them is
under a permissive licence — **there is no copyleft component in this product**, and nothing
here restricts commercial use or requires derivative works to be published.

Licences were read from the `.nuspec` of each resolved package version, not from memory.
The full dependency graph is reproducible with `dotnet list package --include-transitive`.

## MIT

Full text at the end of this file.

| Component | Version | Copyright |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Skia, Avalonia.Native, Avalonia.Win32, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.HarfBuzz, Avalonia.Remote.Protocol, Avalonia.BuildServices | 12.1.1 (BuildServices 11.3.2) | © The Avalonia Project |
| CommunityToolkit.Mvvm | 8.4.2 | © .NET Foundation and Contributors |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.11 | © Microsoft Corporation |
| Microsoft.Extensions.* (Hosting, DependencyInjection, Logging, Configuration, Options, Primitives, FileProviders, Diagnostics and their Abstractions) | 10.0.11 | © Microsoft Corporation |
| Microsoft.Win32.SystemEvents, System.Diagnostics.EventLog | 10.0.11 | © Microsoft Corporation |
| SkiaSharp and SkiaSharp.NativeAssets.{Win32, macOS, Linux, WebAssembly} | 4.151.1 (Linux/Wasm 3.119.4) | © Microsoft Corporation |
| HarfBuzzSharp and HarfBuzzSharp.NativeAssets.{Win32, macOS, Linux, WebAssembly} | 8.3.1.3 | © Microsoft Corporation |
| MicroCom.Runtime | 0.11.6 | © Nikita Tsukanov |
| Tmds.DBus.Protocol | 0.94.1 | © Tom Deseyn |

## Apache License 2.0

Full text: https://www.apache.org/licenses/LICENSE-2.0

| Component | Version | Copyright |
|---|---|---|
| MetadataExtractor | 2.9.3 | © Drew Noakes and contributors |
| Serilog | 4.2.0 | © Serilog Contributors |
| Serilog.Extensions.Logging | 10.0.0 | © Serilog Contributors |
| Serilog.Sinks.File | 7.0.0 | © Serilog Contributors |
| SQLitePCLRaw.core, .bundle_e_sqlite3, .provider.e_sqlite3, .lib.e_sqlite3 | 2.1.12 | © Eric Sink |

## BSD 3-Clause

| Component | Where it comes from |
|---|---|
| **Skia** (native `libSkiaSharp`) | © Google LLC — the graphics engine inside SkiaSharp |
| **ANGLE** (`Avalonia.Angle.Windows.Natives` 2.1.27548) | © The ANGLE Project Authors, TransGaming Inc., Google Inc., 3DLabs Inc. Ltd. — OpenGL-over-Direct3D on Windows |

Neither the names of the copyright holders nor of their contributors may be used to endorse
or promote products derived from this software without specific prior written permission.

## Adobe XMP Library licence

**XmpCore 6.1.10.1** — © 2015–2021 Drew Noakes and contributors, a .NET port of Adobe's Java
XMP SDK, pulled in transitively by MetadataExtractor. Terms:
https://www.adobe.com/devnet/xmp/library/eula-xmp-library-java.html — a BSD-3-Clause-form
permissive licence that allows redistribution in binary form with attribution.

## SIL Open Font License 1.1

**Inter** typeface, embedded in `Avalonia.Fonts.Inter` — © 2016–present The Inter Project
Authors (https://github.com/rsms/inter). The OFL permits bundling and redistribution,
including in commercial products. The font is used unmodified and is not sold on its own.
Full text: https://openfontlicense.org

## Public domain

**SQLite** — the `e_sqlite3` native library shipped inside `SQLitePCLRaw.lib.e_sqlite3`. The
SQLite authors have dedicated the code to the public domain and disclaim copyright. See
https://www.sqlite.org/copyright.html

## Libraries statically linked into `libSkiaSharp`

Skia's prebuilt native binaries embed several upstream libraries. All are permissive; the
notable one is FreeType, called out because it is the single place in this product where a
GPL option exists — and it is only an *option*:

| Library | Licence |
|---|---|
| **FreeType** | FreeType Licence (FTL) *or* GPLv2, at the recipient's choice. **This project relies on the FTL**, a BSD-style attribution licence. No GPL obligation arises. |
| libpng | PNG Reference Library License v2 (permissive) |
| libjpeg-turbo | IJG / BSD-3-Clause / zlib |
| libwebp | BSD 3-Clause (© Google Inc.) |
| zlib | zlib licence |
| Dawn / Vulkan headers, expat, wuffs | permissive (BSD / MIT / Apache-2.0) |

## Android Debug Bridge (`adb`)

Not part of this repository and **not redistributed** by it. Android Explorer locates an
existing `adb` on the machine, or — only with explicit user consent — downloads Google's
official `platform-tools` archive at runtime into the user's own application data folder.
Google's platform-tools are licensed under the **Apache License 2.0**.

> If a future release *bundles* `platform-tools` in the download, that release must ship the
> Apache 2.0 licence text alongside it. Downloading on the user's behalf, as the app does
> today, does not create a redistribution obligation.

Android Explorer speaks the documented ADB **host and sync protocol** over a local TCP
socket. It contains no Android Open Source Project code, and it does not attempt to
circumvent ADB authorization, device encryption, or any other part of Android's security
model.

## Development-only dependencies

Not shipped, listed for completeness: xunit 2.9.3 and xunit.runner.visualstudio 3.1.4
(Apache-2.0), Microsoft.NET.Test.Sdk 17.14.1 (MIT), coverlet.collector 6.0.4 (MIT).

## Trademarks

Android is a trademark of Google LLC. Android Explorer is an independent project with no
affiliation with, endorsement by, or sponsorship from Google.

---

## MIT License (full text)

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
