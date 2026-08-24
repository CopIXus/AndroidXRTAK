param(
    [string]$Dest = "Builds/Android/TAKXR.apk"
)

$ErrorActionPreference = "Stop"
$apk = Get-ChildItem -Path @("Builds", "build") -Recurse -Filter "*.apk" -ErrorAction SilentlyContinue |
    Sort-Object Length -Descending |
    Select-Object -First 1

if (-not $apk) {
    Write-Error "No APK found under Builds/ or build/"
}

$dir = Split-Path $Dest
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$destFull = (Join-Path (Get-Location) $Dest)
if ($apk.FullName -ne $destFull) {
    Copy-Item $apk.FullName $destFull -Force
}

Write-Host "APK at $Dest ($([math]::Round($apk.Length / 1MB, 1)) MB) from $($apk.FullName)"
exit 0
