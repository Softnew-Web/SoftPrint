$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$out = Join-Path $root "dist\checksums.sha256"
New-Item -ItemType Directory -Path (Join-Path $root "dist") -Force | Out-Null
if (Test-Path $out) { Remove-Item $out -Force }

$files = @()
$files += Get-ChildItem (Join-Path $root "dist") -Filter "*.zip" -File -ErrorAction SilentlyContinue
$setup = Join-Path $root "installer\Output\SoftPrint-Setup.exe"
if (Test-Path $setup) {
    $files += Get-Item $setup
}

if ($files.Count -lt 1) {
    Write-Host "Nenhum asset para checksum."
    exit 0
}

$lines = foreach ($f in ($files | Sort-Object Name -Unique)) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, $f.Name
}

[System.IO.File]::WriteAllLines($out, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Gerado: dist\checksums.sha256 ($($lines.Count) arquivos)"
Get-Content $out
