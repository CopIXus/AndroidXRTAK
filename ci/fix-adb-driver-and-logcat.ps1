# Run this script "as Administrator", then plug in the Galaxy XR with USB debugging on.
# Usage (elevated PowerShell):
#   .\ci\fix-adb-driver-and-logcat.ps1

$ErrorActionPreference = "Continue"
$sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
$adb = Join-Path $sdk "platform-tools\adb.exe"
$inf = Join-Path $sdk "extras\google\usb_driver\android_winusb.inf"
$log = Join-Path $env:USERPROFILE "Desktop\takxr-unity.log"

Write-Host "=== Install Google USB (ADB) driver ==="
if (Test-Path $inf) {
    pnputil /add-driver $inf /install
} else {
    Write-Host "Missing $inf — run ci\ensure-android-sdk.ps1 first, then sdkmanager extras;google;usb_driver"
}

Write-Host "`n=== Restart adb ==="
& $adb kill-server
Start-Sleep 1
& $adb start-server
& $adb devices -l

$devs = & $adb devices | Select-String "`tdevice$"
if (-not $devs) {
    Write-Host @"

NO DEVICE YET. On the Galaxy XR:
  1. Settings → About → tap Build number 7x (Developer options)
  2. Developer options → enable USB debugging (and Wireless debugging if you use that)
  3. Unplug/replug USB; accept 'Allow USB debugging?' when it appears
  4. In Windows Device Manager, find 'ADB Interface' (yellow bang) → Update driver
     → Browse → $inf

Then re-run this script.
"@
    exit 1
}

Write-Host "`n=== Capture Unity logcat (15s) ==="
& $adb logcat -c
& $adb shell am force-stop us.copix.takxr
& $adb shell am start -n us.copix.takxr/com.unity3d.player.UnityPlayerActivity
Start-Sleep 2
$job = Start-Job { param($a,$l) & $a logcat -v time -s Unity *:E | Out-File $l -Encoding utf8 } -ArgumentList $adb, $log
Start-Sleep 15
Stop-Job $job -ErrorAction SilentlyContinue
Receive-Job $job -ErrorAction SilentlyContinue | Out-Null
Remove-Job $job -Force -ErrorAction SilentlyContinue

# Also dump whatever is buffered
& $adb logcat -d -v time -s Unity *:E | Out-File $log -Encoding utf8
Write-Host "Wrote $log"
Get-Content $log -Tail 80
