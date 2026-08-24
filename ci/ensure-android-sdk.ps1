# Ensure user Android SDK has components Unity 6000.3 needs for IL2CPP APK builds.
param(
    [string]$SdkRoot = (Join-Path $env:LOCALAPPDATA "Android\Sdk")
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $SdkRoot | Out-Null

function Ensure-CmdlineTools {
    $sdkmanager = Join-Path $SdkRoot "cmdline-tools\latest\bin\sdkmanager.bat"
    if (Test-Path $sdkmanager) { return $sdkmanager }

    $zip = "C:\Temp\commandlinetools-win.zip"
    $url = "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip"
    Write-Host "Downloading Android cmdline-tools..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $extract = "C:\Temp\cmdline-tools-extract"
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive $zip $extract -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $SdkRoot "cmdline-tools") | Out-Null
    $dest = Join-Path $SdkRoot "cmdline-tools\latest"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Move-Item (Join-Path $extract "cmdline-tools") $dest
    return (Join-Path $dest "bin\sdkmanager.bat")
}

function Ensure-Ndk {
    $ndk = Join-Path $SdkRoot "ndk\27.2.12479018"
    if (Test-Path (Join-Path $ndk "source.properties")) { return }
    $zip = "C:\Temp\android-ndk-r27c-windows.zip"
    $url = "https://dl.google.com/android/repository/android-ndk-r27c-windows.zip"
    Write-Host "Downloading Android NDK r27c..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $extract = "C:\Temp\android-ndk-extract"
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive $zip $extract -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $SdkRoot "ndk") | Out-Null
    if (Test-Path $ndk) { Remove-Item -Recurse -Force $ndk }
    Move-Item (Get-ChildItem $extract -Directory | Select-Object -First 1).FullName $ndk
}

$sdkmanager = Ensure-CmdlineTools
Ensure-Ndk

Write-Host "Installing SDK packages (cmdline-tools 16, build-tools 36, platforms, cmake)..."
& $sdkmanager --sdk_root="$SdkRoot" `
    "cmdline-tools;16.0" `
    "platform-tools" `
    "build-tools;36.0.0" `
    "platforms;android-34" `
    "platforms;android-35" `
    "cmake;3.22.1"

if (Test-Path (Join-Path $SdkRoot "cmdline-tools\16.0\bin\sdkmanager.bat")) {
    $latest = Join-Path $SdkRoot "cmdline-tools\latest"
    if (Test-Path $latest) { Remove-Item -Recurse -Force $latest }
    Copy-Item (Join-Path $SdkRoot "cmdline-tools\16.0") $latest -Recurse -Force
}

Write-Host "SDK ready at $SdkRoot"
