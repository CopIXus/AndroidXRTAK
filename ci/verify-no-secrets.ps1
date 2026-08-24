param(
    [string]$Root = ""
)

$ErrorActionPreference = "Stop"
if (-not $Root) { $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }

$patterns = @('tntak\.net', 'atakatak', '343jenkinshollow')
$secretExts = @('.p12', '.pem', '.crt', '.key')
$failed = $false

function Test-FileForSecrets([string]$fullPath, [string]$label) {
    $name = Split-Path $fullPath -Leaf
    if ($name -eq 'local-config.json') {
        Write-Error "Secret file must not be committed: $label"
        return $true
    }
    foreach ($ext in $secretExts) {
        if ($name -like "*$ext") {
            Write-Error "Secret extension must not be committed: $label"
            return $true
        }
    }
    if (-not (Test-Path $fullPath)) { return $false }
    $content = Get-Content $fullPath -Raw -ErrorAction SilentlyContinue
    if (-not $content) { return $false }
    foreach ($p in $patterns) {
        if ($content -match $p) {
            Write-Error "Forbidden pattern '$p' in $label"
            return $true
        }
    }
    return $false
}

Push-Location $Root
$files = @(git ls-files 2>$null)
Pop-Location

if ($files.Count -eq 0) {
    Write-Error "Not a git repo or no tracked files — cannot scan."
    exit 1
}

foreach ($rel in $files) {
    if ($rel -match 'verify-no-secrets\.ps1$' -or $rel -match 'local-config\.json\.example$') { continue }
    $full = Join-Path $Root ($rel -replace '/','\')
    if (Test-FileForSecrets $full $rel) { $failed = $true }
}

if ($failed) {
    Write-Host "Secret scan FAILED. Remove server credentials/certs before pushing."
    exit 1
}
Write-Host "Secret scan passed."
exit 0
