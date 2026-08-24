# Install Unity 6000.3.20f1 + Android modules via Unity Hub headless.
$ErrorActionPreference = "Stop"
$hub = "C:\Program Files\Unity Hub\Unity Hub.exe"
if (-not (Test-Path $hub)) { Write-Error "Unity Hub not found at $hub" }

$version = "6000.3.20f1"
Write-Host "Installing Unity $version with Android modules (long download)..."
# Module ids vary by Hub release; prefer android + sdk tools + OpenJDK 17.
& $hub -- --headless install --version $version -m android -m android-sdk-ndk-tools -m "android-open-jdk-17.0.18+8" --childModules
if ($LASTEXITCODE -ne 0) {
  Write-Host "Retry with generic android-open-jdk module id..."
  & $hub -- --headless install --version $version -m android -m android-sdk-ndk-tools --childModules
}
Write-Host "Verify: & `"$hub`" -- --headless editors -i"
