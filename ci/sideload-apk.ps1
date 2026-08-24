# Sideload TAKXR.apk to a connected Android / Galaxy XR device via adb.
param(
    [string]$Apk = (Join-Path $PSScriptRoot "..\Builds\Android\TAKXR.apk"),
    [string]$Serial = ""
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Apk)) { Write-Error "APK not found: $Apk" }

$adb = $null
foreach ($c in @(
    "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    "$env:ANDROID_HOME\platform-tools\adb.exe",
    "C:\Program Files\Unity\Hub\Editor\*\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
)) {
    $hit = Get-Item $c -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $adb = $hit.FullName; break }
}
if (-not $adb) {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { $adb = $cmd.Source }
}
if (-not $adb) { Write-Error "adb not found. Install Android platform-tools or Unity Android module." }

Write-Host "Using adb: $adb"
& $adb devices
$installArgs = @("install", "-r", $Apk)
if ($Serial) { $installArgs = @("-s", $Serial) + $installArgs }
& $adb @installArgs
# Unity 6 uses GameActivity, not UnityPlayerActivity
$activity = "us.copix.takxr/com.unity3d.player.UnityPlayerGameActivity"
$startArgs = @("shell", "am", "start", "-n", $activity)
if ($Serial) { $startArgs = @("-s", $Serial) + $startArgs }
& $adb @startArgs
Write-Host "Launched $activity"
