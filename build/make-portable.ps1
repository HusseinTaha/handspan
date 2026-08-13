<#
.SYNOPSIS
    Turns a published Windows build into a portable one and zips it.

.DESCRIPTION
    Called by publish.ps1 -Portable. Adds the three things a published folder is missing before it can be
    handed to someone: the marker file that puts the app into portable mode, the licences it is obliged to
    carry, and a plain-text explanation of what to do with it.

    The zip has exactly one top-level directory, so extracting it in Downloads produces a folder rather
    than scattering 40-odd files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $PublishDir 'Handspan.exe'
if (-not (Test-Path $exe)) {
    throw "no Handspan.exe in $PublishDir - did the publish succeed?"
}

# Presence is the whole signal; PortableMode.cs never reads the contents, which frees the file to say what
# it is. Someone finding it in the folder should be able to work out what it does without the README.
$markerText = @"
The presence of this file tells Handspan to keep everything it writes in the "Data" folder
next to Handspan.exe, instead of in your Windows user profile.

That means settings, the file index, thumbnail caches, logs and (if you let it download one)
a copy of adb all travel with this folder. You can run it from a USB stick and leave nothing
behind on the PC.

Delete this file if you would rather Handspan store its data in the usual per-user location,
%LOCALAPPDATA%\Handspan.

Nothing reads the text in this file. Only its name matters.
"@

Set-Content -Path (Join-Path $PublishDir 'Handspan.portable') -Value $markerText -Encoding utf8

$readmeText = @"
Handspan $Version - portable build for Windows
==============================================

A file manager and gallery for Android devices over ADB. Faster and more predictable than MTP,
with resumable transfers.

Getting started
---------------
1. Run Handspan.exe. Nothing needs installing; .NET is included.
2. Handspan needs Google's "adb" to talk to a phone. If it cannot find one already on this PC,
   the Home page offers to download platform-tools into this folder. Nothing is downloaded
   without you asking, and the app works offline afterwards.
3. On the phone: enable Developer options, turn on USB debugging, plug in the cable, and accept
   the authorization prompt. Handspan walks you through this on the Home page.

Portable behaviour
------------------
Everything Handspan writes goes into the "Data" folder beside this file - settings, the file
index, thumbnail caches, logs, and adb if you download it. Delete the "Handspan.portable" file
to switch to the normal per-user location instead. The Home page always shows the folder
actually in use.

If you extract this somewhere you cannot write to (Program Files, or a read-only stick),
Handspan still starts, but falls back to storing data in your user profile and says so on the
Home page.

Windows may warn about an unrecognized app the first time. This build is not code-signed yet.

Files
-----
Handspan.exe               the application
Handspan.portable          delete to disable portable mode
*.dll                      native libraries (Skia, HarfBuzz, SQLite) - keep them beside the exe
LICENSE                    MIT
THIRD-PARTY-NOTICES.md     licences of everything bundled

Source, issues and newer builds: https://github.com/HusseinTaha/handspan

Handspan is open source under the MIT licence. "Android" is a trademark of Google LLC;
Handspan is not affiliated with or endorsed by Google.
"@

Set-Content -Path (Join-Path $PublishDir 'README.txt') -Value $readmeText -Encoding utf8

foreach ($document in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $RepoRoot $document
    if (-not (Test-Path $source)) {
        throw "$document is missing from the repository root; a release must not ship without it"
    }
    Copy-Item $source (Join-Path $PublishDir $document) -Force
}

$archive = "$PublishDir.zip"
if (Test-Path $archive) { Remove-Item $archive -Force }

# bsdtar rather than Compress-Archive or ZipFile.CreateFromDirectory. Both of those write entry names
# with backslash separators when run on PowerShell 5.1 (.NET Framework), which Windows extractors
# tolerate but anything else does not — a Linux or macOS user unpacking this to prepare a stick would
# get one file with backslashes in its name. Compress-Archive is also minutes slower at this size.
# Naming the parent directory keeps exactly one top-level folder inside the zip.
Push-Location (Split-Path $PublishDir -Parent)
try {
    tar -a -c -f $archive (Split-Path $PublishDir -Leaf)
    if ($LASTEXITCODE -ne 0) { throw 'tar failed while building the portable archive' }
}
finally {
    Pop-Location
}

$size = (Get-Item $archive).Length / 1MB
Write-Host ("  portable zip: {0} ({1:N1} MB)" -f (Split-Path $archive -Leaf), $size) -ForegroundColor Green
