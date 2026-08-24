# Download Cesium for Unity release tarball into Packages/
param(
    [string]$Version = "1.24.0",
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$url = "https://github.com/CesiumGS/cesium-unity/releases/download/v$Version/com.cesium.unity-$Version.tgz"
$dest = Join-Path $ProjectPath "Packages\com.cesium.unity-$Version.tgz"
New-Item -ItemType Directory -Force -Path (Join-Path $ProjectPath "Packages") | Out-Null
if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 1MB) {
    Write-Host "Already present: $dest"
    exit 0
}
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
Write-Host "Saved $dest ($([math]::Round((Get-Item $dest).Length/1MB,1)) MB)"
