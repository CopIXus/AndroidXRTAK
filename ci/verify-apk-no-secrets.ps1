param(
    [Parameter(Mandatory = $true)]
    [string]$ApkPath
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $ApkPath)) {
    Write-Error "APK not found: $ApkPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $ApkPath).Path)
try {
    foreach ($e in $zip.Entries) {
        if ($e.FullName -match '(?i)(^|/)takclient\.p12$|local-config\.json$|\.p12$') {
            Write-Error "APK contains secret asset: $($e.FullName)"
        }
    }
} finally {
    $zip.Dispose()
}

$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $ApkPath).Path)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
foreach ($p in @('tntak\.net', 'atakatak', '343jenkinshollow')) {
    if ($text -match $p) {
        Write-Error "APK contains forbidden pattern: $p"
    }
}

Write-Host "APK is safe for public release: $ApkPath"
exit 0
