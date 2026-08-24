# Batch-build TAKXR Android APK when Unity Editor is installed.
# Usage:
#   .\ci\build-android.ps1
#   .\ci\build-android.ps1 -UnityExe "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe"
#   .\ci\build-android.ps1 -UseLocalMirror   # copy to C:\Temp\TAKXR-AndroidXR (recommended for network shares)

param(
    [string]$UnityExe = "",
    [string]$ProjectPath = "",
    [switch]$UseLocalMirror,
    [switch]$SkipCesium
)

$ErrorActionPreference = "Stop"
$SourcePath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Ensure Cesium tarball + Android SDK components when missing
& (Join-Path $PSScriptRoot "fetch-cesium.ps1") -ProjectPath $SourcePath
& (Join-Path $PSScriptRoot "ensure-android-sdk.ps1")

if (-not $UnityExe) {
    $candidates = Get-ChildItem "C:\Program Files\Unity\Hub\Editor" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "Editor\Unity.exe" } |
        Where-Object { Test-Path $_ }
    $UnityExe = $candidates | Select-Object -First 1
}

if (-not $UnityExe -or -not (Test-Path $UnityExe)) {
    Write-Error "Unity Editor not found. Install Unity 6000.3.x with Android Build Support, then re-run."
}

if ($UseLocalMirror -or -not $ProjectPath) {
    if ($UseLocalMirror -or $SourcePath -match '^[\\/]{2}|^T:') {
        $ProjectPath = "C:\Temp\TAKXR-AndroidXR"
        Write-Host "Mirroring $SourcePath -> $ProjectPath"
        New-Item -ItemType Directory -Force -Path (Split-Path $ProjectPath) | Out-Null
        # Keep Library for faster rebuilds; always refresh Assets (including .meta GUID links).
        robocopy $SourcePath $ProjectPath /E /XD Library Temp Obj Logs .git Builds /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
        # After a successful local Unity import, push .meta files back so T:/UNC stays GUID-stable.
        robocopy "$ProjectPath\Assets" "$SourcePath\Assets" *.meta /S /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    } else {
        $ProjectPath = $SourcePath
    }
}

if ($SkipCesium) {
    $manifest = Join-Path $ProjectPath "Packages\manifest.json"
    $text = Get-Content $manifest -Raw
    $text = $text -replace '\s*"com\.cesium\.unity"\s*:\s*"[^"]+"\s*,?', ''
    $text = $text -replace '"scopedRegistries"\s*:\s*\[[^\]]*\]\s*,?', ''
    Set-Content -Path $manifest -Value $text -Encoding UTF8
    Write-Host "Cesium dependency stripped for this build (fallback floor path)."
}

$log = Join-Path $ProjectPath "ci\unity-build.log"
New-Item -ItemType Directory -Force -Path (Join-Path $ProjectPath "ci") | Out-Null
Write-Host "Unity: $UnityExe"
Write-Host "Project: $ProjectPath"

# Start-Process -Wait: Unity.exe can return immediately on some Hub installs
# if invoked with the call operator; -Wait tracks the real editor process.
$unityArgs = @(
    "-batchmode", "-quit", "-nographics",
    "-projectPath", $ProjectPath,
    "-executeMethod", "TakXr.Editor.TakXrEditorMenu.BuildAndroidApk",
    "-logFile", $log
)
$proc = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -PassThru -WindowStyle Hidden
Write-Host "Unity PID $($proc.Id) - waiting for batch build..."
Wait-Process -Id $proc.Id
$exit = 0
if ($null -ne $proc.ExitCode) { $exit = $proc.ExitCode }

if ($exit -ne 0) {
    Write-Host "---- unity log (tail) ----"
    if (Test-Path $log) { Get-Content $log -Tail 120 }
    Write-Error "Unity build failed with exit $exit"
}

$apk = Join-Path $ProjectPath "Builds\Android\TAKXR.apk"
Write-Host "APK: $apk"
if (-not (Test-Path $apk)) { Write-Error "APK missing after build" }

# Copy APK back to source tree when using mirror
$destApk = Join-Path $SourcePath "Builds\Android\TAKXR.apk"
New-Item -ItemType Directory -Force -Path (Split-Path $destApk) | Out-Null
Copy-Item $apk $destApk -Force
Write-Host "Copied to $destApk"
