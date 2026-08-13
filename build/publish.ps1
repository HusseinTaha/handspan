<#
.SYNOPSIS
    Publishes Handspan for one or more runtimes.

.DESCRIPTION
    Produces self-contained, single-file builds so a user needs no .NET install (spec: phase 8).

    Trimming is deliberately OFF. Avalonia resolves XAML types by reflection, and the trimmer removes them
    silently — the app builds, publishes, and then fails at runtime with a missing-type error that points
    nowhere useful. The size saving is not worth that class of bug.

.PARAMETER Portable
    Windows only. Produces a portable build: unzip anywhere and run, with settings, caches, logs and a
    downloaded adb kept in a Data folder beside the executable instead of in the user's profile. A marker
    file named Handspan.portable is what switches the app into that mode (see PortableMode.cs).

    Native libraries stay next to the exe rather than being bundled for self-extraction. A single-file
    build with IncludeNativeLibrariesForSelfExtract unpacks ~40 MB into %TEMP% on first run and leaves it
    there — which defeats the purpose of a build whose promise is that it writes nothing to the machine,
    and costs a noticeable pause on first launch. The managed assemblies are still bundled into the exe.

.EXAMPLE
    ./build/publish.ps1 -Runtime win-x64
    ./build/publish.ps1 -Runtime all -Version 0.4.0
    ./build/publish.ps1 -Runtime win-x64 -Portable -Version 0.6.0
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'all')]
    [string]$Runtime = 'win-x64',

    [string]$Version = '0.1.0',

    [switch]$Portable,

    [string]$OutputRoot = "$PSScriptRoot/../artifacts"
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '../src/Handspan.App/Handspan.App.csproj'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$runtimes = if ($Runtime -eq 'all') { @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64') } else { @($Runtime) }

if ($Portable) {
    $nonWindows = $runtimes | Where-Object { $_ -notlike 'win-*' }
    if ($nonWindows) {
        throw "-Portable applies to Windows runtimes only; got: $($nonWindows -join ', ')"
    }
}

foreach ($rid in $runtimes) {
    # The portable folder is named for what it is, because that name becomes the single top-level
    # directory inside the zip and therefore the folder the user ends up running the app from.
    $output = if ($Portable) {
        Join-Path $OutputRoot "Handspan-$Version-$rid-portable"
    } else {
        Join-Path $OutputRoot $rid
    }
    Write-Host "Publishing $rid -> $output" -ForegroundColor Cyan

    # A stale layout is worse here than elsewhere: leftover files from a previous run end up inside the
    # zip that gets handed to users.
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }

    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $output `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=$(if ($Portable) { 'false' } else { 'true' }) `
        -p:PublishTrimmed=false `
        -p:DebugType=embedded `
        -p:Version=$Version `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "publish failed for $rid"
    }

    if ($Portable) {
        & (Join-Path $PSScriptRoot 'make-portable.ps1') `
            -PublishDir $output -RepoRoot $repoRoot -Version $Version
    }

    if ($rid -like 'osx-*') {
        # The architecture is part of the bundle name, so publishing both does not overwrite one.
        $arch = if ($rid -eq 'osx-arm64') { 'Apple Silicon' } else { 'Intel' }
        & (Join-Path $PSScriptRoot 'make-app-bundle.ps1') `
            -PublishDir $output -Version $Version -Suffix $arch

        # Zip it so it can be moved to a Mac. Neither a zip nor a Windows build carries the Unix
        # executable bit, which is why finish-macos-build.sh travels inside the archive.
        #
        # The archive is named for the release rather than for the bundle: a download called
        # "Handspan (Apple Silicon).app.zip" carries no version, and GitHub rewrites the spaces in it.
        # The bundle keeps its readable name, because that is what shows up in Finder.
        $slug = if ($rid -eq 'osx-arm64') { 'apple-silicon' } else { 'intel' }
        $archive = Join-Path $OutputRoot "Handspan-$Version-macos-$slug.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }

        # bsdtar, NOT Compress-Archive. Compress-Archive on PowerShell 5.1 writes entry names with
        # BACKSLASH separators, and so does .NET Framework's ZipFile. Windows extractors tolerate that;
        # macOS unzip does not — it creates single files literally named
        # "Handspan (Apple Silicon).app\Contents\MacOS\Handspan" and the bundle never reassembles.
        # A .app is a directory, so this is the difference between a working download and a broken one.
        # bsdtar ships with Windows 10+ and writes forward slashes and Unix attributes.
        Push-Location $OutputRoot
        try {
            tar -a -c -f $archive "Handspan ($arch).app" 'finish-macos-build.sh'
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
        }
        finally {
            Pop-Location
        }

        Write-Host ("  archive: {0} ({1:N1} MB)" -f `
            (Split-Path $archive -Leaf), ((Get-Item $archive).Length / 1MB)) -ForegroundColor Green
    }

    $size = (Get-ChildItem $output -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  {0}: {1:N1} MB" -f $rid, ($size / 1MB)) -ForegroundColor Green
}

Write-Host ''
Write-Host 'Next steps, which cannot be automated here:' -ForegroundColor Yellow
Write-Host '  Windows: sign Handspan.exe with an Authenticode certificate.'
Write-Host '           Unsigned, SmartScreen will warn users away from a file-transfer tool.'
Write-Host '  macOS:   run finish-macos-build.sh on the Mac to set the executable bit and ad-hoc sign.'
Write-Host '           For anyone else to run it: codesign with a Developer ID, then notarize and staple.'
