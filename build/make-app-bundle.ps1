<#
.SYNOPSIS
    Wraps a published macOS build in an .app bundle.

.DESCRIPTION
    macOS will not treat a bare executable as an application: it needs the Contents/MacOS layout and an
    Info.plist. This produces that structure, which can be created on any OS — but signing and notarization
    must run on a Mac.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [string]$Version = '0.1.0',
    [string]$AppName = 'Android Explorer',

    # Distinguishes the two architectures. Without it both bundles land on the same path and the
    # second silently overwrites the first — which is exactly what happened the first time.
    [string]$Suffix = ''
)

$ErrorActionPreference = 'Stop'

$bundleName = if ($Suffix) { "$AppName ($Suffix).app" } else { "$AppName.app" }
$bundle = Join-Path (Split-Path $PublishDir -Parent) $bundleName
$contents = Join-Path $bundle 'Contents'
$macos = Join-Path $contents 'MacOS'
$resources = Join-Path $contents 'Resources'

if (Test-Path $bundle) {
    Remove-Item $bundle -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $macos, $resources | Out-Null
Copy-Item (Join-Path $PublishDir '*') $macos -Recurse -Force

# LSMinimumSystemVersion 12.0 matches Avalonia's own macOS floor.
# NSHighResolutionCapable keeps the UI from rendering blurred on Retina displays.
$plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$AppName</string>
    <key>CFBundleDisplayName</key>
    <string>$AppName</string>
    <key>CFBundleIdentifier</key>
    <string>com.androidexplorer.app</string>
    <key>CFBundleVersion</key>
    <string>$Version</string>
    <key>CFBundleShortVersionString</key>
    <string>$Version</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>AndroidExplorer</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Android Explorer</string>
</dict>
</plist>
"@

Set-Content -Path (Join-Path $contents 'Info.plist') -Value $plist -Encoding utf8

# Windows cannot set the Unix executable bit, and a zip does not carry one either — so the bundle will
# not launch until this is run on the Mac. Shipping the script beside it removes the guesswork.
$finish = @'
#!/bin/sh
# Prepares an Android Explorer bundle that was built on Windows.
#   ./finish-macos-build.sh "Android Explorer (Apple Silicon).app"
#   ./finish-macos-build.sh "Android Explorer (Intel).app"
set -e

BUNDLE="${1:-Android Explorer.app}"
BINARY="$BUNDLE/Contents/MacOS/AndroidExplorer"

if [ ! -d "$BUNDLE" ]; then
  echo "No such bundle: $BUNDLE" >&2
  exit 1
fi

# 1. The executable bit, which cannot survive a Windows build or a zip.
chmod +x "$BINARY"
find "$BUNDLE/Contents/MacOS" -name '*.dylib' -exec chmod +x {} \;

# 2. Strip the quarantine flag applied to anything downloaded.
xattr -dr com.apple.quarantine "$BUNDLE" 2>/dev/null || true

# 3. Ad-hoc signature. Apple Silicon refuses to run unsigned native code at all, so this is required
#    even for local use. It is NOT a substitute for a Developer ID signature plus notarization, which
#    is what lets anyone else run it without Gatekeeper objecting.
codesign --force --deep --sign - "$BUNDLE"

echo "Ready: $BUNDLE"
echo "For distribution to others you still need:"
echo "  codesign --force --deep --options runtime --sign \"Developer ID Application: NAME (TEAMID)\" \"$BUNDLE\""
echo "  xcrun notarytool submit --wait ...   then   xcrun stapler staple \"$BUNDLE\""
'@

$finishPath = Join-Path (Split-Path $PublishDir -Parent) 'finish-macos-build.sh'
# LF endings and no BOM: /bin/sh will not run a script with CRLF line endings.
[System.IO.File]::WriteAllText($finishPath, ($finish -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))

Write-Host "  bundle: $bundle"
