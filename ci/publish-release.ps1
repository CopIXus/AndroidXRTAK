# Build a public-safe APK (no gitignored cert/config) and publish to GitHub Releases.
# Requires: Unity 6000.3 + Android modules, gh CLI, logged-in GitHub account.
#
# Usage:
#   .\ci\publish-release.ps1 -Version 0.1.2
#   .\ci\publish-release.ps1 -Version 0.1.2 -SkipBuild   # upload existing Builds/Android/TAKXR.apk

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$tag = if ($Version -match '^v') { $Version } else { "v$Version" }
$apk = Join-Path $Root "Builds\Android\TAKXR.apk"

if (Test-Path "$Root\Assets\StreamingAssets\local-config.json") {
    Write-Error "Remove or rename Assets/StreamingAssets/local-config.json before a public release build."
}
if (Get-ChildItem "$Root\Assets\StreamingAssets" -Filter "*.p12" -ErrorAction SilentlyContinue) {
    Write-Error "Remove Assets/StreamingAssets/*.p12 before a public release build."
}

powershell -NoProfile -File (Join-Path $PSScriptRoot "verify-no-secrets.ps1") -Root $Root

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build-android.ps1") -ProjectPath $Root
}

if (-not (Test-Path $apk)) { Write-Error "APK missing: $apk" }
powershell -NoProfile -File (Join-Path $PSScriptRoot "verify-apk-no-secrets.ps1") -ApkPath $apk

gh release view $tag -R CopIXus/AndroidXRTAK 2>$null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag $apk --clobber -R CopIXus/AndroidXRTAK
} else {
    gh release create $tag $apk `
        -R CopIXus/AndroidXRTAK `
        --title "TAKXR $tag" `
        --notes "Samsung Galaxy XR APK (no enrolled cert). Add TAK server + import P12 in-headset."
}

Write-Host "Release: https://github.com/CopIXus/AndroidXRTAK/releases/tag/$tag"
