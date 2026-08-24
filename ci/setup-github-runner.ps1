# Register this Windows machine as a GitHub Actions self-hosted runner for Unity APK builds.
# Prerequisites: Unity 6000.3.x with Android Build Support + Android SDK (ci/ensure-android-sdk.ps1).
#
# Usage (PowerShell as your user, not admin):
#   .\ci\setup-github-runner.ps1
#   # paste the token from: https://github.com/CopIXus/AndroidXRTAK/settings/actions/runners/new
#
# Labels applied: self-hosted, Windows, Unity  (required by release-apk.yml)

param(
    [string]$Repo = "CopIXus/AndroidXRTAK",
    [string]$RunnerDir = "C:\actions-runner",
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"

if (-not $Token) {
    Write-Host @"
1. Open https://github.com/$Repo/settings/actions/runners/new
2. Click Windows → copy the config token
3. Re-run:
     .\ci\setup-github-runner.ps1 -Token <PASTE_TOKEN>
"@
    exit 1
}

New-Item -ItemType Directory -Force -Path $RunnerDir | Out-Null
Set-Location $RunnerDir

if (-not (Test-Path ".\config.cmd")) {
    $api = "https://api.github.com/repos/actions/runner/releases/latest"
    $rel = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "takxr-setup" }
    $asset = $rel.assets | Where-Object { $_.name -match 'win-x64-\d+\.\d+\.\d+\.zip$' } | Select-Object -First 1
    if (-not $asset) { throw "Could not find Windows runner zip in latest release" }
    $zip = Join-Path $RunnerDir $asset.name
    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $RunnerDir -Force
    Remove-Item $zip -Force
}

& .\config.cmd --url "https://github.com/$Repo" --token $Token `
    --name "$env:COMPUTERNAME-unity" `
    --labels "self-hosted,Windows,Unity" `
    --work "_work" `
    --unattended

Write-Host @"

Configured. Start the runner (keep this window open, or install as a service):

  cd $RunnerDir
  .\run.cmd

  # Optional — run as Windows service:
  # .\svc.cmd install
  # .\svc.cmd start

Then push a tag to publish a Release APK:
  git tag v0.1.2
  git push origin v0.1.2
"@
